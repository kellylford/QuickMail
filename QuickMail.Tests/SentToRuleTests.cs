using System;
using System.IO;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// A client rule's "Sent to" condition: who a message was sent to. It matches the To and the Cc line, as Outlook's
/// "Sent to" does and so a Microsoft server rule's <c>sentToAddresses</c>, so one rule means the same whichever side
/// runs it. A whole address or domain matches on address boundaries, as the sender conditions do; a name or a
/// fragment is still text. It sat in the editor's collapsed Advanced section, where it read as missing; it is now
/// beside the From conditions, and a rule using it no longer opens Advanced on edit.
/// </summary>
public class SentToRuleTests
{
    private static MailMessageSummary Msg(string to = "", string cc = "") => new()
    {
        MessageId = "1",
        AccountId = Guid.NewGuid(),
        FolderName = "INBOX",
        From = "Sender",
        FromEmail = "sender@example.com",
        To = to,
        Cc = cc,
        Subject = "Subject",
        Preview = "Preview",
        Date = DateTimeOffset.Now,
    };

    private static MailRule Rule(params string[] addresses) => new()
    {
        Name = "Sent to",
        UseFromCondition = false,
        UseSubjectCondition = false,
        UseBodyCondition = false,
        UseToCondition = true,
        ToContains = addresses[0],
        SentToAddresses = addresses.Length > 1 ? [.. addresses] : null,
        Action = RuleAction.MarkAsRead,
    };

    private static bool Matches(MailRule rule, MailMessageSummary msg)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = new RuleService(new StubImapMailService(), new StubLocalStoreService(), dir);
            return svc.TestRule(rule, [msg]).Count == 1;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── Matching ────────────────────────────────────────────────────────────

    [Fact]
    public void MatchesTheToLine()
        => Assert.True(Matches(Rule("me@work.com"), Msg(to: "Me <me@work.com>")));

    [Fact]
    public void MatchesTheCcLine()
    {
        // The gap: before, only the To line was read, so mail that copied you never matched.
        Assert.True(Matches(Rule("me@work.com"), Msg(to: "Boss <boss@work.com>", cc: "Me <me@work.com>, Other <o@work.com>")));
    }

    [Fact]
    public void DoesNotMatch_WhenNeitherLineNamesTheAddress()
        => Assert.False(Matches(Rule("me@work.com"), Msg(to: "boss@work.com", cc: "other@work.com")));

    [Fact]
    public void AnyOfSeveralAddresses_OnEitherLine()
    {
        var rule = Rule("dev@list.org", "ops@list.org");
        Assert.True(Matches(rule, Msg(to: "x@y.com", cc: "ops@list.org")));
        Assert.True(Matches(rule, Msg(to: "dev@list.org")));
        Assert.False(Matches(rule, Msg(to: "sales@list.org", cc: "x@y.com")));
    }

    [Theory]
    // A whole address matches on boundaries: not inside a longer one, on either line.
    [InlineData("bob@example.com", "jimbob@example.com", "", false)]
    [InlineData("bob@example.com", "", "jimbob@example.com", false)]
    [InlineData("bob@example.com", "Bob <bob@example.com>", "", true)]
    [InlineData("bob@example.com", "", "\"Smith, Bob\" <BOB@example.com>", true)]
    // A domain matches its own addresses, not a longer domain that starts the same.
    [InlineData("@example.com", "a@example.com", "", true)]
    [InlineData("@example.com", "a@example.com.evil.test", "", false)]
    [InlineData("@example.com", "", "a@example.com.evil.test", false)]
    // A name, or a fragment, is still text either line contains.
    [InlineData("Team", "", "Team Leads <leads@x.com>", true)]
    [InlineData("@exam", "a@example.com", "", true)]
    // A "Name <address>" entry matches on its address.
    [InlineData("Me <me@work.com>", "", "me@work.com", true)]
    public void AddressesMatchWhole_NamesAsText(string value, string to, string cc, bool expected)
        => Assert.Equal(expected, RuleService.RecipientMatches(Msg(to: to, cc: cc), value));

    [Fact]
    public void IsAndedWithTheOtherConditions()
    {
        var rule = Rule("me@work.com");
        rule.UseSubjectCondition = true;
        rule.SubjectContains = "invoice";

        var msg = Msg(cc: "me@work.com");
        msg.Subject = "Lunch";
        Assert.False(Matches(rule, msg));
        msg.Subject = "Invoice 42";
        Assert.True(Matches(rule, msg));
    }

    [Fact]
    public void ARowWithNoCcYet_StillMatchesOnTo()
    {
        // A row cached before the Cc column has an empty Cc until it syncs again; To alone still works.
        Assert.True(Matches(Rule("me@work.com"), Msg(to: "me@work.com", cc: "")));
    }

    // ── Rules list wording ──────────────────────────────────────────────────

    [Fact]
    public void Row_SaysSentTo_ForAWholeAddress()
    {
        var row = UnifiedRuleRow.ForClient(Rule("me@work.com"));
        Assert.Contains("If sent to 'me@work.com' →", row.RowText, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_SaysToOrCcContains_ForANameOrFragment()
    {
        var row = UnifiedRuleRow.ForClient(Rule("Team"));
        Assert.Contains("If to or cc contains 'Team' →", row.RowText, StringComparison.Ordinal);
    }

    // ── Editor ──────────────────────────────────────────────────────────────

    [Fact]
    public void EditingASentToRule_DoesNotOpenAdvanced_SinceTheFieldIsNoLongerThere()
    {
        var vm = ServerRuleEditorViewModel.ForEditClient(Rule("me@work.com"));
        Assert.True(vm.UseSentToAddresses);
        Assert.Equal("me@work.com", vm.SentToAddresses);
        Assert.False(vm.IsAdvancedExpanded);
    }

    // ── Every backend brings the Cc line ────────────────────────────────────

    [Fact]
    public void Pop3Summary_CarriesCc()
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        msg.To.Add(new MailboxAddress("Boss", "boss@work.com"));
        msg.Cc.Add(new MailboxAddress("Me", "me@work.com"));
        msg.Subject = "Hi";
        msg.Body = new TextPart("plain") { Text = "Body" };

        var summary = Pop3MailService.BuildSummary(Guid.NewGuid(), "u1", msg, "Inbox", isRead: false);

        Assert.Contains("me@work.com", summary.Cc, StringComparison.Ordinal);
        Assert.DoesNotContain("me@work.com", summary.To, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalStore_RoundTripsCc_AndAnEmptyCcNeverErasesIt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStoreService(new ProfileContext(tempDir));
            store.Initialize();
            var summary = Msg(to: "boss@work.com", cc: "Me <me@work.com>");

            await store.UpsertSummariesAsync([summary]);
            Assert.Equal("Me <me@work.com>", Assert.Single(await store.LoadAllSummariesAsync()).Cc);

            // A path that re-caches a row without its Cc (a flag or read-state refresh) must not blank it.
            summary.Cc = string.Empty;
            await store.UpsertSummariesAsync([summary]);
            Assert.Equal("Me <me@work.com>", Assert.Single(await store.LoadAllSummariesAsync()).Cc);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* temp */ }
        }
    }
}
