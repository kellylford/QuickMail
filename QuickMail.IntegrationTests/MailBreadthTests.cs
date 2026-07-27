using System.Diagnostics;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Mail-operation breadth against live GreenMail (live-content testing plan §4.5): flags and
/// \Seen state, folder operations, real SMTP send end-to-end, and — exercising #296 — ICS
/// invite-reply routing: the Accept/Decline reply must leave via the SMTP settings of the
/// account that received the invite and reach the organizer's mailbox.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class MailBreadthTests
{
    private readonly GreenMailFixture _greenMail;

    public MailBreadthTests(GreenMailFixture greenMail) => _greenMail = greenMail;

    [Fact]
    public async Task FlagAndMarkRead_RoundTripThroughServer()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("flags");
        await SeedPlainAsync(account.Username, "flag me", ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            var summary = await WaitForMessageAsync(imap, account.Id, ct);
            Assert.False(summary.IsRead);
            Assert.False(summary.IsServerFlagged);

            await imap.SetMessageFlaggedAsync(account.Id, "INBOX", summary.MessageId, flagged: true, ct);
            await imap.MarkReadAsync(account.Id, "INBOX", summary.MessageId, ct);

            var after = (await imap.GetMessageSummariesAsync(account.Id, "INBOX", 10, ct))[0];
            Assert.True(after.IsRead, "\\Seen must round-trip through the server.");
            Assert.True(after.IsServerFlagged, "\\Flagged must round-trip through the server.");

            await imap.SetMessageFlaggedAsync(account.Id, "INBOX", summary.MessageId, flagged: false, ct);
            var cleared = (await imap.GetMessageSummariesAsync(account.Id, "INBOX", 10, ct))[0];
            Assert.False(cleared.IsServerFlagged);
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    [Fact]
    public async Task Folders_CreateListMoveAcross()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("folders");
        await SeedPlainAsync(account.Username, "move me", ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            var summary = await WaitForMessageAsync(imap, account.Id, ct);

            await imap.CreateFolderAsync(account.Id, null, "Projects", ct);
            var folders = await imap.GetFoldersAsync(account.Id, ct);
            Assert.Contains(folders, f => f.FullName == "Projects");

            await imap.MoveMessagesAsync(account.Id, "INBOX", [summary.MessageId], "Projects", ct);

            var inboxAfter = await imap.GetMessageSummariesAsync(account.Id, "INBOX", 10, ct);
            var projectsAfter = await imap.GetMessageSummariesAsync(account.Id, "Projects", 10, ct);
            Assert.Empty(inboxAfter);
            var moved = Assert.Single(projectsAfter);
            Assert.Equal("connection test", moved.Subject);
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    [Fact]
    public async Task SmtpSend_DeliversEndToEnd()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var sender = _greenMail.CreateAccount("sender");
        var recipient = _greenMail.CreateAccount("recipient");

        var smtp = new SmtpService(new NoOpOAuthService(), new NoOpGraphSendService());
        var compose = new ComposeModel
        {
            AccountId = sender.Id,
            To = recipient.Username,
            Subject = "Real SMTP send",
            Body = "Sent through SmtpService against live GreenMail.",
        };
        await smtp.SendAsync(compose, sender, "pw", ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(recipient, "pw", ct);
        try
        {
            var received = await WaitForMessageAsync(imap, recipient.Id, ct);
            Assert.Equal("Real SMTP send", received.Subject);

            var detail = await imap.GetMessageDetailAsync(recipient.Id, "INBOX", received.MessageId, ct);
            Assert.Contains("Sent through SmtpService", detail.PlainTextBody);
            Assert.Contains(sender.Username, detail.From);
        }
        finally
        {
            await imap.DisconnectAsync(recipient.Id, ct);
        }
    }

    [Fact]
    public async Task IcsReply_RoutesFromReceivingAccount_ToOrganizer()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        // #296 shape: an invite lands in ONE of the user's accounts; the Accept reply must go
        // out through THAT account's SMTP identity and reach the organizer.
        var invitee   = _greenMail.CreateAccount("invitee296");
        var organizer = _greenMail.CreateAccount("organizer296");

        var replyIcs = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "METHOD:REPLY",
            "BEGIN:VEVENT",
            "UID:route-296@quickmail.test",
            $"ORGANIZER:mailto:{organizer.Username}",
            $"ATTENDEE;PARTSTAT=ACCEPTED:mailto:{invitee.Username}",
            "SUMMARY:Routed Meeting",
            "DTSTART:20260915T140000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var smtp = new SmtpService(new NoOpOAuthService(), new NoOpGraphSendService());
        await smtp.SendIcsReplyAsync(replyIcs, invitee, "pw", organizer.Username, ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(organizer, "pw", ct);
        try
        {
            var received = await WaitForMessageAsync(imap, organizer.Id, ct);
            // The summary's From renders the display name (the invitee account's AccountName),
            // not the address — asserting it proves the reply left under the invitee account's
            // identity, not the organizer's or a default account's.
            Assert.Contains("invitee296", received.From);
            Assert.DoesNotContain("organizer296", received.From);

            var detail = await imap.GetMessageDetailAsync(organizer.Id, "INBOX", received.MessageId, ct);
            Assert.NotNull(detail.CalendarInvite);
            Assert.Equal("REPLY", detail.CalendarInvite!.Method);
            Assert.Equal("route-296@quickmail.test", detail.CalendarInvite.Uid);
        }
        finally
        {
            await imap.DisconnectAsync(organizer.Id, ct);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<MailMessageSummary> WaitForMessageAsync(
        ImapMailService imap, Guid accountId, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            var summaries = await imap.GetMessageSummariesAsync(accountId, "INBOX", 10, ct);
            if (summaries.Count > 0)
                return summaries[0];
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Expected message never appeared in the GreenMail INBOX.");
    }

    private static async Task SeedPlainAsync(string recipient, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = "connection test";
        message.Body = new TextPart("plain") { Text = body };

        using var smtp = new MailKit.Net.Smtp.SmtpClient();
        await smtp.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.SmtpPort, MailKit.Security.SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}

/// <summary>Graph send-backend stub: IMAP accounts never route here; any call is a bug.</summary>
internal sealed class NoOpGraphSendService : ISendMailService
{
    public Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default) =>
        throw new NotSupportedException("Graph send is not available in integration tests.");
    public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
        string organizerEmail, CancellationToken ct = default) =>
        throw new NotSupportedException("Graph send is not available in integration tests.");
    public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default) =>
        throw new NotSupportedException("Graph send is not available in integration tests.");
}
