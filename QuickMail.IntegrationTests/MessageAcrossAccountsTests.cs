using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Moving and copying a message between two accounts (#929) against live GreenMail: the original is
/// read from one mailbox and APPENDed into the other, with its read and flagged state and its arrival
/// time. The server's own MOVE and COPY cannot do this; they reach only the source account's folders.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class MessageAcrossAccountsTests
{
    private readonly GreenMailFixture _greenMail;

    public MessageAcrossAccountsTests(GreenMailFixture greenMail) => _greenMail = greenMail;

    [Fact]
    public async Task TheOriginal_ArrivesInTheOtherAccount_WithItsStateAndDate()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var from = _greenMail.CreateAccount("across929a");
        var to   = _greenMail.CreateAccount("across929b");
        var subject  = "Across " + Guid.NewGuid().ToString("N");
        var received = new DateTimeOffset(2026, 3, 4, 9, 15, 0, TimeSpan.Zero);
        await SeedAsync(from, subject, ct);

        var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(from, "pw", ct);
        await imap.ConnectAsync(to, "pw", ct);
        try
        {
            await imap.CreateFolderAsync(to.Id, null, "Filed", ct);
            var uid = await FindUidAsync(from, "INBOX", subject, ct);

            using var original = new MemoryStream();
            await imap.CopyOriginalMessageToAsync(from.Id, "INBOX", uid, original, ct);
            original.Position = 0;
            Assert.True(imap.CanAppendMessages(to.Id));
            await imap.AppendMessageAsync(to.Id, "Filed", original, isRead: true, isFlagged: true, received, ct);
        }
        finally
        {
            await imap.DisconnectAsync(from.Id, ct);
            await imap.DisconnectAsync(to.Id, ct);
        }

        using var client = await RawClientAsync(to, ct);
        var filed = await client.GetFolderAsync("Filed", ct);
        await filed.OpenAsync(FolderAccess.ReadOnly, ct);
        var uids = await filed.SearchAsync(SearchQuery.SubjectContains(subject), ct);
        var summary = Assert.Single(await filed.FetchAsync(uids, MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope, ct));
        Assert.True(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
        Assert.True(summary.Flags!.Value.HasFlag(MessageFlags.Flagged));
        Assert.Equal(received, summary.InternalDate);
        Assert.Equal(subject, summary.Envelope?.Subject);
        await client.DisconnectAsync(true, ct);
    }

    [Fact]
    public async Task AnUnreadMessage_StaysUnread()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var from = _greenMail.CreateAccount("across929c");
        var to   = _greenMail.CreateAccount("across929d");
        var subject = "Unread " + Guid.NewGuid().ToString("N");
        await SeedAsync(from, subject, ct);

        var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(from, "pw", ct);
        await imap.ConnectAsync(to, "pw", ct);
        try
        {
            var uid = await FindUidAsync(from, "INBOX", subject, ct);
            using var original = new MemoryStream();
            await imap.CopyOriginalMessageToAsync(from.Id, "INBOX", uid, original, ct);
            original.Position = 0;
            await imap.AppendMessageAsync(to.Id, "INBOX", original, isRead: false, isFlagged: false, default, ct);
        }
        finally
        {
            await imap.DisconnectAsync(from.Id, ct);
            await imap.DisconnectAsync(to.Id, ct);
        }

        using var client = await RawClientAsync(to, ct);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var uids = await inbox.SearchAsync(SearchQuery.SubjectContains(subject), ct);
        var summary = Assert.Single(await inbox.FetchAsync(uids, MessageSummaryItems.Flags, ct));
        Assert.False(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
        await client.DisconnectAsync(true, ct);
    }

    private static async Task SeedAsync(AccountModel account, string subject, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("", account.Username));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "Across accounts." };
        using var client = await RawClientAsync(account, ct);
        await client.Inbox.AppendAsync(message, MessageFlags.None, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static async Task<string> FindUidAsync(AccountModel account, string folderName, string subject, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var uid = Assert.Single(await folder.SearchAsync(SearchQuery.SubjectContains(subject), ct));
        await client.DisconnectAsync(true, ct);
        return uid.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ImapClient> RawClientAsync(AccountModel account, CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.ImapPort, MailKit.Security.SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(account.Username, "pw", ct);
        return client;
    }
}
