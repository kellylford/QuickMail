using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Issue #999: on an account with no Trash folder, Delete took the messages out of the list and the
/// server kept every one. Moving to Trash set \Deleted instead and closed the folder without
/// expunging, and reported success. Proved against a real server, because GreenMail is exactly such
/// an account: a new mailbox there has only INBOX.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class DeleteWithoutTrashTests(GreenMailFixture greenMail)
{
    [Fact]
    public async Task MoveToTrash_WithNoTrashFolder_ThrowsAndLeavesTheMessagesAlone()
    {
        greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = greenMail.CreateAccount("delete-no-trash");
        await AppendAsync(account, "INBOX", "One", ct);
        await AppendAsync(account, "INBOX", "Two", ct);
        var uids = await UidsAsync(account, "INBOX", ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);

        await Assert.ThrowsAsync<TrashFolderNotFoundException>(
            () => imap.MoveToTrashBatchAsync(account.Id, "INBOX", uids, ct));

        // Still there, and not quietly marked for deletion either: nothing was done to them.
        Assert.Equal(2, await CountAsync(account, "INBOX", ct));
        Assert.Equal(0, await CountFlaggedDeletedAsync(account, "INBOX", ct));
        await imap.DisconnectAsync(account.Id, ct);
    }

    /// <summary>What the user gets on choosing to delete permanently: the messages really go.</summary>
    [Fact]
    public async Task PermanentDelete_WithNoTrashFolder_RemovesTheMessagesFromTheServer()
    {
        greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = greenMail.CreateAccount("delete-no-trash-permanent");
        await AppendAsync(account, "INBOX", "Doomed", ct);
        await AppendAsync(account, "INBOX", "Kept", ct);
        var uids = await UidsAsync(account, "INBOX", ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);

        await imap.PermanentlyDeleteBatchAsync(account.Id, "INBOX", [uids[0]], ct);

        Assert.Equal(1, await CountAsync(account, "INBOX", ct));
        await imap.DisconnectAsync(account.Id, ct);
    }

    /// <summary>
    /// Deleting permanently in an Inbox must not take with it a message another client only marked
    /// \Deleted, which that client can still undelete. Needs UID EXPUNGE (UIDPLUS).
    /// </summary>
    [Fact]
    public async Task PermanentDelete_LeavesOtherMessagesMarkedDeletedAlone()
    {
        greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = greenMail.CreateAccount("delete-no-trash-others");
        await AppendAsync(account, "INBOX", "Doomed", ct);
        await AppendAsync(account, "INBOX", "Marked elsewhere", ct);
        var uids = await UidsAsync(account, "INBOX", ct);

        using (var other = await RawClientAsync(account, ct))
        {
            Assert.SkipUnless(other.Capabilities.Contains(ImapCapability.UidPlus), "The server has no UIDPLUS.");
            var inbox = await other.GetFolderAsync("INBOX", ct);
            await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
            await inbox.AddFlagsAsync(new UniqueId(uint.Parse(uids[1], CultureInfo.InvariantCulture)),
                                      MessageFlags.Deleted, true, ct);
            await other.DisconnectAsync(true, ct);
        }

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        await imap.PermanentlyDeleteBatchAsync(account.Id, "INBOX", [uids[0]], ct);

        Assert.Equal(1, await CountAsync(account, "INBOX", ct));
        await imap.DisconnectAsync(account.Id, ct);
    }

    /// <summary>The ordinary case is unchanged: with a Trash folder, the messages move into it.</summary>
    [Fact]
    public async Task MoveToTrash_WithATrashFolder_MovesTheMessages()
    {
        greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = greenMail.CreateAccount("delete-with-trash");
        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        await imap.CreateFolderAsync(account.Id, null, "Trash", ct);
        await AppendAsync(account, "INBOX", "To Trash", ct);
        var uids = await UidsAsync(account, "INBOX", ct);

        await imap.MoveToTrashBatchAsync(account.Id, "INBOX", uids, ct);

        Assert.Equal(0, await CountAsync(account, "INBOX", ct));
        Assert.Equal(1, await CountAsync(account, "Trash", ct));
        await imap.DisconnectAsync(account.Id, ct);
    }

    private static async Task<ImapClient> RawClientAsync(AccountModel account, CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.ImapPort, SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(account.Username, "pw", ct);
        return client;
    }

    private static async Task AppendAsync(AccountModel account, string folderName, string subject, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "Body." } };
        message.From.Add(MailboxAddress.Parse("someone@example.test"));
        message.To.Add(MailboxAddress.Parse(account.Username));
        await folder.AppendAsync(message, MessageFlags.None, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static async Task<List<string>> UidsAsync(AccountModel account, string folderName, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var uids = await folder.SearchAsync(SearchQuery.All, ct);
        await client.DisconnectAsync(true, ct);
        return uids.Select(u => u.Id.ToString(CultureInfo.InvariantCulture)).ToList();
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

    private static async Task<int> CountFlaggedDeletedAsync(AccountModel account, string folderName, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var deleted = await folder.SearchAsync(SearchQuery.Deleted, ct);
        await client.DisconnectAsync(true, ct);
        return deleted.Count;
    }
}
