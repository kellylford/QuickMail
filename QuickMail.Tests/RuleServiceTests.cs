using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class RuleServiceTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static MailMessageSummary MakeMsg(
        string from = "alice@example.com",
        string to = "bob@example.com",
        string subject = "Test Subject",
        string preview = "Hello world",
        bool hasAttachments = false,
        bool isRead = false,
        Guid? accountId = null,
        string folderName = "INBOX",
        uint uid = 1)
    {
        return new MailMessageSummary
        {
            UniqueId = uid,
            AccountId = accountId ?? Guid.NewGuid(),
            FolderName = folderName,
            From = from,
            To = to,
            Subject = subject,
            Preview = preview,
            HasAttachments = hasAttachments,
            IsRead = isRead,
            Date = DateTimeOffset.Now,
        };
    }

    private static RuleService CreateService(string dataDir)
    {
        return new RuleService(new StubImapMailService(), new StubLocalStoreService(), dataDir);
    }

    // ── Load / Save tests ───────────────────────────────────────────────────

    [Fact]
    public void LoadRules_EmptyFile_ReturnsEmptyList()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            var rules = svc.LoadRules();
            Assert.NotNull(rules);
            Assert.Empty(rules);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadRules_CorruptedFile_ReturnsEmptyList()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "rules.json"), "this is not json {{{");

            var svc = CreateService(dir);
            var rules = svc.LoadRules();
            Assert.NotNull(rules);
            Assert.Empty(rules);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SaveRules_RoundTrip_PreservesAllFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            var original = new List<MailRule>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Test Rule",
                    IsEnabled = true,
                    FromContains = "boss@company.com",
                    ToContains = null,
                    SubjectContains = "URGENT",
                    BodyContains = "deadline",
                    MustHaveAttachments = true,
                    AccountId = Guid.NewGuid(),
                    Action = RuleAction.MoveToFolder,
                    TargetFolder = "INBOX/Priority",
                },
                new()
                {
                    Name = "Disabled Rule",
                    IsEnabled = false,
                    Action = RuleAction.MarkAsRead,
                },
            };

            svc.SaveRules(original);
            var loaded = svc.LoadRules();

            Assert.Equal(2, loaded.Count);
            Assert.Equal(original[0].Id, loaded[0].Id);
            Assert.Equal(original[0].Name, loaded[0].Name);
            Assert.Equal(original[0].IsEnabled, loaded[0].IsEnabled);
            Assert.Equal(original[0].FromContains, loaded[0].FromContains);
            Assert.Null(loaded[0].ToContains);
            Assert.Equal(original[0].SubjectContains, loaded[0].SubjectContains);
            Assert.Equal(original[0].BodyContains, loaded[0].BodyContains);
            Assert.Equal(original[0].MustHaveAttachments, loaded[0].MustHaveAttachments);
            Assert.Equal(original[0].AccountId, loaded[0].AccountId);
            Assert.Equal(original[0].Action, loaded[0].Action);
            Assert.Equal(original[0].TargetFolder, loaded[0].TargetFolder);

            Assert.False(loaded[1].IsEnabled);
            Assert.Equal(RuleAction.MarkAsRead, loaded[1].Action);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SaveRules_LoadRules_CachesResult()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            var rules = new List<MailRule> { new() { Name = "Cached" } };
            svc.SaveRules(rules);

            var first = svc.LoadRules();
            var second = svc.LoadRules();

            Assert.Same(first, second); // cached — same reference
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── TestRule — condition matching ───────────────────────────────────────

    [Fact]
    public void TestRule_FromContains_MatchesSubstring()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule { FromContains = "alice" };
        var msg = MakeMsg(from: "Alice Smith <alice@example.com>");

        var result = svc.TestRule(rule, new[] { msg });
        Assert.Single(result);
    }

    [Fact]
    public void TestRule_FromContains_NoMatch_ReturnsEmpty()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule { FromContains = "bob" };
        var msg = MakeMsg(from: "Alice Smith <alice@example.com>");

        var result = svc.TestRule(rule, new[] { msg });
        Assert.Empty(result);
    }

    [Fact]
    public void TestRule_ToContains_MatchesSubstring()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule { ToContains = "team" };
        var msg = MakeMsg(to: "team@company.com, alice@company.com");

        var result = svc.TestRule(rule, new[] { msg });
        Assert.Single(result);
    }

    [Fact]
    public void TestRule_SubjectContains_MatchesSubstring()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule { SubjectContains = "weekly" };
        var msg = MakeMsg(subject: "Weekly Report — May 2026");

        var result = svc.TestRule(rule, new[] { msg });
        Assert.Single(result);
    }

    [Fact]
    public void TestRule_BodyContains_MatchesPreview()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule { BodyContains = "unsubscribe" };
        var msg = MakeMsg(preview: "Click here to unsubscribe from this newsletter.");

        var result = svc.TestRule(rule, new[] { msg });
        Assert.Single(result);
    }

    [Fact]
    public void TestRule_BodyContains_NullPreview_NoMatch()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule { BodyContains = "text" };
        var msg = MakeMsg(preview: null!);

        var result = svc.TestRule(rule, new[] { msg });
        Assert.Empty(result);
    }

    [Fact]
    public void TestRule_HasAttachments_MatchesOnlyWhenTrue()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule { MustHaveAttachments = true };
        var withAtt = MakeMsg(hasAttachments: true, uid: 1);
        var withoutAtt = MakeMsg(hasAttachments: false, uid: 2);

        var result = svc.TestRule(rule, new[] { withAtt, withoutAtt });
        Assert.Single(result);
        Assert.Equal(1u, result[0].UniqueId);
    }

    [Fact]
    public void TestRule_AllConditionsAnded_RequiresAllMatch()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule
        {
            FromContains = "alice",
            SubjectContains = "report",
            MustHaveAttachments = true,
        };
        var matchAll = MakeMsg(from: "alice@example.com", subject: "Weekly Report", hasAttachments: true, uid: 1);
        var matchSome = MakeMsg(from: "alice@example.com", subject: "Weekly Report", hasAttachments: false, uid: 2);

        var result = svc.TestRule(rule, new[] { matchAll, matchSome });
        Assert.Single(result);
        Assert.Equal(1u, result[0].UniqueId);
    }

    [Fact]
    public void TestRule_EmptyConditions_MatchesEverything()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule(); // no conditions set
        var msgs = new[] { MakeMsg(uid: 1), MakeMsg(uid: 2), MakeMsg(uid: 3) };

        var result = svc.TestRule(rule, msgs);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void TestRule_CaseInsensitive_Matching()
    {
        var svc = CreateService(Path.GetTempPath());
        var rule = new MailRule { FromContains = "ALICE" };
        var msg = MakeMsg(from: "alice@example.com");

        var result = svc.TestRule(rule, new[] { msg });
        Assert.Single(result);
    }

    // ── ApplyRulesAsync tests ───────────────────────────────────────────────

    [Fact]
    public async Task ApplyRulesAsync_DisabledRule_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            svc.SaveRules(new List<MailRule>
            {
                new() { Name = "Disabled", IsEnabled = false, FromContains = "alice", Action = RuleAction.MarkAsRead },
            });

            var accountId = Guid.NewGuid();
            var msgs = new List<MailMessageSummary> { MakeMsg(from: "alice@example.com", accountId: accountId) };

            var (count, _) = await svc.ApplyRulesAsync(msgs, accountId, CancellationToken.None);
            Assert.Equal(0, count);
            Assert.False(msgs[0].IsRead); // not changed
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplyRulesAsync_WrongAccount_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            var ruleAccountId = Guid.NewGuid();
            svc.SaveRules(new List<MailRule>
            {
                new() { Name = "Account Scoped", FromContains = "alice", AccountId = ruleAccountId, Action = RuleAction.MarkAsRead },
            });

            var otherAccountId = Guid.NewGuid();
            var msgs = new List<MailMessageSummary> { MakeMsg(from: "alice@example.com", accountId: otherAccountId) };

            var (count, _) = await svc.ApplyRulesAsync(msgs, otherAccountId, CancellationToken.None);
            Assert.Equal(0, count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplyRulesAsync_MarkAsRead_UpdatesIsRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            svc.SaveRules(new List<MailRule>
            {
                new() { Name = "Mark Read", FromContains = "alice", Action = RuleAction.MarkAsRead },
            });

            var accountId = Guid.NewGuid();
            var msgs = new List<MailMessageSummary> { MakeMsg(from: "alice@example.com", accountId: accountId, isRead: false) };

            var (count, _) = await svc.ApplyRulesAsync(msgs, accountId, CancellationToken.None);
            Assert.Equal(1, count);
            Assert.True(msgs[0].IsRead);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplyRulesAsync_MarkAsUnread_UpdatesIsRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            svc.SaveRules(new List<MailRule>
            {
                new() { Name = "Mark Unread", FromContains = "alice", Action = RuleAction.MarkAsUnread },
            });

            var accountId = Guid.NewGuid();
            var msgs = new List<MailMessageSummary> { MakeMsg(from: "alice@example.com", accountId: accountId, isRead: true) };

            var (count, _) = await svc.ApplyRulesAsync(msgs, accountId, CancellationToken.None);
            Assert.Equal(1, count);
            Assert.False(msgs[0].IsRead);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplyRulesAsync_ReturnsMatchCount()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            svc.SaveRules(new List<MailRule>
            {
                new() { Name = "Rule 1", FromContains = "alice", Action = RuleAction.MarkAsRead },
                new() { Name = "Rule 2", SubjectContains = "news", Action = RuleAction.MarkAsRead },
            });

            var accountId = Guid.NewGuid();
            var msgs = new List<MailMessageSummary>
            {
                MakeMsg(from: "alice@example.com", uid: 1, accountId: accountId),
                MakeMsg(from: "bob@example.com", subject: "Daily News", uid: 2, accountId: accountId),
                MakeMsg(from: "charlie@example.com", uid: 3, accountId: accountId),
            };

            var (count, _) = await svc.ApplyRulesAsync(msgs, accountId, CancellationToken.None);
            Assert.Equal(2, count); // msg 1 matches rule 1, msg 2 matches rule 2
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplyRulesAsync_NoEnabledRules_ReturnsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            svc.SaveRules(new List<MailRule>()); // empty

            var accountId = Guid.NewGuid();
            var msgs = new List<MailMessageSummary> { MakeMsg(accountId: accountId) };

            var (count, _) = await svc.ApplyRulesAsync(msgs, accountId, CancellationToken.None);
            Assert.Equal(0, count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplyRulesAsync_Cancellation_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            svc.SaveRules(new List<MailRule>
            {
                new() { Name = "Rule", FromContains = "alice", Action = RuleAction.MarkAsRead },
            });

            var accountId = Guid.NewGuid();
            var msgs = new List<MailMessageSummary> { MakeMsg(from: "alice@example.com", accountId: accountId) };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => svc.ApplyRulesAsync(msgs, accountId, cts.Token));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplyRulesAsync_AccountIdNull_AppliesToAllAccounts()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = CreateService(dir);
            svc.SaveRules(new List<MailRule>
            {
                new() { Name = "All Accounts", FromContains = "alice", AccountId = null, Action = RuleAction.MarkAsRead },
            });

            var accountId = Guid.NewGuid();
            var msgs = new List<MailMessageSummary> { MakeMsg(from: "alice@example.com", accountId: accountId) };

            var (count, _) = await svc.ApplyRulesAsync(msgs, accountId, CancellationToken.None);
            Assert.Equal(1, count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
