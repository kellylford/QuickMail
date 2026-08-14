using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// POP3 backend (#128) behavior that does not need a server: the local half of the protocol — how a
/// downloaded message maps onto QuickMail's models, how the synthetic folders behave, and above all
/// the sync contract that keeps <c>SyncService</c> from deleting mail that exists nowhere else.
///
/// <para>These run against the REAL <see cref="LocalStoreService"/> on a temp profile rather than a
/// stub. That is deliberate: the flag and read-state semantics under test are enforced by the
/// upsert SQL, so a hand-written fake store would happily pass tests the shipping code fails.</para>
///
/// <para>The protocol half — RETR, UIDL dedup across sessions, DELE — is covered against a real
/// server in QuickMail.IntegrationTests/Pop3ProtocolTests.cs.</para>
/// </summary>
public class Pop3MailServiceTests
{
    private static LocalStoreService NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailPop3-{Guid.NewGuid():N}");
        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();
        return store;
    }

    private static MailMessageSummary Msg(Guid account, string id, string folder,
                                          DateTimeOffset? date = null, bool isRead = false) => new()
    {
        MessageId  = id,
        AccountId  = account,
        FolderName = folder,
        From       = "Sender <sender@example.com>",
        To         = "me@example.com",
        Subject    = $"Message {id}",
        Date       = date ?? DateTimeOffset.UtcNow,
        IsRead     = isRead,
        Preview    = "preview text",
    };

    private static MimeMessage BuildMime(
        string subject = "Hello",
        string? text = "Line one\nLine two",
        string? html = null,
        string? messageId = "<abc@example.com>",
        Action<MimeMessage>? customize = null)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Kelly Ford", "kelly@example.com"));
        msg.To.Add(new MailboxAddress("Recipient Name", "recipient@example.com"));
        msg.Subject = subject;
        msg.Date = new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.Zero);
        if (messageId is not null) msg.MessageId = messageId.Trim('<', '>');

        var body = new BodyBuilder();
        if (text is not null) body.TextBody = text;
        if (html is not null) body.HtmlBody = html;
        msg.Body = body.ToMessageBody();

        customize?.Invoke(msg);
        return msg;
    }

    // ── The sync contract (#462) ─────────────────────────────────────────────────
    // SyncService routes every POP3 folder through its id-diff sweep, which deletes cached ids the
    // backend's listing does not mention. For POP3 the cache is the only copy of the mail, so these
    // are the tests that stand between the sweep and a user's mailbox.

    [Fact]
    public async Task IdDateListing_IncludesEveryCachedId_SoTheDeletionReconcileCanNeverPurge()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([
            Msg(account, "uidl-1", "Inbox"),
            Msg(account, "uidl-2", "Inbox"),
        ]);

        // No ConnectAsync, so the server half contributes nothing — exactly the state a POP3 server
        // that has already dropped the mail would produce.
        var listing = await pop.GetFolderMessageIdDatesAsync(account, "Inbox");

        var listed = listing.Select(l => l.Id).ToHashSet();
        Assert.Contains("uidl-1", listed);
        Assert.Contains("uidl-2", listed);

        // The sweep's deletion reconcile is (cached ids) minus (listed ids). It must come out empty.
        var cached = await store.GetAllMessageIdsAsync(account, "Inbox");
        Assert.Empty(cached.Except(listed));
    }

    [Fact]
    public async Task IdDateListing_ReportsCachedReadState_SoTheReadReconcileIsANoOp()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([
            Msg(account, "read-one",   "Inbox", isRead: true),
            Msg(account, "unread-one", "Inbox", isRead: false),
        ]);

        var listing = await pop.GetFolderMessageIdDatesAsync(account, "Inbox");

        // POP3 has no server-side read state. Reporting anything but the cached value would make the
        // sweep "reconcile" a message the user has read straight back to unread on every cycle.
        Assert.True(listing.Single(l => l.Id == "read-one").IsRead);
        Assert.False(listing.Single(l => l.Id == "unread-one").IsRead);
    }

    [Fact]
    public void MergeListing_KeepsEveryCachedId_EvenWhenTheServerListsNothing()
    {
        // The listing the sweep consumes, exercised directly — the service-level test above cannot
        // reach the merge without a live server, so this is what pins the arithmetic.
        var account = Guid.NewGuid();
        var cached  = new[] { Msg(account, "uidl-1", "Inbox"), Msg(account, "uidl-2", "Inbox") };

        var merged = Pop3MailService.MergeListing(cached, [], DateTimeOffset.UtcNow);

        Assert.Equal(["uidl-1", "uidl-2"], merged.Select(m => m.Id));
    }

    [Fact]
    public void MergeListing_AddsUnseenServerIdsAsArrivingNow()
    {
        var account = Guid.NewGuid();
        var now     = DateTimeOffset.UtcNow;
        var cached  = new[] { Msg(account, "known", "Inbox", now.AddDays(-2), isRead: true) };

        var merged = Pop3MailService.MergeListing(cached, ["known", "brand-new"], now);

        // The known id appears once, with its own date and read state.
        var known = Assert.Single(merged.Where(m => m.Id == "known"));
        Assert.True(known.IsRead);
        Assert.Equal(now.AddDays(-2), known.ReceivedUtc);

        // The unseen one is stamped now and unread, so it lands inside the sweep's window and gets
        // fetched. Its real date is inside the message, which POP3 cannot show without downloading it.
        var fresh = Assert.Single(merged.Where(m => m.Id == "brand-new"));
        Assert.Equal(now, fresh.ReceivedUtc);
        Assert.False(fresh.IsRead);
    }

    [Fact]
    public void MergeListing_NeverDropsACachedIdTheServerHasForgotten()
    {
        // With leave-on-server off, this is the steady state seconds after collection: the server
        // lists nothing QuickMail holds. Dropping those ids here is what would delete the mail.
        var account = Guid.NewGuid();
        var cached  = new[] { Msg(account, "collected-1", "Inbox"), Msg(account, "collected-2", "Inbox") };

        var merged = Pop3MailService.MergeListing(cached, ["a-totally-different-uidl"], DateTimeOffset.UtcNow);
        var listed = merged.Select(m => m.Id).ToHashSet();

        Assert.Contains("collected-1", listed);
        Assert.Contains("collected-2", listed);
        Assert.Contains("a-totally-different-uidl", listed);
    }

    [Fact]
    public async Task FolderMessageIds_ComeFromTheStore_NotTheServer()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([Msg(account, "uidl-1", "Inbox")]);

        // ReconcileFolderAsync (the pre-#462 path, taken when a server issues numeric UIDLs) diffs
        // the cache against this. Store-backed means it diffs the cache against itself.
        var ids = await pop.GetFolderMessageIdsAsync(account, "Inbox");
        Assert.Equal(["uidl-1"], ids);
    }

    [Fact]
    public async Task SyncFetch_RestatesLocalFlags_SoASweepDoesNotClearThem()
    {
        // UpsertSummariesAsync derives the stored flag from IsServerFlagged — an IMAP rule, where the
        // server is the authority. SyncService upserts whatever a fetch returns, so a POP3 fetch that
        // returned rows with IsServerFlagged=false would clear the user's flags on every sweep.
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([Msg(account, "uidl-1", "Sent")]);
        await store.UpdateFlagIdAsync(account, "Sent", "uidl-1", FlagDefinition.BuiltInFlagId.ToString());

        // Sent is a synthetic folder, so this reads the store without touching a server.
        var fetched = await pop.GetMessagesSinceDateAsync(account, "Sent", DateTime.UtcNow.AddDays(-30));

        Assert.True(fetched.Single().IsServerFlagged);

        // Simulate what SyncService does with the batch, then confirm the flag survived.
        await store.UpsertSummariesAsync(fetched);
        var reloaded = await store.LoadFolderSummariesAsync(account, "Sent");
        Assert.Equal(FlagDefinition.BuiltInFlagId.ToString(), reloaded.Single().FlagId);
    }

    [Fact]
    public async Task SyncFetch_HonoursTheWindow()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([
            Msg(account, "recent", "Sent", DateTimeOffset.UtcNow.AddDays(-1)),
            Msg(account, "old",    "Sent", DateTimeOffset.UtcNow.AddDays(-90)),
        ]);

        var fetched = await pop.GetMessagesSinceDateAsync(account, "Sent", DateTime.UtcNow.AddDays(-30));

        // Old mail stays in the store and stays visible in the folder; it is simply not announced as
        // an arrival, so a backlog does not produce a toast per message.
        Assert.Equal(["recent"], fetched.Select(f => f.MessageId));
        Assert.Equal(2, (await store.GetAllMessageIdsAsync(account, "Sent")).Count);
    }

    // ── Synthetic folders ────────────────────────────────────────────────────────

    [Fact]
    public async Task Folders_AreTheFourSyntheticOnes_WithCountsFromTheStore()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([
            Msg(account, "a", "Inbox", isRead: false),
            Msg(account, "b", "Inbox", isRead: true),
            Msg(account, "c", "Trash", isRead: false),
        ]);

        var folders = await pop.GetFoldersAsync(account);

        Assert.Equal(["Inbox", "Sent", "Drafts", "Trash"], folders.Select(f => f.FullName));
        Assert.Equal(SpecialFolderKind.Inbox,  folders[0].Kind);
        Assert.Equal(SpecialFolderKind.Sent,   folders[1].Kind);
        Assert.Equal(SpecialFolderKind.Drafts, folders[2].Kind);
        Assert.Equal(SpecialFolderKind.Trash,  folders[3].Kind);

        // Counts come from the store because there is no server-side STATUS to ask.
        Assert.Equal(2, folders[0].MessageCount);
        Assert.Equal(1, folders[0].UnreadCount);
        Assert.Equal(1, folders[3].MessageCount);

        // Everything but the Inbox is excluded from the All Mail aggregate, as on IMAP.
        Assert.False(folders[0].ExcludeFromAllMail);
        Assert.True(folders.Skip(1).All(f => f.ExcludeFromAllMail));
    }

    [Fact]
    public async Task FolderCrud_IsRefused()
    {
        var pop = new Pop3MailService(NewStore());
        var id  = Guid.NewGuid();

        await Assert.ThrowsAsync<NotSupportedException>(() => pop.CreateFolderAsync(id, null, "New"));
        await Assert.ThrowsAsync<NotSupportedException>(() => pop.DeleteFolderAsync(id, "Inbox"));
        await Assert.ThrowsAsync<NotSupportedException>(() => pop.RenameFolderAsync(id, "Inbox", "X", null));
        await Assert.ThrowsAsync<NotSupportedException>(() => pop.CopyFolderAsync(id, "Inbox", null));
    }

    // ── Local move / copy ────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveToTrash_MovesTheWholeMessage_AndKeepsItsFlag()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);
        var flag    = Guid.NewGuid().ToString();   // a named flag, not the built-in one

        await store.UpsertSummariesAsync([Msg(account, "uidl-1", "Inbox")]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "uidl-1", AccountId = account, FolderName = "Inbox",
            PlainTextBody = "the body", Attachments = [new AttachmentModel { FileName = "a.pdf", PartSpecifier = "0" }],
        });
        await store.StoreMimeBytesAsync(account, "Inbox", "uidl-1", [1, 2, 3]);
        await store.UpdateFlagIdAsync(account, "Inbox", "uidl-1", flag);

        await pop.MoveToTrashAsync(account, "Inbox", "uidl-1");

        Assert.Empty(await store.GetAllMessageIdsAsync(account, "Inbox"));
        var trashed = (await store.LoadFolderSummariesAsync(account, "Trash")).Single();
        Assert.Equal("uidl-1", trashed.MessageId);
        // A named flag survives the move: the upsert can only carry the built-in id, so the service
        // restates it explicitly.
        Assert.Equal(flag, trashed.FlagId);

        var detail = await store.LoadDetailAsync(account, "Trash", "uidl-1");
        Assert.NotNull(detail);
        Assert.Equal("the body", detail!.PlainTextBody);
        // The raw bytes travel too, or the attachment becomes unopenable by being filed.
        Assert.Equal([1, 2, 3], await store.LoadMimeBytesAsync(account, "Trash", "uidl-1"));
    }

    [Fact]
    public async Task Copy_LeavesTheSourceInPlace()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([Msg(account, "uidl-1", "Inbox")]);
        await pop.CopyMessagesAsync(account, "Inbox", ["uidl-1"], "Sent");

        Assert.Single(await store.GetAllMessageIdsAsync(account, "Inbox"));
        Assert.Single(await store.GetAllMessageIdsAsync(account, "Sent"));
    }

    [Fact]
    public async Task PermanentDelete_RemovesFromTheStore_WhenTheServerIsNotInvolved()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([Msg(account, "uidl-1", "Trash")]);

        // The account is not registered, so there is no server leg to run — and no exception either:
        // the local delete must still happen.
        await pop.PermanentlyDeleteBatchAsync(account, "Trash", ["uidl-1"]);

        Assert.Empty(await store.GetAllMessageIdsAsync(account, "Trash"));
    }

    [Fact]
    public async Task EmptyTrash_ReportsWhatItRemoved()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([Msg(account, "a", "Trash"), Msg(account, "b", "Trash")]);

        Assert.Equal(2, await pop.CountTrashMessagesAsync(account));
        Assert.Equal(2, await pop.EmptyTrashAsync(account));
        Assert.Equal(0, await pop.CountTrashMessagesAsync(account));
    }

    // ── Local mail (sent, drafts) ────────────────────────────────────────────────

    [Fact]
    public async Task Draft_IsStoredLocally_UnderAnIdThatIsNotAUidl()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        var id = await pop.AppendDraftAsync(account, new ComposeModel
        {
            To = "someone@example.com", Subject = "Draft subject", Body = "draft body",
        }, replaceMessageId: null);

        Assert.StartsWith(Pop3MailService.LocalIdPrefix, id);
        var drafts = await store.LoadFolderSummariesAsync(account, "Drafts");
        Assert.Equal("Draft subject", drafts.Single().Subject);
        // Locally-authored mail is not unread mail — it would otherwise inflate the Drafts badge.
        Assert.True(drafts.Single().IsRead);
    }

    [Fact]
    public async Task ReplacingADraft_LeavesOnlyTheNewOne()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        var first  = await pop.AppendDraftAsync(account, new ComposeModel { Subject = "v1" }, null);
        var second = await pop.AppendDraftAsync(account, new ComposeModel { Subject = "v2" }, first);

        var drafts = await store.LoadFolderSummariesAsync(account, "Drafts");
        Assert.Equal(second, drafts.Single().MessageId);
        Assert.Equal("v2", drafts.Single().Subject);
    }

    [Fact]
    public async Task SentMail_LandsInTheSyntheticSentFolder()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await pop.AppendToSentAsync(account, new ComposeModel
        {
            To = "someone@example.com", Subject = "Sent subject", Body = "sent body",
        });

        Assert.Equal("Sent subject", (await store.LoadFolderSummariesAsync(account, "Sent")).Single().Subject);
    }

    // ── Reading ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpeningAMessageThatWasNeverDownloaded_SaysSo()
    {
        var pop = new Pop3MailService(NewStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pop.GetMessageDetailAsync(Guid.NewGuid(), "Inbox", "uidl-1"));

        // Never a silent empty state: the message says what is wrong.
        Assert.Contains("local store", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkRead_AndFlag_AreLocalOnly()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([Msg(account, "uidl-1", "Inbox")]);

        await pop.MarkReadAsync(account, "Inbox", "uidl-1");
        await pop.SetMessageFlaggedAsync(account, "Inbox", "uidl-1", flagged: true);

        var row = (await store.LoadFolderSummariesAsync(account, "Inbox")).Single();
        Assert.True(row.IsRead);
        Assert.Equal(FlagDefinition.BuiltInFlagId.ToString(), row.FlagId);

        await pop.SetMessageFlaggedAsync(account, "Inbox", "uidl-1", flagged: false);
        Assert.Null((await store.LoadFolderSummariesAsync(account, "Inbox")).Single().FlagId);
    }

    [Fact]
    public async Task InboxStatus_AndPreviews_ComeFromTheStore()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        await store.UpsertSummariesAsync([
            Msg(account, "a", "Inbox", isRead: true),
            Msg(account, "b", "Inbox", isRead: false),
        ]);

        Assert.Equal((2, 1), await pop.GetInboxStatusAsync(account));

        var previews = await pop.FetchPreviewsAsync(account, "Inbox", ["a", "b"], maxLines: 3);
        Assert.Equal("preview text", previews["a"]);
    }

    [Fact]
    public async Task NoOp_DoesNothing_AndDisposeIsSafe()
    {
        var pop = new Pop3MailService(NewStore());
        await pop.NoOpAsync(Guid.NewGuid());   // no persistent connection to keep alive
        pop.Dispose();
        pop.Dispose();                          // idempotent
    }

    [Fact]
    public void IsConnected_IsFalseUntilTheAccountAuthenticates()
        => Assert.False(new Pop3MailService(NewStore()).IsConnected(Guid.NewGuid()));

    [Fact]
    public async Task Connect_WithoutAPassword_SaysWhatIsMissing()
    {
        var pop = new Pop3MailService(NewStore());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pop.ConnectAsync(new AccountModel { Username = "me@example.com" }, password: null));

        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Mapping a downloaded message onto the models ─────────────────────────────

    [Fact]
    public void Summary_MatchesTheImapBackendsConventions()
    {
        var account = Guid.NewGuid();
        var msg     = BuildMime(subject: "Quarterly report");

        var summary = Pop3MailService.BuildSummary(account, "uidl-1", msg, "Inbox", isRead: false);

        Assert.Equal("uidl-1", summary.MessageId);
        // Display name in the list, like ImapMailService.SummaryToModel.
        Assert.Equal("Kelly Ford", summary.From);
        Assert.Equal("Quarterly report", summary.Subject);
        // Carried so aggregate views can collapse duplicates and a reply can thread (#220).
        Assert.Equal("abc@example.com", summary.InternetMessageId);
        Assert.Equal("Line one Line two", summary.Preview);
        Assert.False(summary.IsRead);
    }

    [Fact]
    public void Summary_FallsBackForAMessageWithNoSubjectAndNoDate()
    {
        var msg = BuildMime(subject: "");
        msg.Date = default;

        var summary = Pop3MailService.BuildSummary(Guid.NewGuid(), "uidl-1", msg, "Inbox", isRead: false);

        Assert.Equal("(no subject)", summary.Subject);
        // Not DateTimeOffset.MinValue: an undated message sorts to the top and counts as arriving
        // now, which is what the sweep's window test needs it to be.
        Assert.True(summary.Date > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void Summary_DetectsAMailingList()
    {
        var msg = BuildMime(customize: m => m.Headers.Add("List-Id", "<quickmail.example.com>"));
        Assert.True(Pop3MailService.BuildSummary(Guid.NewGuid(), "u", msg, "Inbox", false).IsMailingList);
        Assert.False(Pop3MailService.BuildSummary(Guid.NewGuid(), "u", BuildMime(), "Inbox", false).IsMailingList);
    }

    [Fact]
    public void Preview_FallsBackToTheHtmlBody()
    {
        var msg = BuildMime(text: null, html: "<p>Hello <b>there</b></p>");
        var preview = Pop3MailService.BuildSummary(Guid.NewGuid(), "u", msg, "Inbox", false).Preview;

        Assert.Contains("Hello", preview);
        Assert.DoesNotContain("<p>", preview);
    }

    [Fact]
    public void Detail_CarriesFullAddresses_AndTheComposeMode()
    {
        var msg = BuildMime(customize: m =>
        {
            m.Cc.Add(new MailboxAddress("Cc Person", "cc@example.com"));
            m.ReplyTo.Add(new MailboxAddress("Reply Person", "reply@example.com"));
            m.Headers.Add("X-QuickMail-Compose-Mode", "markdown");
        });

        var detail = Pop3MailService.BuildDetail(Guid.NewGuid(), "u", msg, "Drafts", isRead: true);

        Assert.Contains("kelly@example.com", detail.From);
        Assert.Contains("cc@example.com", detail.Cc);
        Assert.Contains("reply@example.com", detail.ReplyTo);
        Assert.Equal(ComposeMode.Markdown, detail.DraftComposeMode);
    }

    [Fact]
    public void Detail_MeasuresAttachmentsAndIndexesThemForLaterExtraction()
    {
        var payload = new byte[5000];
        Array.Fill(payload, (byte)7);

        var msg = BuildMime(customize: m =>
        {
            var builder = new BodyBuilder { TextBody = "see attached" };
            builder.Attachments.Add("report.pdf", payload, new ContentType("application", "pdf"));
            m.Body = builder.ToMessageBody();
        });

        var detail = Pop3MailService.BuildDetail(Guid.NewGuid(), "u", msg, "Inbox", isRead: false);

        var attachment = Assert.Single(detail.Attachments);
        Assert.Equal("report.pdf", attachment.FileName);
        // The DECODED size. Reporting the base64 length would overstate the file by a third.
        Assert.Equal(payload.Length, attachment.FileSize);
        // Index into MimeMessage.Attachments, which is what DownloadAttachmentAsync walks.
        Assert.Equal("0", attachment.PartSpecifier);
        Assert.True(detail.HasAttachments);
    }

    [Fact]
    public void Detail_ParsesACalendarInvite()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            METHOD:REQUEST
            BEGIN:VEVENT
            UID:invite-1
            SUMMARY:Design review
            DTSTART:20260901T150000Z
            DTEND:20260901T160000Z
            END:VEVENT
            END:VCALENDAR
            """;

        var msg = BuildMime(customize: m =>
        {
            var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "invite" } };
            mixed.Add(new TextPart("calendar") { Text = ics });
            m.Body = mixed;
        });

        var detail = Pop3MailService.BuildDetail(Guid.NewGuid(), "u", msg, "Inbox", isRead: false);

        // The raw ICS is what gets cached, and for POP3 the cache is the only copy — an invite whose
        // card came only from a live parse would vanish the moment it was stored (#297).
        Assert.Contains("Design review", detail.CalendarIcs);
        Assert.NotNull(detail.CalendarInvite);
    }

    [Fact]
    public async Task Attachment_IsExtractedFromTheStoredBytes()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);
        var payload = new byte[] { 10, 20, 30, 40 };

        var msg = BuildMime(customize: m =>
        {
            var builder = new BodyBuilder { TextBody = "see attached" };
            builder.Attachments.Add("data.bin", payload, new ContentType("application", "octet-stream"));
            m.Body = builder.ToMessageBody();
        });

        await store.UpsertDetailAsync(Pop3MailService.BuildDetail(account, "uidl-1", msg, "Inbox", false));
        using (var ms = new MemoryStream())
        {
            await msg.WriteToAsync(ms);
            await store.StoreMimeBytesAsync(account, "Inbox", "uidl-1", ms.ToArray());
        }

        var extracted = await pop.DownloadAttachmentAsync(account, "Inbox", "uidl-1", "0");
        Assert.Equal(payload, extracted);
    }

    [Fact]
    public async Task Attachment_WithNoStoredBytes_SaysWhyItCannotBeFetched()
    {
        var pop = new Pop3MailService(NewStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pop.DownloadAttachmentAsync(Guid.NewGuid(), "Inbox", "uidl-1", "0"));

        Assert.Contains("re-fetch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── --online mode (no local store) ───────────────────────────────────────────

    [Fact]
    public async Task OnlineMode_ReadsAreEmptyRatherThanAStoreError()
    {
        // --online skips LocalStoreService.Initialize(), so every table is missing. Reading anyway
        // put "SqliteException: no such table: MessageSummary" in the log on every aggregate load —
        // seen for real when the app was run with --online and a POP3 account (#128).
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store, onlineMode: true);

        Assert.Empty(await pop.GetMessageSummariesAsync(account, "Inbox", 50));
        Assert.Empty(await pop.GetMessagesSinceDateAsync(account, "Inbox", DateTime.UtcNow.AddDays(-30)));
        Assert.Empty(await pop.GetMessagesSinceAsync(account, "Inbox", "0", 50));
        Assert.Empty(await pop.GetFolderMessageIdsAsync(account, "Inbox"));
        Assert.Empty(await pop.GetFolderMessageIdDatesAsync(account, "Inbox"));
        Assert.Empty(await pop.FetchPreviewsAsync(account, "Inbox", ["a"], 3));
        Assert.Equal((0, 0), await pop.GetInboxStatusAsync(account));
        Assert.Equal(0, await pop.CountTrashMessagesAsync(account));
        Assert.Equal(0, await pop.EmptyTrashAsync(account));
    }

    [Fact]
    public async Task OnlineMode_StillListsTheFolders_SoTheAccountIsNotAnEmptyNode()
    {
        var pop = new Pop3MailService(NewStore(), onlineMode: true);
        var folders = await pop.GetFoldersAsync(Guid.NewGuid());

        Assert.Equal(["Inbox", "Sent", "Drafts", "Trash"], folders.Select(f => f.FullName));
        Assert.All(folders, f => Assert.Equal(0, f.MessageCount));
    }

    [Fact]
    public async Task OnlineMode_MutationsAreNoOps_AndReadsSayWhy()
    {
        var pop     = new Pop3MailService(NewStore(), onlineMode: true);
        var account = Guid.NewGuid();

        // No throw: a mark-read on a message that cannot exist is simply nothing to do.
        await pop.MarkReadAsync(account, "Inbox", "uidl-1");
        await pop.MarkReadBatchAsync(account, "Inbox", ["uidl-1"]);
        await pop.SetMessageFlaggedAsync(account, "Inbox", "uidl-1", flagged: true);
        await pop.MoveToTrashAsync(account, "Inbox", "uidl-1");
        await pop.PermanentlyDeleteBatchAsync(account, "Trash", ["uidl-1"]);
        await pop.AppendToSentAsync(account, new ComposeModel { Subject = "sent" });

        // Opening one, though, has to explain itself rather than fail as a database error.
        var open = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pop.GetMessageDetailAsync(account, "Inbox", "uidl-1"));
        Assert.Contains("--online", open.Message, StringComparison.Ordinal);

        var attach = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pop.DownloadAttachmentAsync(account, "Inbox", "uidl-1", "0"));
        Assert.Contains("--online", attach.Message, StringComparison.Ordinal);

        var draft = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pop.AppendDraftAsync(account, new ComposeModel { Subject = "draft" }, null));
        Assert.Contains("--online", draft.Message, StringComparison.Ordinal);
    }

    // ── The store columns POP3 depends on ────────────────────────────────────────

    [Fact]
    public async Task StoredMessageBytes_RoundTrip_AndAreDroppedWithTheMessage()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var bytes   = new byte[] { 1, 2, 3, 4, 5 };

        await store.UpsertSummariesAsync([Msg(account, "uidl-1", "Inbox")]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "uidl-1", AccountId = account, FolderName = "Inbox", PlainTextBody = "body",
        });

        await store.StoreMimeBytesAsync(account, "Inbox", "uidl-1", bytes);
        Assert.Equal(bytes, await store.LoadMimeBytesAsync(account, "Inbox", "uidl-1"));

        // The bytes live on the detail row, so deleting the message takes them with it rather than
        // leaving orphaned blobs in the database.
        await store.DeleteSummariesAsync(account, "Inbox", ["uidl-1"]);
        Assert.Null(await store.LoadMimeBytesAsync(account, "Inbox", "uidl-1"));
    }

    [Fact]
    public async Task NoStoredBytes_ReadsAsNull_ForIMapAndGraphMessages()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();

        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "42", AccountId = account, FolderName = "INBOX", PlainTextBody = "body",
        });

        Assert.Null(await store.LoadMimeBytesAsync(account, "INBOX", "42"));
    }

    [Fact]
    public async Task CachedDetail_CarriesTheMessageId_SoAReplyThreads()
    {
        // ComposeViewModel puts MailMessageDetail.InternetMessageId in In-Reply-To. A cache-served
        // open used to hand it an empty string; for POP3 the cache is the only copy, so nothing
        // would ever fill it in later.
        var store   = NewStore();
        var account = Guid.NewGuid();

        var summary = Msg(account, "uidl-1", "Inbox");
        summary.InternetMessageId = "original@example.com";
        await store.UpsertSummariesAsync([summary]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "uidl-1", AccountId = account, FolderName = "Inbox", PlainTextBody = "body",
        });

        var loaded = await store.LoadDetailAsync(account, "Inbox", "uidl-1");

        Assert.NotNull(loaded);
        Assert.Equal("original@example.com", loaded!.InternetMessageId);
    }
}
