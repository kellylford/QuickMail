using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The Outbox tables (#637) hold the only copy of a message written offline, so these run against
/// the real <see cref="LocalStoreService"/> on a temp profile rather than the stub: a hand-written
/// fake store would happily pass tests the shipping SQL fails. What matters most is that a row
/// reopens in the compose window with nothing lost — Bcc, the Markdown source, the mode, the reply
/// linkage and the attachment bytes — because a MIME round-trip loses every one of those.
/// </summary>
public class OutboxStoreTests
{
    private static LocalStoreService NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailOutbox-{Guid.NewGuid():N}");
        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();
        return store;
    }

    private static ComposeModel FullCompose(Guid accountId) => new()
    {
        Kind = ComposeKind.Reply,
        AccountId = accountId,
        To = "to@example.com",
        Cc = "cc@example.com",
        Bcc = "secret@example.com",
        Subject = "Lunch Friday",
        Body = "# Heading\n\nMarkdown *source*",
        Mode = ComposeMode.Markdown,
        HtmlBody = "<html><body><h1>Heading</h1></body></html>",
        InReplyToMessageId = "<parent@example.com>",
        DraftMessageId = "4242",
        DraftFolderName = "Drafts",
        Attachments =
        [
            new AttachmentModel { FileName = "a.txt", ContentType = "text/plain", FileSize = 3, Content = [1, 2, 3] },
            new AttachmentModel { FileName = "b.bin", ContentType = "application/octet-stream", Content = [9, 8, 7, 6] },
            // Never downloaded: nothing to send later, so it must be dropped rather than stored as empty.
            new AttachmentModel { FileName = "ghost.pdf", ContentType = "application/pdf", FileSize = 100, PartSpecifier = "2" },
        ],
    };

    private static OutboxItem Item(Guid accountId, OutboxKind kind, ComposeModel compose) => new()
    {
        Id = OutboxItem.NewId(),
        AccountId = accountId,
        Kind = kind,
        Subject = compose.Subject,
        To = compose.To,
        Cc = compose.Cc,
        Bcc = compose.Bcc,
        ReplaceDraftId = compose.DraftMessageId,
        DraftFolderName = compose.DraftFolderName,
    };

    [Fact]
    public async Task ComposeRoundTripsLosslessly()
    {
        var store = NewStore();
        var account = Guid.NewGuid();
        var compose = FullCompose(account);
        var item = Item(account, OutboxKind.Send, compose);

        await store.UpsertOutboxItemAsync(item, compose);
        var back = await store.LoadOutboxComposeAsync(item.Id);

        Assert.NotNull(back);
        Assert.Equal(ComposeKind.Reply, back.Kind);
        Assert.Equal(account, back.AccountId);
        Assert.Equal("secret@example.com", back.Bcc);
        Assert.Equal("# Heading\n\nMarkdown *source*", back.Body);
        Assert.Equal(ComposeMode.Markdown, back.Mode);
        Assert.Equal(compose.HtmlBody, back.HtmlBody);
        Assert.Equal("<parent@example.com>", back.InReplyToMessageId);
        Assert.Equal("4242", back.DraftMessageId);
        Assert.Equal("Drafts", back.DraftFolderName);
        Assert.Equal(item.Id, back.OutboxId);
    }

    [Fact]
    public async Task AttachmentsRehydrateByteEqualInOrderAndUnloadedOnesAreDropped()
    {
        var store = NewStore();
        var account = Guid.NewGuid();
        var compose = FullCompose(account);
        var item = Item(account, OutboxKind.Draft, compose);

        await store.UpsertOutboxItemAsync(item, compose);
        var back = await store.LoadOutboxComposeAsync(item.Id);

        Assert.NotNull(back);
        Assert.Equal(["a.txt", "b.bin"], back.Attachments.Select(a => a.FileName));
        Assert.Equal([1, 2, 3], back.Attachments[0].Content);
        Assert.Equal([9, 8, 7, 6], back.Attachments[1].Content);
        Assert.Equal("text/plain", back.Attachments[0].ContentType);
        Assert.Equal(3, back.Attachments[0].FileSize);
        // FileSize was 0 on the way in; the stored size is the byte length.
        Assert.Equal(4, back.Attachments[1].FileSize);
        Assert.All(back.Attachments, a => Assert.True(a.IsLoaded));
        Assert.True(item.HasAttachments);
    }

    [Fact]
    public async Task ListingIsNewestFirstWithoutJsonOrBlobs()
    {
        var store = NewStore();
        var account = Guid.NewGuid();
        var older = Item(account, OutboxKind.Draft, new ComposeModel { Subject = "older" });
        older.CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        var newer = Item(account, OutboxKind.Send, new ComposeModel { Subject = "newer" });
        newer.CreatedUtc = DateTimeOffset.UtcNow;

        await store.UpsertOutboxItemAsync(older, new ComposeModel { Subject = "older" });
        await store.UpsertOutboxItemAsync(newer, new ComposeModel { Subject = "newer" });

        var list = await store.LoadOutboxItemsAsync();
        Assert.Equal(["newer", "older"], list.Select(i => i.Subject));
        Assert.Equal(OutboxKind.Send, list[0].Kind);
        Assert.Equal(OutboxState.Pending, list[0].State);
        Assert.False(list[0].HasAttachments);
        Assert.Equal(2, await store.CountOutboxItemsAsync());
    }

    [Fact]
    public async Task UpsertWithTheSameIdReplacesRatherThanDuplicates()
    {
        var store = NewStore();
        var account = Guid.NewGuid();
        var compose = FullCompose(account);
        var item = Item(account, OutboxKind.Draft, compose);
        await store.UpsertOutboxItemAsync(item, compose);

        // Second save: fewer attachments, a new subject, and the kind flips to Send.
        compose.Subject = "Lunch Friday (moved)";
        compose.Attachments.RemoveAt(1);
        item.Subject = compose.Subject;
        item.Kind = OutboxKind.Send;
        await store.UpsertOutboxItemAsync(item, compose);

        var list = await store.LoadOutboxItemsAsync();
        var one = Assert.Single(list);
        Assert.Equal("Lunch Friday (moved)", one.Subject);
        Assert.Equal(OutboxKind.Send, one.Kind);
        var back = await store.LoadOutboxComposeAsync(item.Id);
        Assert.NotNull(back);
        Assert.Equal(["a.txt"], back.Attachments.Select(a => a.FileName));
    }

    [Fact]
    public async Task StateUpdateAndDeleteCascade()
    {
        var store = NewStore();
        var account = Guid.NewGuid();
        var compose = FullCompose(account);
        var item = Item(account, OutboxKind.Send, compose);
        await store.UpsertOutboxItemAsync(item, compose);

        var next = DateTimeOffset.UtcNow.AddMinutes(4);
        await store.UpdateOutboxStateAsync(item.Id, OutboxState.Failed, 3, "550 no such user", next);
        var loaded = await store.LoadOutboxItemAsync(item.Id);
        Assert.NotNull(loaded);
        Assert.Equal(OutboxState.Failed, loaded.State);
        Assert.Equal(3, loaded.Attempts);
        Assert.Equal("550 no such user", loaded.LastError);
        Assert.Equal(next.UtcTicks, loaded.NextAttemptUtc!.Value.UtcTicks);
        Assert.Equal("Failed: 550 no such user", loaded.StateDisplay);

        await store.DeleteOutboxItemAsync(item.Id);
        Assert.Null(await store.LoadOutboxItemAsync(item.Id));
        Assert.Null(await store.LoadOutboxComposeAsync(item.Id));
        Assert.Equal(0, await store.CountOutboxItemsAsync());
        // Deleting again is a no-op, not an error.
        await store.DeleteOutboxItemAsync(item.Id);
    }

    [Fact]
    public async Task AccountDeleteIsScopedAndClearingCacheLeavesTheOutboxAlone()
    {
        var store = NewStore();
        var doomed = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await store.UpsertOutboxItemAsync(Item(doomed, OutboxKind.Send, new ComposeModel { Subject = "d" }), FullCompose(doomed));
        await store.UpsertOutboxItemAsync(Item(kept, OutboxKind.Send, new ComposeModel { Subject = "k" }), FullCompose(kept));

        // The Graph immutable-id rebuild clears cached mail; queued mail is not cache.
        await store.ClearCachedMailAsync([doomed, kept]);
        Assert.Equal(2, await store.CountOutboxItemsAsync());

        await store.DeleteAccountDataAsync(doomed);
        var remaining = Assert.Single(await store.LoadOutboxItemsAsync());
        Assert.Equal(kept, remaining.AccountId);
        var back = await store.LoadOutboxComposeAsync(remaining.Id);
        Assert.NotNull(back);
        Assert.Equal(2, back.Attachments.Count);
    }

    [Theory]
    [InlineData(OutboxKind.Send, OutboxState.Pending, "Waiting to send")]
    [InlineData(OutboxKind.Draft, OutboxState.Pending, "Waiting to upload draft")]
    [InlineData(OutboxKind.Send, OutboxState.Sending, "Sending…")]
    [InlineData(OutboxKind.Draft, OutboxState.Sending, "Uploading draft…")]
    [InlineData(OutboxKind.Send, OutboxState.Failed, "Failed")]
    public void StateDisplayWording(OutboxKind kind, OutboxState state, string expected)
    {
        var item = new OutboxItem { Kind = kind, State = state, Subject = "s" };
        Assert.Equal(expected, item.StateDisplay);
        Assert.Equal($"{expected}: s", item.ToString());
    }
}
