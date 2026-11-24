// Rename Folder (#1083) on the real stores: the SQLite cache re-files a renamed IMAP folder's
// messages, and those of every folder inside it, and folderviews.json moves the remembered views.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class FolderRenameStoreTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");

    private static MailMessageSummary Summary(Guid account, string folder, string id) => new()
    {
        MessageId = id, AccountId = account, FolderName = folder, Subject = $"Subject {id}",
        From = "Sam <sam@example.com>", Date = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task TheFolderAndEverythingInsideIt_AreRefiled_AndNothingElse()
    {
        var store = new LocalStoreService(new ProfileContext(TempDir()));
        store.Initialize();
        var acct = Guid.NewGuid();
        var other = Guid.NewGuid();
        await store.UpsertSummariesAsync(
        [
            Summary(acct, "Projects", "1"),
            Summary(acct, "Projects/2026", "2"),
            Summary(acct, "Projects/2026/Q1", "3"),
            Summary(acct, "Projects old", "4"),   // a sibling that only starts the same way
            Summary(acct, "Other", "5"),
            Summary(other, "Projects", "6"),      // another account's folder of the same name
        ]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "1", AccountId = acct, FolderName = "Projects", To = "you@example.com", PlainTextBody = "body",
        });

        var moved = await store.RenameFolderAsync(acct, "Projects", "Clients", '/');

        Assert.Equal(3, moved);
        Assert.Equal(["1"], (await store.LoadFolderSummariesAsync(acct, "Clients")).Select(s => s.MessageId));
        Assert.Equal(["2"], (await store.LoadFolderSummariesAsync(acct, "Clients/2026")).Select(s => s.MessageId));
        Assert.Equal(["3"], (await store.LoadFolderSummariesAsync(acct, "Clients/2026/Q1")).Select(s => s.MessageId));
        Assert.Empty(await store.LoadFolderSummariesAsync(acct, "Projects"));
        Assert.Equal(["4"], (await store.LoadFolderSummariesAsync(acct, "Projects old")).Select(s => s.MessageId));
        Assert.Equal(["6"], (await store.LoadFolderSummariesAsync(other, "Projects")).Select(s => s.MessageId));
        // The body went with its message, so it opens without downloading again.
        Assert.NotNull(await store.LoadDetailAsync(acct, "Clients", "1"));
        Assert.Null(await store.LoadDetailAsync(acct, "Projects", "1"));
    }

    [Fact]
    public async Task ANameWithSqlWildcards_MatchesOnlyItself()
    {
        var store = new LocalStoreService(new ProfileContext(TempDir()));
        store.Initialize();
        var acct = Guid.NewGuid();
        await store.UpsertSummariesAsync([Summary(acct, "50%_off", "1"), Summary(acct, "50xyoff/Deals", "2")]);

        Assert.Equal(1, await store.RenameFolderAsync(acct, "50%_off", "Sales", '/'));
        Assert.Equal(["1"], (await store.LoadFolderSummariesAsync(acct, "Sales")).Select(s => s.MessageId));
        Assert.Equal(["2"], (await store.LoadFolderSummariesAsync(acct, "50xyoff/Deals")).Select(s => s.MessageId));
    }

    [Fact]
    public async Task ARowAlreadyUnderTheNewName_DoesNotStopTheRename()
    {
        // A folder of the new name that existed once can have left rows behind; the rename replaces
        // them rather than failing on the key.
        var store = new LocalStoreService(new ProfileContext(TempDir()));
        store.Initialize();
        var acct = Guid.NewGuid();
        await store.UpsertSummariesAsync([Summary(acct, "Projects", "1"), Summary(acct, "Clients", "1")]);

        Assert.Equal(1, await store.RenameFolderAsync(acct, "Projects", "Clients", '/'));
        Assert.Single(await store.LoadFolderSummariesAsync(acct, "Clients"));
        Assert.Empty(await store.LoadFolderSummariesAsync(acct, "Projects"));
    }

    [Fact]
    public async Task TheSameName_ChangesNothing()
    {
        var store = new LocalStoreService(new ProfileContext(TempDir()));
        store.Initialize();
        var acct = Guid.NewGuid();
        await store.UpsertSummariesAsync([Summary(acct, "Projects", "1")]);
        Assert.Equal(0, await store.RenameFolderAsync(acct, "Projects", "Projects", '/'));
        Assert.Single(await store.LoadFolderSummariesAsync(acct, "Projects"));
    }

    [Fact]
    public void RememberedListViews_MoveToTheNewNames_AndSurviveARestart()
    {
        var dir = TempDir();
        var acct = Guid.NewGuid();
        var other = Guid.NewGuid();
        var state = new ListState(ViewMode.Conversations, MessageFilter.Unread, null, MessageSort.DateAscending, 7);
        var service = new FolderViewStateService(new ProfileContext(dir));
        service.Remember(acct, "Projects", state);
        service.Remember(acct, "Projects/2026", state);
        service.Remember(acct, "Projects old", state);
        service.Remember(other, "Projects", state);

        var rename = new FolderRename(acct, "Projects", "Clients", '/', "Projects", "Clients");
        service.Rename(acct, rename.Map);

        var reloaded = new FolderViewStateService(new ProfileContext(dir));
        Assert.Equal(state, reloaded.Recall(acct, "Clients"));
        Assert.Equal(state, reloaded.Recall(acct, "Clients/2026"));
        Assert.Null(reloaded.Recall(acct, "Projects"));
        Assert.Equal(state, reloaded.Recall(acct, "Projects old"));
        Assert.Equal(state, reloaded.Recall(other, "Projects"));
    }
}
