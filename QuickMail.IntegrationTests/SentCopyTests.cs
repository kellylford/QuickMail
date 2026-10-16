using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Filing the copy in Sent against live GreenMail (#811). A server that files its own copy of what
/// it relayed (Gmail, iCloud, Outlook.com) used to end up with two, because QuickMail appended
/// another under a different Message-ID. GreenMail does not file one itself, so the tests put the
/// "server's copy" there by hand.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class SentCopyTests
{
    private readonly GreenMailFixture _greenMail;

    public SentCopyTests(GreenMailFixture greenMail) => _greenMail = greenMail;

    [Fact]
    public async Task ServerAlreadyFiledTheCopy_NoSecondCopyIsAdded()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sent811a");
        var compose = SentCompose(account);
        MimeMessageBuilder.EnsureOutgoingMessageId(compose, account);

        using var imap = await ConnectWithSentAsync(account, ct);
        try
        {
            await FileServerCopyAsync(account, compose, ct);

            await imap.AppendToSentAsync(account.Id, compose, ct);

            Assert.Equal(1, await CountInSentAsync(account, compose.OutgoingMessageId!, ct));
        }
        finally { await imap.DisconnectAsync(account.Id, ct); }
    }

    [Fact]
    public async Task ServerFilesTheCopyLate_IsFoundOnALaterLook()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sent811b");
        var compose = SentCompose(account);
        MimeMessageBuilder.EnsureOutgoingMessageId(compose, account);

        using var imap = await ConnectWithSentAsync(account, ct);
        imap.SentCopyChecks = [TimeSpan.Zero, TimeSpan.FromSeconds(1)];
        // The server's copy lands only after the first look has found nothing, so the test
        // fails unless the second look runs and finds it.
        var missed = new List<int>();
        imap.SentCopyLookMissed += i =>
        {
            missed.Add(i);
            if (i == 0) FileServerCopyAsync(account, compose, ct).GetAwaiter().GetResult();
        };
        try
        {
            await imap.AppendToSentAsync(account.Id, compose, ct);

            Assert.Equal([0], missed);
            Assert.Equal(1, await CountInSentAsync(account, compose.OutgoingMessageId!, ct));
        }
        finally { await imap.DisconnectAsync(account.Id, ct); }
    }

    [Fact]
    public async Task ServerFiledNothing_QuickMailFilesOneCopyUnderTheSentId()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sent811c");
        var compose = SentCompose(account);
        MimeMessageBuilder.EnsureOutgoingMessageId(compose, account);

        using var imap = await ConnectWithSentAsync(account, ct);
        imap.SentCopyChecks = [TimeSpan.Zero, TimeSpan.FromMilliseconds(100)];
        try
        {
            await imap.AppendToSentAsync(account.Id, compose, ct);

            Assert.Equal(1, await CountInSentAsync(account, compose.OutgoingMessageId!, ct));
        }
        finally { await imap.DisconnectAsync(account.Id, ct); }
    }

    [Fact]
    public async Task NoOutgoingId_StillFilesOneCopy()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sent811d");
        var compose = SentCompose(account);

        using var imap = await ConnectWithSentAsync(account, ct);
        try
        {
            await imap.AppendToSentAsync(account.Id, compose, ct);

            Assert.Equal(1, await CountSubjectInSentAsync(account, compose.Subject, ct));
        }
        finally { await imap.DisconnectAsync(account.Id, ct); }
    }

    [Fact]
    public async Task RealSend_ThenFile_TheTwoCopiesShareOneMessageId()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var sender = _greenMail.CreateAccount("sent811sender");
        var recipient = _greenMail.CreateAccount("sent811recipient");
        var compose = SentCompose(sender);
        compose.To = recipient.Username;

        // The same ComposeModel goes to SmtpService and then AppendToSentAsync, as in the app.
        var smtp = new SmtpService(new NoOpOAuthService(), new NoOpGraphSendService());
        await smtp.SendAsync(compose, sender, "pw", ct);
        Assert.False(string.IsNullOrEmpty(compose.OutgoingMessageId));

        using var imap = await ConnectWithSentAsync(sender, ct);
        imap.SentCopyChecks = [TimeSpan.Zero];
        try { await imap.AppendToSentAsync(sender.Id, compose, ct); }
        finally { await imap.DisconnectAsync(sender.Id, ct); }

        Assert.Equal(1, await CountInSentAsync(sender, compose.OutgoingMessageId!, ct));
        Assert.Equal(1, await CountAsync(recipient, "INBOX",
            SearchQuery.HeaderContains("Message-ID", compose.OutgoingMessageId!), ct, waitForOne: true));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ComposeModel SentCompose(AccountModel account) => new()
    {
        AccountId = account.Id,
        To = "someone@example.test",
        Subject = "Sent copy " + Guid.NewGuid().ToString("N"),
        Body = "Filed once.",
    };

    private static async Task<ImapMailService> ConnectWithSentAsync(AccountModel account, CancellationToken ct)
    {
        var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        await imap.CreateFolderAsync(account.Id, null, "Sent", ct);
        return imap;
    }

    /// <summary>Stands in for a server filing its own copy of relayed mail.</summary>
    private static async Task FileServerCopyAsync(AccountModel account, ComposeModel compose, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var sent = await client.GetFolderAsync("Sent", ct);
        await sent.OpenAsync(FolderAccess.ReadWrite, ct);
        await sent.AppendAsync(MimeMessageBuilder.Build(compose, account), MessageFlags.Seen, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static Task<int> CountInSentAsync(AccountModel account, string messageId, CancellationToken ct) =>
        CountAsync(account, "Sent", SearchQuery.HeaderContains("Message-ID", messageId), ct);

    private static Task<int> CountSubjectInSentAsync(AccountModel account, string subject, CancellationToken ct) =>
        CountAsync(account, "Sent", SearchQuery.SubjectContains(subject), ct);

    private static async Task<int> CountAsync(
        AccountModel account, string folderName, SearchQuery query, CancellationToken ct, bool waitForOne = false)
    {
        using var client = await RawClientAsync(account, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            var count = (await folder.SearchAsync(query, ct)).Count;
            await folder.CloseAsync(false, ct);
            if (!waitForOne || count > 0 || DateTime.UtcNow > deadline)
            {
                await client.DisconnectAsync(true, ct);
                return count;
            }
            await Task.Delay(250, ct);
        }
    }

    private static async Task<ImapClient> RawClientAsync(AccountModel account, CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.ImapPort, MailKit.Security.SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(account.Username, "pw", ct);
        return client;
    }
}
