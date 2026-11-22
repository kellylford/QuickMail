using System.Diagnostics;
using System.IO;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// A client rule's "Sent to" condition against real IMAP and POP3 servers. It matches the To and the Cc line, as
/// Outlook's does (and so a Microsoft server rule's), which means every backend has to bring the Cc line back on the
/// message summary the rule reads. Only a real server proves the IMAP ENVELOPE and the POP3 download carry it; the
/// unit tests hand the rule a summary they built themselves. The POP3 case also goes through the local store, which
/// for POP3 is where the summary comes back from.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class SentToRuleTests : IDisposable
{
    private readonly GreenMailFixture _greenMail;
    private readonly string _profileDir;

    public SentToRuleTests(GreenMailFixture greenMail)
    {
        _greenMail = greenMail;
        _profileDir = Path.Combine(Path.GetTempPath(), $"QuickMailSentToIT-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        // Pooled SQLite connections hold the store's file open; release them so the folder can go.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, recursive: true); }
        catch { /* a temp dir left behind is not a test failure */ }
    }

    [Fact]
    public async Task Imap_AMessageThatOnlyCcsTheAccount_MatchesASentToRule()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sentto-imap");
        await SeedAsync(account.Username, "cc only", to: "someone.else@example.test", cc: account.Username, ct);
        await SeedAsync(account.Username, "to directly", to: account.Username, cc: null, ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            var rows = await WaitForImapAsync(imap, account.Id, 2, ct);
            var ccOnly = rows.Single(r => r.Subject == "cc only");
            var direct = rows.Single(r => r.Subject == "to directly");

            Assert.Contains(account.Username, ccOnly.Cc, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(account.Username, ccOnly.To, StringComparison.OrdinalIgnoreCase);

            var matched = TestRule(SentToRule(account.Username), rows);
            Assert.Contains(matched, m => m.MessageId == ccOnly.MessageId);
            Assert.Contains(matched, m => m.MessageId == direct.MessageId);

            // And one that names neither line does not match.
            Assert.Empty(TestRule(SentToRule("nobody@example.test"), rows));
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    [Fact]
    public async Task Pop3_AMessageThatOnlyCcsTheAccount_MatchesASentToRule_AfterTheStore()
    {
        _greenMail.RequirePop3();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("sentto-pop");
        await SeedAsync(account.Username, "cc only", to: "someone.else@example.test", cc: account.Username, ct);

        var store = new LocalStoreService(new ProfileContext(Path.Combine(_profileDir, "pop")));
        store.Initialize();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);
        try
        {
            var downloaded = await WaitForPop3Async(pop, account.Id, ct);
            var row = Assert.Single(downloaded);
            Assert.Contains(account.Username, row.Cc, StringComparison.OrdinalIgnoreCase);
            Assert.Single(TestRule(SentToRule(account.Username), downloaded));

            // "Run rules on existing mail" reads the store, so the Cc line has to survive it too.
            var cached = (await store.LoadAllSummariesAsync()).Single(s => s.AccountId == account.Id);
            Assert.Contains(account.Username, cached.Cc, StringComparison.OrdinalIgnoreCase);
            Assert.Single(TestRule(SentToRule(account.Username), [cached]));
        }
        finally
        {
            await pop.DisconnectAsync(account.Id, ct);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static MailRule SentToRule(string address) => new()
    {
        Name = "Sent to",
        UseFromCondition = false,
        UseSubjectCondition = false,
        UseBodyCondition = false,
        UseToCondition = true,
        ToContains = address,
        Action = RuleAction.MarkAsRead,
    };

    private List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages)
    {
        var dir = Path.Combine(_profileDir, "rules-" + Guid.NewGuid().ToString("N"));
        // TestRule only evaluates conditions; the mail service and store are never called.
        using var imap = new ImapMailService(new NoOpOAuthService());
        var svc = new RuleService(imap, new LocalStoreService(new ProfileContext(dir)), dir);
        return svc.TestRule(rule, messages);
    }

    private static async Task<List<MailMessageSummary>> WaitForImapAsync(
        ImapMailService imap, Guid accountId, int count, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            var summaries = await imap.GetMessageSummariesAsync(accountId, "INBOX", 10, ct);
            if (summaries.Count >= count) return summaries;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"Expected {count} messages in the GreenMail INBOX.");
    }

    private static async Task<List<MailMessageSummary>> WaitForPop3Async(
        Pop3MailService pop, Guid accountId, CancellationToken ct)
    {
        var collected = new List<MailMessageSummary>();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            collected.AddRange(await pop.GetMessagesSinceAsync(accountId, "Inbox", "0", 50, ct));
            if (collected.Count >= 1) return collected;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Expected a message to arrive over POP3.");
    }

    /// <summary>Delivers a message to <paramref name="deliverTo"/> whose headers name <paramref name="to"/> and
    /// <paramref name="cc"/>: the SMTP envelope decides the mailbox, the headers are what the rule reads.</summary>
    private static async Task SeedAsync(string deliverTo, string subject, string to, string? cc, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@example.test"));
        message.To.Add(MailboxAddress.Parse(to));
        if (cc is not null) message.Cc.Add(new MailboxAddress("Copied", cc));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "Seeded for the Sent to rule test." };

        using var smtp = new MailKit.Net.Smtp.SmtpClient();
        await smtp.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.SmtpPort, MailKit.Security.SecureSocketOptions.None, ct);
        await smtp.SendAsync(new MailKit.SendRequest(message, MailboxAddress.Parse("sender@example.test"), [MailboxAddress.Parse(deliverTo)]), ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
