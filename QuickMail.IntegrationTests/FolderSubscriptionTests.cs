using System.Collections.Generic;
using System.Linq;
using MailKit;
using MailKit.Net.Imap;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Folder subscriptions against live GreenMail (#943). Outlook and most other clients show only
/// subscribed folders, so a folder QuickMail made without subscribing was on the server and in
/// QuickMail, but missing everywhere else.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class FolderSubscriptionTests
{
    private readonly GreenMailFixture _greenMail;

    public FolderSubscriptionTests(GreenMailFixture greenMail) => _greenMail = greenMail;

    [Fact]
    public async Task ANewFolder_IsSubscribed()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sub943a");
        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            await imap.CreateFolderAsync(account.Id, null, "messagetest", ct);
        }
        finally { await imap.DisconnectAsync(account.Id, ct); }

        Assert.Contains("messagetest", await SubscribedAsync(account, ct));
    }

    [Fact]
    public async Task ARenamedFolder_AndItsSubfolders_AreSubscribedUnderTheirNewNames_AndNotTheOld()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sub943b");
        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        string separator;
        try
        {
            await imap.CreateFolderAsync(account.Id, null, "Projects", ct);
            separator = await SeparatorAsync(account, ct);
            await imap.CreateFolderAsync(account.Id, "Projects", "2026", ct);

            await imap.RenameFolderAsync(account.Id, "Projects", "Work", null, ct);
        }
        finally { await imap.DisconnectAsync(account.Id, ct); }

        var subscribed = await SubscribedAsync(account, ct);
        Assert.Contains("Work", subscribed);
        Assert.Contains($"Work{separator}2026", subscribed);
        Assert.DoesNotContain("Projects", subscribed);
        Assert.DoesNotContain($"Projects{separator}2026", subscribed);
    }

    [Fact]
    public async Task ACopiedFolder_IsSubscribed()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sub943c");
        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        string separator;
        try
        {
            await imap.CreateFolderAsync(account.Id, null, "Source", ct);
            await imap.CreateFolderAsync(account.Id, null, "Shelf", ct);
            separator = await SeparatorAsync(account, ct);

            await imap.CopyFolderAsync(account.Id, "Source", "Shelf", ct);
        }
        finally { await imap.DisconnectAsync(account.Id, ct); }

        Assert.Contains($"Shelf{separator}Source", await SubscribedAsync(account, ct));
    }

    [Fact]
    public async Task ADeletedFolder_IsNoLongerSubscribed()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = _greenMail.CreateAccount("sub943d");
        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            await imap.CreateFolderAsync(account.Id, null, "Temporary", ct);
            await imap.DeleteFolderAsync(account.Id, "Temporary", ct);
        }
        finally { await imap.DisconnectAsync(account.Id, ct); }

        Assert.DoesNotContain("Temporary", await SubscribedAsync(account, ct));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<List<string>> SubscribedAsync(AccountModel account, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0], subscribedOnly: true, cancellationToken: ct);
        var names = folders.Select(f => f.FullName).ToList();
        await client.DisconnectAsync(true, ct);
        return names;
    }

    private static async Task<string> SeparatorAsync(AccountModel account, CancellationToken ct)
    {
        using var client = await RawClientAsync(account, ct);
        var separator = client.PersonalNamespaces[0].DirectorySeparator.ToString();
        await client.DisconnectAsync(true, ct);
        return separator;
    }

    private static async Task<ImapClient> RawClientAsync(AccountModel account, CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.ImapPort, MailKit.Security.SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(account.Username, "pw", ct);
        return client;
    }
}
