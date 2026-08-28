// Local draft persistence — issue #637.
//
// Runs against a real LocalStoreService on a temp SQLite file rather than a stub, because half of
// what is being asserted IS the storage: the is_pending_upload column, the MIME bytes a draft is
// rebuilt from, and the fact that all of it survives a new service instance the way it has to
// survive a restart. A stub that remembers objects in a dictionary would pass while the schema was
// wrong.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class LocalDraftServiceTests
{
    private const string Drafts = "Drafts";

    private static (LocalDraftService drafts, LocalStoreService store) MakeService()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();
        return (new LocalDraftService(store), store);
    }

    private static AccountModel Account() => new()
    {
        Id = Guid.NewGuid(),
        Username = "me@example.com",
        DisplayName = "Me",
    };

    private static ComposeModel Draft(string subject = "Airport thoughts", string body = "Boarding soon.") => new()
    {
        To      = "someone@example.com",
        Subject = subject,
        Body    = body,
    };

    [Fact]
    public async Task Save_MintsALocalId_AndMarksTheRowPending()
    {
        var (drafts, store) = MakeService();
        var account = Account();

        var saved = await drafts.SaveAsync(account, Draft(), Drafts, null);

        Assert.True(LocalMessageId.IsLocal(saved.MessageId));
        Assert.Null(saved.SupersededServerMessageId);

        var rows = await store.LoadFolderSummariesAsync(account.Id, Drafts);
        var row  = Assert.Single(rows);
        Assert.True(row.IsPendingUpload);
        Assert.Equal("Airport thoughts", row.Subject);
    }

    /// <summary>
    /// The row has to come back pending from a cold read, not just from the instance that wrote it —
    /// the whole promise is that quitting the app does not lose the draft or its status.
    /// </summary>
    [Fact]
    public async Task PendingFlag_SurvivesAReadFromANewServiceInstance()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var profile = new ProfileContext(tempDir);
        var account = Account();

        var writeStore = new LocalStoreService(profile);
        writeStore.Initialize();
        var written = await new LocalDraftService(writeStore).SaveAsync(account, Draft(), Drafts, null);

        var readStore = new LocalStoreService(profile);
        readStore.Initialize();
        var pending = await new LocalDraftService(readStore).GetPendingAsync(account.Id);

        var row = Assert.Single(pending);
        Assert.Equal(written.MessageId, row.MessageId);
        Assert.True(row.IsPendingUpload);
    }

    [Fact]
    public async Task Load_RebuildsTheComposeState()
    {
        var (drafts, _) = MakeService();
        var account = Account();
        var original = new ComposeModel
        {
            To      = "a@example.com",
            Cc      = "b@example.com",
            Subject = "Numbers",
            Body    = "One two three.",
        };

        var saved  = await drafts.SaveAsync(account, original, Drafts, null);
        var loaded = await drafts.LoadAsync(account.Id, Drafts, saved.MessageId);

        Assert.NotNull(loaded);
        Assert.Contains("a@example.com", loaded!.To);
        Assert.Contains("b@example.com", loaded.Cc);
        Assert.Equal("Numbers", loaded.Subject);
        Assert.Contains("One two three.", loaded.Body);
        Assert.Equal(saved.MessageId, loaded.DraftMessageId);
    }

    /// <summary>
    /// Attachment bytes must come back with the draft. A pending draft has never been on a server,
    /// so there is nothing to re-fetch a part from later: if the bytes are not stored, the file is
    /// gone the moment the app closes.
    /// </summary>
    [Fact]
    public async Task Load_ReturnsAttachmentBytes()
    {
        var (drafts, _) = MakeService();
        var account = Account();
        var draft = Draft();
        draft.Attachments.Add(new AttachmentModel
        {
            FileName    = "boarding-pass.txt",
            ContentType = "text/plain",
            Content     = "SEAT 14C"u8.ToArray(),
            FileSize    = 8,
        });

        var saved  = await drafts.SaveAsync(account, draft, Drafts, null);
        var loaded = await drafts.LoadAsync(account.Id, Drafts, saved.MessageId);

        var att = Assert.Single(loaded!.Attachments);
        Assert.Equal("boarding-pass.txt", att.FileName);
        Assert.True(att.IsLoaded);
        Assert.Equal("SEAT 14C", System.Text.Encoding.UTF8.GetString(att.Content!));
    }

    [Fact]
    public async Task Save_ReplacesALocalDraftInPlace_RatherThanAccumulating()
    {
        var (drafts, store) = MakeService();
        var account = Account();

        var first  = await drafts.SaveAsync(account, Draft(subject: "v1"), Drafts, null);
        var second = await drafts.SaveAsync(account, Draft(subject: "v2"), Drafts, first.MessageId);

        Assert.Equal(first.MessageId, second.MessageId);
        var row = Assert.Single(await store.LoadFolderSummariesAsync(account.Id, Drafts));
        Assert.Equal("v2", row.Subject);
    }

    /// <summary>
    /// Editing a server draft offline records which one it replaces, so the eventual upload swaps it
    /// rather than leaving the stale copy behind as a duplicate.
    /// </summary>
    [Fact]
    public async Task Save_OverAServerDraft_RecordsWhatItSupersedes()
    {
        var (drafts, _) = MakeService();
        var account = Account();

        var saved = await drafts.SaveAsync(account, Draft(), Drafts, "server-uid-77");

        Assert.Equal("server-uid-77", saved.SupersededServerMessageId);
        Assert.Equal("server-uid-77",
            await drafts.GetSupersededServerIdAsync(account.Id, Drafts, saved.MessageId));
    }

    /// <summary>
    /// And it must not lose that on the second, third, tenth offline save — auto-save runs on a
    /// timer, so the common case is many saves before the connection returns.
    /// </summary>
    [Fact]
    public async Task Save_CarriesTheSupersededIdForwardAcrossRepeatedLocalSaves()
    {
        var (drafts, _) = MakeService();
        var account = Account();

        var first = await drafts.SaveAsync(account, Draft(subject: "v1"), Drafts, "server-uid-77");
        var later = first;
        for (var i = 2; i <= 4; i++)
            later = await drafts.SaveAsync(account, Draft(subject: $"v{i}"), Drafts, later.MessageId);

        Assert.Equal("server-uid-77", later.SupersededServerMessageId);
    }

    /// <summary>
    /// The superseded server draft's row goes away, so the user is not offered a stale copy next to
    /// the fresh one with nothing in the list to tell them apart.
    /// </summary>
    [Fact]
    public async Task Save_OverAServerDraft_RemovesTheSupersededRow()
    {
        var (drafts, store) = MakeService();
        var account = Account();
        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId  = "server-uid-77",
            AccountId  = account.Id,
            FolderName = Drafts,
            Subject    = "the copy on the server",
            Date       = DateTimeOffset.UtcNow,
        }]);

        await drafts.SaveAsync(account, Draft(), Drafts, "server-uid-77");

        var rows = await store.LoadFolderSummariesAsync(account.Id, Drafts);
        Assert.DoesNotContain(rows, r => r.MessageId == "server-uid-77");
        Assert.Single(rows);
    }

    [Fact]
    public async Task Discard_RemovesThePendingDraft()
    {
        var (drafts, _) = MakeService();
        var account = Account();
        var saved = await drafts.SaveAsync(account, Draft(), Drafts, null);

        await drafts.DiscardAsync(account.Id, Drafts, saved.MessageId);

        Assert.Empty(await drafts.GetPendingAsync(account.Id));
    }

    [Fact]
    public async Task Load_ReturnsNull_ForAServerId()
        => Assert.Null(await (MakeService().drafts).LoadAsync(Guid.NewGuid(), Drafts, "12345"));

    [Fact]
    public async Task GetPending_IsScopedToOneAccount()
    {
        var (drafts, _) = MakeService();
        var mine = Account();
        var theirs = Account();

        await drafts.SaveAsync(mine, Draft(), Drafts, null);
        await drafts.SaveAsync(theirs, Draft(), Drafts, null);

        Assert.Single(await drafts.GetPendingAsync(mine.Id));
    }

    [Fact]
    public async Task ResolveDraftsFolderName_ReadsTheCachedFolderList()
    {
        var (drafts, store) = MakeService();
        var account = Account();
        await store.SaveFoldersAsync(account.Id,
        [
            new MailFolderModel { FullName = "INBOX",  DisplayName = "Inbox",  Kind = SpecialFolderKind.Inbox },
            new MailFolderModel { FullName = "INBOX.Drafts", DisplayName = "Drafts", Kind = SpecialFolderKind.Drafts },
        ]);

        Assert.Equal("INBOX.Drafts", await drafts.ResolveDraftsFolderNameAsync(account.Id));
    }

    /// <summary>
    /// An account whose folders have never synced has nowhere to file a draft, and the caller has to
    /// be able to tell that apart from a successful resolve — it is the one case that still refuses
    /// to save.
    /// </summary>
    [Fact]
    public async Task ResolveDraftsFolderName_IsNull_WhenNothingIsCached()
        => Assert.Null(await (MakeService().drafts).ResolveDraftsFolderNameAsync(Guid.NewGuid()));
}
