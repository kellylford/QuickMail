using System;
using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class LocalStoreServiceTests
{
    [Fact]
    public async Task SummaryToField_PersistsAndLoads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var summary = new MailMessageSummary
        {
            MessageId = "42",
            AccountId = Guid.NewGuid(),
            FolderName = "Inbox",
            From = "Sender <sender@example.com>",
            To = "Long Recipient Name <recipient@example.com>",
            Subject = "Test subject",
            Date = DateTimeOffset.UtcNow,
            Preview = "Preview",
        };

        await store.UpsertSummariesAsync([summary]);
        var loaded = await store.LoadAllSummariesAsync();

        Assert.Single(loaded);
        Assert.Equal(summary.To, loaded[0].To);
    }

    [Fact]
    public async Task DetailFrom_RoundTripsTheFullAddress_NotTheSummaryDisplayName()
    {
        // Issue #636: the detail row had no From column, so a cache-served open handed the reading
        // pane, Properties and the reply To field the summary's display-name-only value. Opening the
        // same message straight from IMAP gave the full address — the "sometimes" in the report.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId = "7", AccountId = acct, FolderName = "Inbox",
            From = "Kelly Ford",                       // display name alone, as the list column wants
            Subject = "s", Date = DateTimeOffset.UtcNow,
        }]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "7", AccountId = acct, FolderName = "Inbox",
            From = "Kelly Ford <kelly@example.com>",
            To = "you@example.com", PlainTextBody = "body",
        });

        var loaded = await store.LoadDetailAsync(acct, "Inbox", "7");
        Assert.NotNull(loaded);
        Assert.Equal("Kelly Ford <kelly@example.com>", loaded!.From);
    }

    [Fact]
    public async Task DetailFrom_FallsBackToTheSummary_ForRowsWrittenBeforeFromAddrExisted()
    {
        // A detail row cached by an older build has from_addr = '', and here the summary has no from_email yet
        // either, so the address is stored nowhere in the database. Serving the summary's display name keeps
        // such a message readable; MainViewModel re-fetches it on open to fill the address back in.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId = "8", AccountId = acct, FolderName = "Inbox",
            From = "Kelly Ford", Subject = "s", Date = DateTimeOffset.UtcNow,
        }]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "8", AccountId = acct, FolderName = "Inbox",
            From = string.Empty,                       // what a pre-#636 row looks like on disk
            To = "you@example.com", PlainTextBody = "body",
        });

        var loaded = await store.LoadDetailAsync(acct, "Inbox", "8");
        Assert.NotNull(loaded);
        Assert.Equal("Kelly Ford", loaded!.From);
    }

    [Fact]
    public async Task DetailFrom_ForRowsWrittenBeforeFromAddrExisted_KeepsTheAddressTheSummaryHolds()
    {
        // Review of #806: an older Graph row used to fall back to its from_disp "Kelly Ford <kelly@example.com>".
        // The backfill splits that into from_disp "Kelly Ford" and from_email, so the fallback reads both — or an
        // offline reply, with no re-fetch to repair it, would be addressed to a bare name.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId = "9", AccountId = acct, FolderName = "Inbox",
            From = "Kelly Ford", FromEmail = "kelly@example.com", Subject = "s", Date = DateTimeOffset.UtcNow,
        }]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "9", AccountId = acct, FolderName = "Inbox",
            From = string.Empty,                       // what a pre-#636 row looks like on disk
            To = "you@example.com", PlainTextBody = "body",
        });

        var loaded = await store.LoadDetailAsync(acct, "Inbox", "9");
        Assert.NotNull(loaded);
        Assert.Equal("Kelly Ford <kelly@example.com>", loaded!.From);
    }

    [Fact]
    public async Task DetailFrom_FallbackWithACommaInTheName_ParsesAsOneMailbox()
    {
        // Review of #806: "Ford, Kelly <kelly@example.com>" unquoted is two addresses to a reply's To box, and the
        // address in it stops the re-fetch that used to repair the row. The fallback is built as a header.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId = "10", AccountId = acct, FolderName = "Inbox",
            From = "Ford, Kelly", FromEmail = "kelly@example.com", Subject = "s", Date = DateTimeOffset.UtcNow,
        }]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "10", AccountId = acct, FolderName = "Inbox",
            From = string.Empty, To = "you@example.com", PlainTextBody = "body",
        });

        var loaded = await store.LoadDetailAsync(acct, "Inbox", "10");

        Assert.NotNull(loaded);
        Assert.True(MimeKit.InternetAddressList.TryParse(loaded!.From, out var list));
        var mailbox = Assert.IsType<MimeKit.MailboxAddress>(Assert.Single(list));
        Assert.Equal("Ford, Kelly", mailbox.Name);
        Assert.Equal("kelly@example.com", mailbox.Address);
    }

    [Fact]
    public async Task CountSummariesByFolder_GroupsPerFolderForOneAccount()
    {
        // #462 sweep instrumentation: one grouped query yields every folder's cached count.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        MailMessageSummary Msg(string id, string folder) => new()
        {
            MessageId = id, AccountId = acct, FolderName = folder,
            From = "a@b.com", Subject = "s", Date = DateTimeOffset.UtcNow,
        };
        await store.UpsertSummariesAsync([Msg("1", "Inbox"), Msg("2", "Inbox"), Msg("3", "Archive")]);

        var counts = await store.CountSummariesByFolderAsync(acct);
        Assert.Equal(2, counts["Inbox"]);
        Assert.Equal(1, counts["Archive"]);
        Assert.False(counts.ContainsKey("Sent"));                                   // empty folder absent from the map
        Assert.Empty(await store.CountSummariesByFolderAsync(Guid.NewGuid()));       // different account → empty map
    }

    [Fact]
    public async Task CountMessagesByFolder_ReturnsTotalAndUnread_PerFolder()
    {
        // One grouped scan answers what the POP3 folder tree and Inbox status both ask for. Reading
        // it off LoadFolderReadStatesAsync instead means building a dictionary of every id in each
        // folder just to count it — a cost that grows with a store nothing prunes.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        MailMessageSummary Msg(string id, string folder, bool read) => new()
        {
            MessageId = id, AccountId = acct, FolderName = folder, IsRead = read,
            From = "a@b.com", Subject = "s", Date = DateTimeOffset.UtcNow,
        };
        await store.UpsertSummariesAsync([
            Msg("1", "Inbox", read: false), Msg("2", "Inbox", read: true), Msg("3", "Inbox", read: false),
            Msg("4", "Trash", read: true),
        ]);

        var counts = await store.CountMessagesByFolderAsync(acct);
        Assert.Equal((3, 2), counts["Inbox"]);
        Assert.Equal((1, 0), counts["Trash"]);
        Assert.False(counts.ContainsKey("Sent"));                                    // empty folder absent from the map
        Assert.Empty(await store.CountMessagesByFolderAsync(Guid.NewGuid()));         // different account → empty map
    }

    [Fact]
    public async Task LoadFolderSummariesSince_FiltersInSql_OnTheSameBoundaryTheCallerMeans()
    {
        // date_ticks is written as Date.UtcTicks, so the bound value has to be UtcTicks: a since
        // expressed in a local offset, compared as raw Ticks, silently shifts the window by the
        // offset — which for the POP3 sweep means either missing arrivals or re-announcing old mail.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct   = Guid.NewGuid();
        var cutoff = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        MailMessageSummary Msg(string id, DateTimeOffset date) => new()
        {
            MessageId = id, AccountId = acct, FolderName = "Inbox", Date = date,
            From = "a@b.com", Subject = "s",
        };
        await store.UpsertSummariesAsync([
            Msg("older",  cutoff.AddMinutes(-1)),
            Msg("onTheBoundary", cutoff),
            Msg("newer",  cutoff.AddMinutes(1)),
        ]);

        var window = await store.LoadFolderSummariesSinceAsync(acct, "Inbox", cutoff);
        Assert.Equal(["newer", "onTheBoundary"], window.Select(m => m.MessageId));    // newest first, boundary included

        // The same instant written with a non-zero offset selects the same rows.
        var shifted = await store.LoadFolderSummariesSinceAsync(acct, "Inbox", cutoff.ToOffset(TimeSpan.FromHours(-5)));
        Assert.Equal(window.Select(m => m.MessageId), shifted.Select(m => m.MessageId));

        // Full loads are unaffected.
        Assert.Equal(3, (await store.LoadFolderSummariesAsync(acct, "Inbox")).Count);
    }

    [Fact]
    public async Task LoadFolderMessageStates_ReturnsIdDateAndReadState_ForTheWholeFolder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.Zero);
        await store.UpsertSummariesAsync([
            new() { MessageId = "a", AccountId = acct, FolderName = "Inbox", Date = when, IsRead = true,  From = "x", Subject = "s" },
            new() { MessageId = "b", AccountId = acct, FolderName = "Inbox", Date = when, IsRead = false, From = "x", Subject = "s" },
            new() { MessageId = "c", AccountId = acct, FolderName = "Trash", Date = when, IsRead = false, From = "x", Subject = "s" },
        ]);

        var states = await store.LoadFolderMessageStatesAsync(acct, "Inbox");
        Assert.Equal(2, states.Count);
        Assert.True(states.Single(s => s.Id == "a").IsRead);
        Assert.False(states.Single(s => s.Id == "b").IsRead);
        Assert.Equal(when, states.Single(s => s.Id == "a").Date);
        Assert.Empty(await store.LoadFolderMessageStatesAsync(acct, "Sent"));
    }

    [Fact]
    public async Task HasAttachments_PersistsAndLoads()
    {
        // Regression for §1.10: ReadSummariesAsync used to omit the has_attachments
        // column, so the attachment indicator was blank on cold start until each
        // message was opened individually.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var accountId = Guid.NewGuid();
        var summary = new MailMessageSummary
        {
            MessageId  = "7",
            AccountId  = accountId,
            FolderName = "Inbox",
            From       = "x@example.com",
            Subject    = "with attachment",
            Date       = DateTimeOffset.UtcNow,
        };
        await store.UpsertSummariesAsync([summary]);

        // UpsertDetailAsync flips the has_attachments flag when attachments are present.
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId   = "7",
            AccountId   = accountId,
            FolderName  = "Inbox",
            Attachments = new() { new AttachmentModel { FileName = "doc.pdf", ContentType = "application/pdf" } },
        });

        var loaded = await store.LoadAllSummariesAsync();
        Assert.Single(loaded);
        Assert.True(loaded[0].HasAttachments);

        var loaded2 = await store.LoadFolderSummariesAsync(accountId, "Inbox");
        Assert.True(loaded2[0].HasAttachments);

        var loaded3 = await store.LoadAllSummariesAsync(accountId);
        Assert.True(loaded3[0].HasAttachments);
    }

    [Fact]
    public async Task DeleteSummariesAsync_RemovesAllRequestedIds()
    {
        // Covers §2.11: the chunked IN-list delete must remove every requested UID,
        // including across chunk boundaries (chunkSize = 500 internally).
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var accountId = Guid.NewGuid();
        var ids = Enumerable.Range(1, 1100).ToList(); // crosses two chunks
        var summaries = ids.Select(id => new MailMessageSummary
        {
            MessageId  = id.ToString(),
            AccountId  = accountId,
            FolderName = "Inbox",
            Subject    = $"msg{id}",
            Date       = DateTimeOffset.UtcNow,
        });
        await store.UpsertSummariesAsync(summaries);

        var toDelete = ids.Where(i => i % 2 == 0).Select(i => i.ToString()).ToList(); // 550 ids
        await store.DeleteSummariesAsync(accountId, "Inbox", toDelete);

        var remaining = await store.LoadFolderSummariesAsync(accountId, "Inbox");
        Assert.Equal(550, remaining.Count);
        Assert.All(remaining, m => Assert.Equal(1, int.Parse(m.MessageId) % 2));
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        // §2.5: data migrations are gated on PRAGMA user_version, so calling
        // Initialize() multiple times must be safe and have no further effect.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();
        store.Initialize();
        store.Initialize();
        // No exception, no schema breakage.
    }

    [Fact]
    public async Task DeleteSummariesAsync_EmptyInput_NoOp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        // Must not throw on empty input.
        await store.DeleteSummariesAsync(Guid.NewGuid(), "Inbox", Array.Empty<string>());
    }

    [Fact]
    public async Task HasAttachments_DefaultsFalse_WhenNotSet()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId  = "1",
            AccountId  = Guid.NewGuid(),
            FolderName = "Inbox",
            From       = "x@example.com",
            Subject    = "no attachment",
            Date       = DateTimeOffset.UtcNow,
        }]);

        var loaded = await store.LoadAllSummariesAsync();
        Assert.Single(loaded);
        Assert.False(loaded[0].HasAttachments);
    }
}