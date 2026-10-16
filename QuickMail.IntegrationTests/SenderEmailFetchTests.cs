using System.Diagnostics;
using System.IO;
using System.Text;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// The From-header fetch behind the one-time sender-address backfill (#803), against a real IMAP server (review
/// of #806). The unit tests stand in for the server, so only here is it checked that the header comes back raw
/// enough to parse an encoded name with a comma in it, that an id the server no longer has is simply left out,
/// that the Message-ID the fetch returns can be compared with the one the sync cached, and that fetching marks
/// nothing read.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class SenderEmailFetchTests
{
    private readonly GreenMailFixture _greenMail;

    public SenderEmailFetchTests(GreenMailFixture greenMail) => _greenMail = greenMail;

    [Fact]
    public async Task FetchSenderEmails_ReadsEachShapeOfSender_AndLeavesOutWhatIsGone()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("senders");

        // RFC 2047 encodes the whole name, comma included. Decoded first and parsed second it reads as two
        // addresses, "Ford" and "Kelly <kelly@example.test>"; the fetch must parse the raw header instead.
        await SeedRawAsync(account.Username, "encoded", "=?utf-8?q?Ford=2C_Kelly?= <kelly@example.test>", "encoded@quickmail.test", ct);
        await SeedRawAsync(account.Username, "bare", "bare@example.test", "bare@quickmail.test", ct);
        await SeedRawAsync(account.Username, "deleted", "Gone Sender <gone@example.test>", "deleted@quickmail.test", ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            var rows = await WaitForMessagesAsync(imap, account.Id, 3, ct);
            var encoded = rows.Single(r => r.Subject == "encoded");
            var bare = rows.Single(r => r.Subject == "bare");
            var deleted = rows.Single(r => r.Subject == "deleted");
            await imap.PermanentlyDeleteBatchAsync(account.Id, "INBOX", [deleted.MessageId], ct);

            var fetched = await imap.FetchSenderEmailsAsync(
                account.Id, "INBOX", [encoded.MessageId, bare.MessageId, deleted.MessageId], ct);

            Assert.Equal(2, fetched.Count);   // the deleted message is left out, not an error
            Assert.False(fetched.ContainsKey(deleted.MessageId));
            Assert.Equal("kelly@example.test", fetched[encoded.MessageId].FromEmail);
            Assert.Equal("bare@example.test", fetched[bare.MessageId].FromEmail);   // no display name

            // The Message-ID comes back in the form the sync cached from the ENVELOPE, so the backfill's check
            // that an id still names the cached message compares like with like (review of #806, item 11).
            Assert.Equal("encoded@quickmail.test", fetched[encoded.MessageId].InternetMessageId);
            Assert.Equal(encoded.InternetMessageId, fetched[encoded.MessageId].InternetMessageId);
            Assert.Equal(bare.InternetMessageId, fetched[bare.MessageId].InternetMessageId);

            // BODY.PEEK: catching up on senders must not mark anything read.
            var after = await imap.GetMessageSummariesAsync(account.Id, "INBOX", 10, ct);
            Assert.All(after, r => Assert.False(r.IsRead, $"'{r.Subject}' was marked read by the sender fetch."));
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<List<MailMessageSummary>> WaitForMessagesAsync(
        ImapMailService imap, Guid accountId, int count, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            var summaries = await imap.GetMessageSummariesAsync(accountId, "INBOX", 10, ct);
            if (summaries.Count >= count)
                return summaries;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"Expected {count} messages in the GreenMail INBOX.");
    }

    /// <summary>
    /// Sends a message whose From header is exactly <paramref name="rawFrom"/>. Loaded from raw text rather than
    /// built, because MimeKit would otherwise choose its own encoding for the name; a loaded header keeps its
    /// bytes on the way out.
    /// </summary>
    private static async Task SeedRawAsync(
        string recipient, string subject, string rawFrom, string messageId, CancellationToken ct)
    {
        var raw = string.Join("\r\n",
            $"From: {rawFrom}",
            $"To: {recipient}",
            $"Subject: {subject}",
            $"Message-ID: <{messageId}>",
            "Date: Thu, 15 Oct 2026 10:00:00 +0000",
            "MIME-Version: 1.0",
            "Content-Type: text/plain; charset=us-ascii",
            "",
            "Seeded for the sender-address fetch test.",
            "");
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var message = await MimeMessage.LoadAsync(stream, ct);

        using var smtp = new MailKit.Net.Smtp.SmtpClient();
        await smtp.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.SmtpPort, MailKit.Security.SecureSocketOptions.None, ct);
        // The envelope sender is given explicitly: the encoded From is what is under test, not what SMTP is
        // handed as the return path.
        await smtp.SendAsync(message, MailboxAddress.Parse("seeder@example.test"), [MailboxAddress.Parse(recipient)], ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
