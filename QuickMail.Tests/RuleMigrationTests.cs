using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The D1 "All accounts" → per-account rule migration (#333): each unscoped (null-AccountId) rule is
/// duplicated into one rule per NON-Graph account, Graph accounts get none, and for a Graph-only
/// profile the unscoped rule is dropped. Runs once in <see cref="RuleService.LoadRules"/>, is
/// idempotent, and persists atomically.
/// </summary>
public class RuleMigrationTests : IDisposable
{
    private readonly string _dir;

    public RuleMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"qm-rule-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class FixedAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public FixedAccountService(params AccountModel[] accounts) => _accounts = [.. accounts];
        public List<AccountModel> LoadAccounts() => _accounts;
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static AccountModel Imap(string name = "IMAP") => new() { Id = Guid.NewGuid(), AccountName = name, BackendKind = BackendKind.ImapSmtp };
    private static AccountModel Graph(string name = "M365") => new() { Id = Guid.NewGuid(), AccountName = name, BackendKind = BackendKind.MicrosoftGraph };

    /// <summary>Writes an initial rules.json via a plain RuleService (no account service = no migration).</summary>
    private void SeedRules(params MailRule[] rules)
        => new RuleService(new StubImapMailService(), new StubLocalStoreService(), _dir).SaveRules([.. rules]);

    private RuleService ServiceWith(params AccountModel[] accounts)
        => new(new StubImapMailService(), new StubLocalStoreService(), _dir, new FixedAccountService(accounts));

    private static MailRule Unscoped(string name) => new() { Name = name, AccountId = null, SubjectContains = "x", Action = RuleAction.MarkAsRead };
    private static MailRule Scoped(string name, Guid accountId) => new() { Name = name, AccountId = accountId, SubjectContains = "y", Action = RuleAction.MarkAsRead };

    // ── The migration ────────────────────────────────────────────────────────────

    [Fact]
    public void Unscoped_DuplicatedIntoEachNonGraphAccount_WithFreshIds()
    {
        var a = Imap("A");
        var b = Imap("B");
        SeedRules(Unscoped("Newsletters"));

        var rules = ServiceWith(a, b).LoadRules();

        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal("Newsletters", r.Name));
        Assert.Contains(rules, r => r.AccountId == a.Id);
        Assert.Contains(rules, r => r.AccountId == b.Id);
        Assert.Equal(2, rules.Select(r => r.Id).Distinct().Count());   // fresh, distinct ids
        Assert.DoesNotContain(rules, r => r.AccountId is null);
    }

    [Fact]
    public void GraphAccounts_GetNoCopies()
    {
        var imap = Imap("Home");
        var graph = Graph("Work");
        SeedRules(Unscoped("Global"));

        var rules = ServiceWith(imap, graph).LoadRules();

        var rule = Assert.Single(rules);
        Assert.Equal(imap.Id, rule.AccountId);   // only the IMAP account, never the Graph one
    }

    [Fact]
    public void GraphOnlyProfile_UnscopedRuleIsDropped()
    {
        var graph = Graph("Work");
        SeedRules(Unscoped("Legacy global"), Scoped("Kept", graph.Id));

        var rules = ServiceWith(graph).LoadRules();

        // The unscoped rule has no non-Graph target and is dropped; the already-scoped one survives.
        var kept = Assert.Single(rules);
        Assert.Equal("Kept", kept.Name);
        Assert.DoesNotContain(rules, r => r.AccountId is null);
    }

    [Fact]
    public void AlreadyScopedRules_AreLeftUntouched()
    {
        var a = Imap("A");
        SeedRules(Scoped("Existing", a.Id));

        var rules = ServiceWith(a, Imap("B")).LoadRules();

        var rule = Assert.Single(rules);
        Assert.Equal("Existing", rule.Name);
        Assert.Equal(a.Id, rule.AccountId);
    }

    [Fact]
    public void Migration_IsIdempotent_SecondLoadDoesNotDuplicateAgain()
    {
        var a = Imap("A");
        var b = Imap("B");
        SeedRules(Unscoped("Global"));

        // First service migrates: 1 unscoped → 2 scoped, persisted to disk.
        var first = ServiceWith(a, b).LoadRules();
        Assert.Equal(2, first.Count);

        // A brand-new service reads the migrated file — must not duplicate the already-scoped rules.
        var second = ServiceWith(a, b).LoadRules();
        Assert.Equal(2, second.Count);
        Assert.DoesNotContain(second, r => r.AccountId is null);
    }

    [Fact]
    public void Migration_PersistsToDisk()
    {
        var a = Imap("A");
        SeedRules(Unscoped("Global"));

        ServiceWith(a, Imap("B")).LoadRules();   // migrates + saves

        // A plain service (no account service, so no migration) reads the file straight back;
        // if the migration hadn't persisted, it would still see the null-AccountId rule.
        var reloaded = new RuleService(new StubImapMailService(), new StubLocalStoreService(), _dir).LoadRules();
        Assert.Equal(2, reloaded.Count);
        Assert.DoesNotContain(reloaded, r => r.AccountId is null);
    }

    [Fact]
    public void NoAccountService_LeavesUnscopedRulesUntouched()
    {
        // The test-only path: without an account context there's nothing to migrate against, so an
        // unscoped rule is preserved as-is rather than dropped.
        SeedRules(Unscoped("Global"));

        var rules = new RuleService(new StubImapMailService(), new StubLocalStoreService(), _dir).LoadRules();

        var rule = Assert.Single(rules);
        Assert.Null(rule.AccountId);
    }

    [Fact]
    public void MixedRules_OnlyUnscopedOnesAreExpanded()
    {
        var a = Imap("A");
        var b = Imap("B");
        SeedRules(Scoped("Pinned", a.Id), Unscoped("Global"));

        var rules = ServiceWith(a, b).LoadRules();

        // Pinned stays as one; Global becomes two (a + b) → 3 total, none unscoped.
        Assert.Equal(3, rules.Count);
        Assert.Single(rules, r => r.Name == "Pinned");
        Assert.Equal(2, rules.Count(r => r.Name == "Global"));
        Assert.DoesNotContain(rules, r => r.AccountId is null);
    }
}
