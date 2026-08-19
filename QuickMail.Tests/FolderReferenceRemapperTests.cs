using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #529 step 4 (§5.2): after an IMAP→Graph convert, folder references stored by IMAP name are rewritten
/// to the matching Graph folder id, or safely invalidated. Matching is exact-full → unique-leaf → else
/// unmatched (never guess between two same-named folders).
/// </summary>
public class FolderReferenceRemapperTests
{
    private static readonly Guid Acct = Guid.NewGuid();

    private static MailFolderModel GFolder(string id, string display, Guid? acct = null) =>
        new() { AccountId = acct ?? Acct, FullName = id, DisplayName = display };

    private static MailRule MoveRule(string name, string target, Guid? acct = null) =>
        new() { Name = name, AccountId = acct ?? Acct, Action = RuleAction.MoveToFolder, TargetFolder = target };

    // Well-known Graph folders for the account, by opaque id.
    private static List<MailFolderModel> Folders() =>
    [
        GFolder("id-inbox", "Inbox"),
        GFolder("id-archive", "Archive"),
        GFolder("id-priority", "Priority"),
    ];

    [Fact]
    public void MoveRule_ExactName_RewrittenToGraphId()
    {
        var rules = new List<MailRule> { MoveRule("File to Archive", "Archive") };
        var r = FolderReferenceRemapper.Remap(Acct, Folders(), rules, [], new ConfigModel());

        Assert.Equal("id-archive", rules[0].TargetFolder);
        Assert.True(rules[0].IsEnabled);
        Assert.Contains("File to Archive", r.RemappedRules);
    }

    [Fact]
    public void MoveRule_NestedPath_MatchedByUniqueLeaf()
    {
        var rules = new List<MailRule> { MoveRule("File", "INBOX/Priority") };
        FolderReferenceRemapper.Remap(Acct, Folders(), rules, [], new ConfigModel());

        Assert.Equal("id-priority", rules[0].TargetFolder);   // leaf "Priority" resolved uniquely
    }

    [Fact]
    public void MoveRule_AmbiguousLeaf_LeftUnmatched_AndDisabled()
    {
        // Two Graph folders named "Reports" — a leaf match must NOT guess between them.
        var folders = new List<MailFolderModel>
        {
            GFolder("id-work-reports", "Reports"),
            GFolder("id-home-reports", "Reports"),
        };
        var rules = new List<MailRule> { MoveRule("File to Reports", "Work/Reports") };

        var r = FolderReferenceRemapper.Remap(Acct, folders, rules, [], new ConfigModel());

        Assert.Equal("Work/Reports", rules[0].TargetFolder);   // unchanged
        Assert.False(rules[0].IsEnabled);                      // disabled, not deleted
        Assert.Contains("File to Reports", r.DisabledRules);
    }

    [Fact]
    public void MoveRule_NoMatch_Disabled()
    {
        var rules = new List<MailRule> { MoveRule("File to Ghost", "NoSuchFolder") };
        var r = FolderReferenceRemapper.Remap(Acct, Folders(), rules, [], new ConfigModel());

        Assert.False(rules[0].IsEnabled);
        Assert.Contains("File to Ghost", r.DisabledRules);
    }

    [Fact]
    public void OtherAccountsRules_AndNonMoveRules_Untouched()
    {
        var otherAcct = Guid.NewGuid();
        var rules = new List<MailRule>
        {
            MoveRule("Other account", "Archive", otherAcct),
            new() { Name = "Mark read", AccountId = Acct, Action = RuleAction.MarkAsRead },
        };

        FolderReferenceRemapper.Remap(Acct, Folders(), rules, [], new ConfigModel());

        Assert.Equal("Archive", rules[0].TargetFolder);   // other account untouched (still an IMAP name)
        Assert.True(rules[0].IsEnabled);
        Assert.True(rules[1].IsEnabled);                  // non-move rule untouched
    }

    [Fact]
    public void SavedView_RealFolder_Remapped_UnmatchedDropped()
    {
        var view = new SavedView
        {
            Name = "My view",
            Folders =
            [
                new ViewFolder { AccountId = Acct, FolderFullName = "Archive" },
                new ViewFolder { AccountId = Acct, FolderFullName = "Gone" },
            ],
        };
        var views = new List<SavedView> { view };

        var r = FolderReferenceRemapper.Remap(Acct, Folders(), [], views, new ConfigModel());

        Assert.Single(view.Folders);                            // the unmatched one dropped
        Assert.Equal("id-archive", view.Folders[0].FolderFullName);
        Assert.Contains("My view", r.AffectedViews);
    }

    [Fact]
    public void VirtualFolderView_Untouched()
    {
        var view = new SavedView { Name = "All Inboxes", VirtualFolderKey = "AllInboxes" };
        var views = new List<SavedView> { view };

        var r = FolderReferenceRemapper.Remap(Acct, Folders(), [], views, new ConfigModel());

        Assert.Empty(r.AffectedViews);
        Assert.Equal("AllInboxes", view.VirtualFolderKey);
    }

    [Fact]
    public void StartupFolder_Remapped_WhenMatched()
    {
        var config = new ConfigModel
        {
            StartupFolder = "Archive",
            StartupFolderAccount = Acct.ToString(),
            StartupFolderLabel = "Archive",
        };

        var r = FolderReferenceRemapper.Remap(Acct, Folders(), [], [], config);

        Assert.Equal("id-archive", config.StartupFolder);
        Assert.True(r.StartupFolderChanged);
    }

    [Fact]
    public void StartupFolder_AlreadyGraphId_NotReportedChanged() // idempotent re-run (crash between save and marker-clear)
    {
        var config = new ConfigModel
        {
            StartupFolder = "id-archive",                 // already the Graph id from a prior pass
            StartupFolderAccount = Acct.ToString(),
            StartupFolderLabel = "Archive",
        };

        var r = FolderReferenceRemapper.Remap(Acct, Folders(), [], [], config);

        Assert.Equal("id-archive", config.StartupFolder);  // unchanged
        Assert.False(r.StartupFolderChanged);              // and not re-announced
    }

    [Fact]
    public void StartupFolder_FallsBackToAllMail_WhenUnmatched()
    {
        var config = new ConfigModel
        {
            StartupFolder = "Gone",
            StartupFolderAccount = Acct.ToString(),
            StartupFolderLabel = "Gone",
        };

        FolderReferenceRemapper.Remap(Acct, Folders(), [], [], config);

        Assert.Equal(string.Empty, config.StartupFolder);          // fell back
        Assert.Equal(string.Empty, config.StartupFolderAccount);
    }

    [Fact]
    public void StartupFolder_OtherAccount_Untouched()
    {
        var config = new ConfigModel
        {
            StartupFolder = "Archive",
            StartupFolderAccount = Guid.NewGuid().ToString(),   // a different account
        };

        var r = FolderReferenceRemapper.Remap(Acct, Folders(), [], [], config);

        Assert.Equal("Archive", config.StartupFolder);
        Assert.False(r.StartupFolderChanged);
    }

    [Fact]
    public void Remap_IsIdempotent_ReRunKeepsRemappedRules() // a crash between save and marker-clear re-runs this
    {
        var rules = new List<MailRule> { MoveRule("File to Archive", "Archive") };
        FolderReferenceRemapper.Remap(Acct, Folders(), rules, [], new ConfigModel());
        Assert.Equal("id-archive", rules[0].TargetFolder);   // remapped to the Graph id

        // Second run over the already-Graph-id reference must NOT disable the rule.
        var r2 = FolderReferenceRemapper.Remap(Acct, Folders(), rules, [], new ConfigModel());

        Assert.Equal("id-archive", rules[0].TargetFolder);
        Assert.True(rules[0].IsEnabled);
        Assert.Empty(r2.DisabledRules);
    }

    [Fact]
    public void Summary_NamesTheDisabledRules_AndIsEmptyWhenNothingChanged()
    {
        Assert.Equal(string.Empty, new FolderReferenceRemapper.Report().Summary());

        var rules = new List<MailRule> { MoveRule("Broken", "Gone"), MoveRule("Fine", "Archive") };
        var r = FolderReferenceRemapper.Remap(Acct, Folders(), rules, [], new ConfigModel());

        Assert.Contains("Broken", r.Summary());       // the disabled rule is named (actionable)
        Assert.Contains("updated", r.Summary());      // the remapped one is counted
    }
}
