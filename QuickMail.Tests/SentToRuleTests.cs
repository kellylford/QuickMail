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
    // A whole address is looked for in the real addresses, not in a display name that happens to hold it.
    [InlineData("me@work.com", "\"me@work.com\" <list@lists.example>", "", false)]
    [InlineData("me@work.com", "\"me@work.com\" <list@lists.example>, Me <me@work.com>", "", true)]
    // Several recipients on one line, one of them the address.
    [InlineData("me@work.com", "", "A <a@x.com>, \"Ford, Kelly\" <me@work.com>, b@y.com", true)]
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

    // ── Create Rule from Message ────────────────────────────────────────────

    private static MailMessageSummary FromMessage(string to, string cc) => new()
    {
        MessageId = "1",
        AccountId = Guid.NewGuid(),
        FolderName = "INBOX",
        From = "Alice",
        FromEmail = "alice@example.com",
        To = to,
        Cc = cc,
        Subject = "Status",
        Date = DateTimeOffset.Now,
    };

    [Fact]
    public void CreateRuleFromMessage_FillsSentToWithTheRecipients_SwitchedOff()
    {
        // Kelly's report on build 2: the message's recipients were not offered at all. They are now, To then Cc,
        // addresses only, each once; switched off, so the rule is still "mail from Alice" until turned on.
        var template = MainViewModel.RuleTemplateFrom(FromMessage(
            to: "Me <me@work.com>, \"Lists, Dev\" <dev@list.org>",
            cc: "ME@work.com, Boss <boss@work.com>"));
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(template);

        Assert.Equal("me@work.com, dev@list.org, boss@work.com", vm.SentToAddresses);
        Assert.False(vm.UseSentToAddresses);
        Assert.True(vm.UseFromAddresses);
        Assert.Empty(template.ToValues());   // switched off: the template's rule tests nothing on recipients
        Assert.False(vm.IsAdvancedExpanded);
    }

    [Fact]
    public void CreateRuleFromMessage_ASingleRecipient_AndTurningItOn_SavesASentToRule()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(
            MainViewModel.RuleTemplateFrom(FromMessage(to: "me@work.com", cc: "")));
        Assert.Equal("me@work.com", vm.SentToAddresses);

        vm.UseFromAddresses = false;
        vm.UseSentToAddresses = true;
        Assert.True(Matches(vm.ToClientRule(Guid.NewGuid()),Msg(to: "x@y.com", cc: "me@work.com")));
    }

    [Fact]
    public void CreateRuleFromMessage_PiecesOfAnUnquotedName_AreNotTakenForRecipients()
    {
        // A Graph row writes "Ford, Kelly <k@x>" without quotes; the "Ford" before the comma is not an address.
        var template = MainViewModel.RuleTemplateFrom(FromMessage(to: "Ford, Kelly <kelly@work.com>", cc: ""));
        Assert.Equal("kelly@work.com", ServerRuleEditorViewModel.ForNewFromTemplate(template).SentToAddresses);
    }

    [Fact]
    public void CreateRuleFromMessage_NoRecipients_LeavesSentToEmpty()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(MainViewModel.RuleTemplateFrom(FromMessage(to: "", cc: "")));
        Assert.Equal(string.Empty, vm.SentToAddresses);
        Assert.False(vm.UseSentToAddresses);
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

            // Defensive: no current path re-caches a row without its Cc, but one that did must not blank it.
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

    [Fact]
    public async Task UpgradingAStore_FillsCcFromCachedBodies_Once()
    {
        // A store from before the column: rows with a cached body take its Cc, rows without stay empty, and the
        // backfill never runs again once the column is there.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var account = Guid.NewGuid().ToString();
        try
        {
            new LocalStoreService(new ProfileContext(tempDir)).Initialize();
            var dbPath = Path.Combine(tempDir, "mail.db");
            Exec(dbPath, "ALTER TABLE MessageSummary DROP COLUMN cc_addr;");
            Exec(dbPath, $"""
                INSERT INTO MessageSummary(unique_id, account_id, folder_name, date_ticks) VALUES('1', '{account}', 'INBOX', 1);
                INSERT INTO MessageSummary(unique_id, account_id, folder_name, date_ticks) VALUES('2', '{account}', 'INBOX', 2);
                INSERT INTO MessageDetail(unique_id, account_id, folder_name, cc) VALUES('1', '{account}', 'INBOX', 'Me <me@work.com>');
                """);

            var store = new LocalStoreService(new ProfileContext(tempDir));
            store.Initialize();
            var rows = await store.LoadAllSummariesAsync();
            Assert.Equal("Me <me@work.com>", rows.Single(r => r.MessageId == "1").Cc);
            Assert.Equal(string.Empty, rows.Single(r => r.MessageId == "2").Cc);

            Exec(dbPath, "UPDATE MessageSummary SET cc_addr = 'changed@work.com' WHERE unique_id = '1';");
            new LocalStoreService(new ProfileContext(tempDir)).Initialize();
            Assert.Equal("changed@work.com",
                (await store.LoadAllSummariesAsync()).Single(r => r.MessageId == "1").Cc);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* temp */ }
        }
    }

    private static void Exec(string dbPath, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
