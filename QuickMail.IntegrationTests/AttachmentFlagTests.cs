using System.Diagnostics;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// The attachment flag on IMAP summaries (#931), against a real IMAP server: every summary fetch the message list
/// uses carries BODYSTRUCTURE and sets HasAttachments from it, the one-time pass's BODYSTRUCTURE-only fetch agrees,
/// and none of it marks a message read.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class AttachmentFlagTests
{
    private readonly GreenMailFixture _greenMail;

    public AttachmentFlagTests(GreenMailFixture greenMail) => _greenMail = greenMail;

    [Fact]
    public async Task EverySummaryFetchSaysWhichMessagesHaveAttachments()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("attflags");

        await SendAsync(account.Username, "plain", attachment: false, ct);
        await SendAsync(account.Username, "with file", attachment: true, ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            var all = await WaitForMessagesAsync(imap, account.Id, 2, ct);
            AssertFlags(all, "GetMessageSummariesAsync");
            AssertFlags(await imap.GetMessagesSinceAsync(account.Id, "INBOX", "0", 10, ct), "GetMessagesSinceAsync");
            AssertFlags(await imap.GetMessagesSinceDateAsync(account.Id, "INBOX", DateTime.UtcNow.AddDays(-30), ct), "GetMessagesSinceDateAsync");
            var found = await imap.SearchServerAsync(account.Id, MessageSearchQuery.Parse("subject:with"), ["INBOX"], 10, ct);
            Assert.True(Assert.Single(found).HasAttachments, "SearchServerAsync");

            var flags = await imap.FetchAttachmentFlagsAsync(account.Id, "INBOX", all.Select(m => m.MessageId).ToList(), ct);
            Assert.False(flags[all.Single(m => m.Subject == "plain").MessageId].HasAttachments);
            var withFile = all.Single(m => m.Subject == "with file");
            Assert.True(flags[withFile.MessageId].HasAttachments);
            Assert.Equal(withFile.InternetMessageId, flags[withFile.MessageId].InternetMessageId);

            var after = await imap.GetMessageSummariesAsync(account.Id, "INBOX", 10, ct);
            Assert.All(after, r => Assert.False(r.IsRead, $"'{r.Subject}' was marked read."));
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    private static void AssertFlags(List<MailMessageSummary> rows, string path)
    {
        Assert.False(rows.Single(r => r.Subject == "plain").HasAttachments, path);
        Assert.True(rows.Single(r => r.Subject == "with file").HasAttachments, path);
    }

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

    private static async Task SendAsync(string recipient, string subject, bool attachment, CancellationToken ct)
    {
        var builder = new BodyBuilder { TextBody = "Seeded for the attachment-flag test." };
        if (attachment)
            builder.Attachments.Add("report.pdf", "%PDF-1.4 test"u8.ToArray(), new ContentType("application", "pdf"));
        var message = new MimeMessage
        {
            Subject = subject,
            Body = builder.ToMessageBody(),
        };
        message.From.Add(MailboxAddress.Parse("seeder@example.test"));
        message.To.Add(MailboxAddress.Parse(recipient));

        using var smtp = new MailKit.Net.Smtp.SmtpClient();
        await smtp.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.SmtpPort, MailKit.Security.SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
