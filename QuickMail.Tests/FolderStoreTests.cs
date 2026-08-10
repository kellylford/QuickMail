using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Round-trip tests for the Folder SQLite table via LocalStoreService (#516).
///
/// The folder list is what lets the startup folder resolve — and the folder tree draw — before any
/// account connects. Before it was persisted, "open me in All Inboxes" was unimplementable at launch
/// because inbox-ness is only known once <c>GetFoldersAsync</c> has run. So the properties these
/// tests pin are not incidental: <see cref="MailFolderModel.Kind"/> surviving the round trip is the
/// whole point, and replace-on-save is what stops a folder deleted on the server from haunting the
/// tree forever.
///
/// Each test uses a fresh temp-directory profile so migrations run from scratch.
/// </summary>
public class FolderStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;

    public FolderStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static MailFolderModel Folder(
        Guid accountId, string fullName, string display,
        SpecialFolderKind kind = SpecialFolderKind.None,
        bool excluded = false, int unread = 0, int total = 0, string? parentId = null) => new()
        {
            AccountId          = accountId,
            FullName           = fullName,
            DisplayName        = display,
            Kind               = kind,
            ExcludeFromAllMail = excluded,
            UnreadCount        = unread,
            MessageCount       = total,
            ParentId           = parentId,
        };

    [Fact]
    public async Task SaveThenLoad_RoundTripsEveryPersistedField()
    {
        var account = Guid.NewGuid();
        await _store.SaveFoldersAsync(account,
        [
            Folder(account, "INBOX", "Inbox", SpecialFolderKind.Inbox, unread: 7, total: 120),
            Folder(account, "INBOX/Work", "Work", parentId: "INBOX", unread: 2, total: 30),
            Folder(account, "Trash", "Trash", SpecialFolderKind.Trash, excluded: true),
        ]);

        var loaded = await _store.LoadFoldersAsync();

        var folders = Assert.Contains(account, loaded);
        Assert.Equal(3, folders.Count);

        var inbox = folders[0];
        Assert.Equal("INBOX", inbox.FullName);
        Assert.Equal("Inbox", inbox.DisplayName);
        Assert.Equal(SpecialFolderKind.Inbox, inbox.Kind);
        Assert.Equal(7, inbox.UnreadCount);
        Assert.Equal(120, inbox.MessageCount);
        Assert.False(inbox.ExcludeFromAllMail);
        Assert.Null(inbox.ParentId);
        Assert.Equal(account, inbox.AccountId);

        Assert.Equal("INBOX", folders[1].ParentId);

        Assert.Equal(SpecialFolderKind.Trash, folders[2].Kind);
        Assert.True(folders[2].ExcludeFromAllMail);
    }

    [Fact]
    public async Task Load_PreservesSaveOrder()
    {
        // The tree and the All Mail aggregate both present folders in the order the backend listed
        // them; a set that comes back alphabetised would reorder the user's tree on every restart.
        var account = Guid.NewGuid();
        var names = new[] { "INBOX", "Zebra", "Alpha", "INBOX/Sub" };
        await _store.SaveFoldersAsync(account, [.. names.Select(n => Folder(account, n, n))]);

        var loaded = await _store.LoadFoldersAsync();

        Assert.Equal(names, loaded[account].Select(f => f.FullName));
    }

    [Fact]
    public async Task Save_ReplacesThatAccountsFolders_RatherThanMerging()
    {
        // A folder deleted or renamed on the server must disappear locally. An upsert would leave
        // the old row behind and the tree would keep offering a folder that no longer exists.
        var account = Guid.NewGuid();
        await _store.SaveFoldersAsync(account,
        [
            Folder(account, "INBOX", "Inbox", SpecialFolderKind.Inbox),
            Folder(account, "OldName", "OldName"),
        ]);

        await _store.SaveFoldersAsync(account,
        [
            Folder(account, "INBOX", "Inbox", SpecialFolderKind.Inbox),
            Folder(account, "NewName", "NewName"),
        ]);

        var folders = (await _store.LoadFoldersAsync())[account];
        Assert.Equal(["INBOX", "NewName"], folders.Select(f => f.FullName));
    }

    [Fact]
    public async Task Save_LeavesOtherAccountsAlone()
    {
        // A partial connect refreshes only the accounts it reached; the rest must keep the folders
        // they had, otherwise one failing account would blank another's tree.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await _store.SaveFoldersAsync(a, [Folder(a, "INBOX", "Inbox")]);
        await _store.SaveFoldersAsync(b, [Folder(b, "INBOX", "Inbox"), Folder(b, "Archive", "Archive")]);

        await _store.SaveFoldersAsync(a, [Folder(a, "INBOX", "Inbox"), Folder(a, "Sent", "Sent")]);

        var loaded = await _store.LoadFoldersAsync();
        Assert.Equal(2, loaded[a].Count);
        Assert.Equal(["INBOX", "Archive"], loaded[b].Select(f => f.FullName));
    }

    [Fact]
    public async Task Save_SkipsHeaderRowsAndEmptyNames()
    {
        // Header rows are synthesized by RebuildFolderListFromCache for display; they carry no
        // server folder, and a stored one would come back as a phantom child in the tree.
        var account = Guid.NewGuid();
        await _store.SaveFoldersAsync(account,
        [
            new MailFolderModel { AccountId = account, IsHeader = true, FullName = "\u0000Header:x", DisplayName = "Work" },
            new MailFolderModel { AccountId = account, FullName = "", DisplayName = "nameless" },
            Folder(account, "INBOX", "Inbox"),
        ]);

        var folders = (await _store.LoadFoldersAsync())[account];
        Assert.Equal(["INBOX"], folders.Select(f => f.FullName));
    }

    [Fact]
    public async Task Save_EmptyList_ClearsTheAccount()
    {
        var account = Guid.NewGuid();
        await _store.SaveFoldersAsync(account, [Folder(account, "INBOX", "Inbox")]);

        await _store.SaveFoldersAsync(account, []);

        Assert.DoesNotContain(account, await _store.LoadFoldersAsync());
    }

    [Fact]
    public async Task PurgeForUnknownAccounts_DropsOrphansAndKeepsKnown()
    {
        var known  = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        await _store.SaveFoldersAsync(known,  [Folder(known,  "INBOX", "Inbox")]);
        await _store.SaveFoldersAsync(orphan, [Folder(orphan, "INBOX", "Inbox")]);

        await _store.PurgeFoldersForUnknownAccountsAsync([known]);

        var loaded = await _store.LoadFoldersAsync();
        Assert.Contains(known, loaded);
        Assert.DoesNotContain(orphan, loaded);
    }

    [Fact]
    public async Task PurgeForUnknownAccounts_WithNoKnownAccounts_ClearsEverything()
    {
        // Unlike calendar events there is no Guid.Empty "local" bucket to preserve — every folder
        // belongs to a real account, so an empty known-set means every row is an orphan.
        var account = Guid.NewGuid();
        await _store.SaveFoldersAsync(account, [Folder(account, "INBOX", "Inbox")]);

        await _store.PurgeFoldersForUnknownAccountsAsync([]);

        Assert.Empty(await _store.LoadFoldersAsync());
    }

    [Fact]
    public async Task DeleteAccountData_AlsoRemovesItsFolders()
    {
        // Otherwise a removed account's folders would still be restored into the tree at next launch.
        var account = Guid.NewGuid();
        await _store.SaveFoldersAsync(account, [Folder(account, "INBOX", "Inbox")]);

        await _store.DeleteAccountDataAsync(account);

        Assert.Empty(await _store.LoadFoldersAsync());
    }

    [Fact]
    public async Task Load_OnAFreshDatabase_ReturnsEmpty()
    {
        Assert.Empty(await _store.LoadFoldersAsync());
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AndKeepsStoredFolders()
    {
        // Initialize runs on every launch. The Folder table is created with CREATE TABLE IF NOT
        // EXISTS and no user_version bump, so a second run must neither throw nor drop rows —
        // this is the guard for an existing database picking up the new table.
        var account = Guid.NewGuid();
        await _store.SaveFoldersAsync(account, [Folder(account, "INBOX", "Inbox", SpecialFolderKind.Inbox)]);

        _store.Initialize();
        var reopened = new LocalStoreService(new ProfileContext(_tempDir));
        reopened.Initialize();

        var folders = (await reopened.LoadFoldersAsync())[account];
        Assert.Equal(SpecialFolderKind.Inbox, Assert.Single(folders).Kind);
    }
}
