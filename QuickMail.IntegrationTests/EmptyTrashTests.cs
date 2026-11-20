using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Empty Trash expunges whatever folder it settles on, so which folder that is needs proving against
/// a real server: it used to fall back on Junk when there was no Trash, and delete all of it.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class EmptyTrashTests(GreenMailFixture greenMail)
{
    [Fact]
    public async Task WithNoTrashFolder_JunkIsLeftAlone()
    {
        greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = greenMail.CreateAccount("empty-no-trash");
        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        await imap.CreateFolderAsync(account.Id, null, "Junk", ct);
        await AppendAsync(account, "Junk", ct);

        await Assert.ThrowsAsync<TrashFolderNotFoundException>(() => imap.EmptyTrashAsync(account.Id, ct));
        await Assert.ThrowsAsync<TrashFolderNotFoundException>(() => imap.CountTrashMessagesAsync(account.Id, ct));

        Assert.Equal(1, await CountAsync(account, "Junk", ct));
        await imap.DisconnectAsync(account.Id, ct);
    }

    [Fact]
    public async Task WithATrashFolder_OnlyTrashIsEmptied()
    {
        greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = greenMail.CreateAccount("empty-trash");
        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        await imap.CreateFolderAsync(account.Id, null, "Junk", ct);
        await imap.CreateFolderAsync(account.Id, null, "Trash", ct);
        await AppendAsync(account, "Junk", ct);
        await AppendAsync(account, "Trash", ct);

        Assert.Equal(1, await imap.CountTrashMessagesAsync(account.Id, ct));
        Assert.Equal(1, await imap.EmptyTrashAsync(account.Id, ct));

        Assert.Equal(0, await imap.CountTrashMessagesAsync(account.Id, ct));
        Assert.Equal(1, await CountAsync(account, "Junk", ct));
        await imap.DisconnectAsync(account.Id, ct);
    }

    private static async Task<ImapClient> RawClientAsync(AccountModel account, CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.ImapPort, SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(account.Username, "pw", ct);
        return client;
    }

    private static async Task AppendAsync(AccountModel account, string folderName, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        var message = new MimeMessage { Subject = "In " + folderName, Body = new TextPart("plain") { Text = "Body." } };
        message.From.Add(MailboxAddress.Parse("someone@example.test"));
        message.To.Add(MailboxAddress.Parse(account.Username));
        await folder.AppendAsync(message, MessageFlags.None, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static async Task<int> CountAsync(AccountModel account, string folderName, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var count = folder.Count;
        await client.DisconnectAsync(true, ct);
        return count;
    }
}
