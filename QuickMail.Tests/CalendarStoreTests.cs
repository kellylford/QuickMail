using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Round-trip tests for the CalendarEvent SQLite table via LocalStoreService.
/// Each test uses a fresh temp-directory profile so migrations run from scratch.
/// </summary>
public class CalendarStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;

    public CalendarStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-cal-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var profile = new ProfileContext(_tempDir);
        _store = new LocalStoreService(profile);
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpsertAndLoad_RoundTrips()
    {
        var evt = new CalendarEvent
        {
            Uid = "evt-123@test",
            AccountId = Guid.NewGuid(),
            Summary = "Team standup",
            Description = "Weekly sync",
            Location = "Zoom",
            Organizer = "alex@example.com",
            OrganizerName = "Alex",
            StartTimeTicks = DateTime.UtcNow.Date.AddHours(10).Ticks,
            EndTimeTicks = DateTime.UtcNow.Date.AddHours(11).Ticks,
            Sequence = "0",
            Method = "REQUEST",
            SourceMessageId = "msg-1",
            SourceFolder = "INBOX",
            ResponseStatus = CalendarResponseStatus.Accepted,
            CalendarId = "cal-home",
            CalendarName = "Home",
        };

        await _store.UpsertCalendarEventAsync(evt);
        var loaded = await _store.LoadCalendarEventsAsync();

        Assert.Single(loaded);
        var r = loaded[0];
        Assert.Equal(evt.Uid, r.Uid);
        Assert.Equal(evt.AccountId, r.AccountId);
        Assert.Equal("Team standup", r.Summary);
        Assert.Equal("Zoom", r.Location);
        Assert.Equal("Alex", r.OrganizerName);
        Assert.Equal(evt.StartTimeTicks, r.StartTimeTicks);
        Assert.Equal(CalendarResponseStatus.Accepted, r.ResponseStatus);
        Assert.Equal("msg-1", r.SourceMessageId);
        Assert.Equal("cal-home", r.CalendarId);
        Assert.Equal("Home", r.CalendarName);
        Assert.False(r.IsAllDay);
    }

    [Fact]
    public async Task ResourceUrl_RoundTrips_ThroughUpsertAndReplaceSlice()
    {
        var accountId = Guid.NewGuid();
        const string href = "https://p42-caldav.icloud.com/123456/calendars/home/AB12-random.ics";

        // Upsert path (write-back / local create).
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "res-upsert", AccountId = accountId, IsGraph = true, Summary = "Upsert",
            CalendarId = "cal-home", ResourceUrl = href,
        });
        // Replace-slice path (read-sync).
        await _store.ReplaceGraphCalendarEventsAsync(accountId,
        [
            new CalendarEvent { Uid = "res-slice", AccountId = accountId, CalendarId = "cal-home", ResourceUrl = href },
        ]);

        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal(href, rows.Single(r => r.Uid == "res-slice").ResourceUrl);

        // A row with no href round-trips as empty (Graph/Google/local/invite rows).
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "res-none", AccountId = CalendarEvent.LocalAccountId, Summary = "No href",
        });
        Assert.Equal(string.Empty,
            (await _store.LoadCalendarEventsAsync()).Single(r => r.Uid == "res-none").ResourceUrl);
    }

    [Fact]
    public async Task ResourceUrl_Preserved_WhenUpsertWriteBackCarriesNoHref()
    {
        var accountId = Guid.NewGuid();
        const string href = "https://p42-caldav.icloud.com/123456/calendars/home/AB12-random.ics";

        // Read-sync captured the real server href.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "res-keep", AccountId = accountId, IsGraph = true, Summary = "Original",
            CalendarId = "cal-home", ResourceUrl = href,
        });

        // A later write-back for the same row that carries NO href (e.g. an untagged edit) must not
        // wipe the stored one — mirrors the calendar_id CASE-preserve idiom.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "res-keep", AccountId = accountId, IsGraph = true, Summary = "Edited",
            CalendarId = "cal-home", ResourceUrl = string.Empty,
        });

        var row = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.Equal("Edited", row.Summary);
        Assert.Equal(href, row.ResourceUrl); // preserved, not wiped
    }

    [Fact]
    public async Task LoadCalendarSources_ReturnsDistinctTaggedCalendars()
    {
        var acctA = Guid.NewGuid();
        var acctB = Guid.NewGuid();

        // Two calendars on account A (two events each) and one on account B.
        await _store.ReplaceGraphCalendarEventsAsync(acctA,
        [
            new CalendarEvent { Uid = "a-home-1", AccountId = acctA, CalendarId = "cal-home", CalendarName = "Home" },
            new CalendarEvent { Uid = "a-home-2", AccountId = acctA, CalendarId = "cal-home", CalendarName = "Home" },
            new CalendarEvent { Uid = "a-work-1", AccountId = acctA, CalendarId = "cal-work", CalendarName = "Work" },
        ]);
        await _store.ReplaceGraphCalendarEventsAsync(acctB,
        [
            new CalendarEvent { Uid = "b-fam-1", AccountId = acctB, CalendarId = "cal-fam", CalendarName = "Family" },
        ]);
        // A local (untagged) row must be excluded — it has no distinct server calendar.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "local-1", AccountId = CalendarEvent.LocalAccountId, Summary = "Mine",
        });

        var sources = await _store.LoadCalendarSourcesAsync();

        Assert.Equal(3, sources.Count); // Home, Work, Family — distinct, untagged local excluded
        Assert.Contains(sources, s => s.AccountId == acctA && s.CalendarId == "cal-home" && s.CalendarName == "Home");
        Assert.Contains(sources, s => s.AccountId == acctA && s.CalendarId == "cal-work" && s.CalendarName == "Work");
        Assert.Contains(sources, s => s.AccountId == acctB && s.CalendarId == "cal-fam"  && s.CalendarName == "Family");
    }

    [Fact]
    public async Task AllDayFlag_RoundTrips()
    {
        var evt = new CalendarEvent
        {
            Uid = "local-allday",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Holiday",
            StartTimeTicks = DateTime.UtcNow.Date.Ticks,
            EndTimeTicks = DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1).Ticks,
            IsAllDay = true,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };

        await _store.UpsertCalendarEventAsync(evt);
        var loaded = await _store.LoadCalendarEventsAsync();

        Assert.Single(loaded);
        Assert.True(loaded[0].IsAllDay);
        Assert.True(loaded[0].IsUserCreated);
    }

    [Fact]
    public async Task RecurrenceRule_RoundTrips()
    {
        var evt = new CalendarEvent
        {
            Uid = "local-weekly",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Standup",
            StartTimeTicks = DateTime.UtcNow.Date.AddHours(9).Ticks,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=TU",
            ResponseStatus = CalendarResponseStatus.Accepted,
        };

        await _store.UpsertCalendarEventAsync(evt);
        var loaded = (await _store.LoadCalendarEventsAsync()).Single();

        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", loaded.RecurrenceRule);
        Assert.True(loaded.IsRecurring);
    }

    [Fact]
    public async Task Upsert_OnConflict_UpdatesFields_ButPreservesResponseStatus()
    {
        var accountId = Guid.NewGuid();
        var evt = new CalendarEvent
        {
            Uid = "evt-conflict@test",
            AccountId = accountId,
            Summary = "Original",
            SourceMessageId = "msg-1",
            SourceFolder = "INBOX",
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        await _store.UpsertCalendarEventAsync(evt);

        // Harvest an updated ICS for the same UID — response status should be preserved.
        var updated = new CalendarEvent
        {
            Uid = "evt-conflict@test",
            AccountId = accountId,
            Summary = "Updated title",
            SourceMessageId = "msg-2",
            SourceFolder = "INBOX",
            ResponseStatus = CalendarResponseStatus.Pending, // harvest always sends Pending
        };
        await _store.UpsertCalendarEventAsync(updated);

        var loaded = await _store.LoadCalendarEventsAsync();
        Assert.Single(loaded);
        Assert.Equal("Updated title", loaded[0].Summary);
        Assert.Equal("msg-2", loaded[0].SourceMessageId);
        // Response status is NOT overwritten by the upsert (it's excluded from the ON CONFLICT update).
        Assert.Equal(CalendarResponseStatus.Accepted, loaded[0].ResponseStatus);
    }

    [Fact]
    public async Task UpdateResponseStatus_Persists()
    {
        var accountId = Guid.NewGuid();
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "evt-status@test",
            AccountId = accountId,
            Summary = "Meeting",
            ResponseStatus = CalendarResponseStatus.Pending,
        });

        await _store.UpdateCalendarResponseStatusAsync("evt-status@test", accountId, CalendarResponseStatus.Declined);

        var loaded = await _store.LoadCalendarEventsAsync();
        Assert.Single(loaded);
        Assert.Equal(CalendarResponseStatus.Declined, loaded[0].ResponseStatus);
    }

    [Fact]
    public async Task Delete_RemovesEvent()
    {
        var accountId = Guid.NewGuid();
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "evt-del@test",
            AccountId = accountId,
            Summary = "To delete",
        });

        await _store.DeleteCalendarEventAsync("evt-del@test", accountId);
        var loaded = await _store.LoadCalendarEventsAsync();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task LoadCalendarEvents_SortsByStartTimeAscending_NullsLast()
    {
        var accountId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "no-time", AccountId = accountId, Summary = "No time", StartTimeTicks = null,
        });
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "later", AccountId = accountId, Summary = "Later",
            StartTimeTicks = now.AddHours(2).Ticks,
        });
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "earlier", AccountId = accountId, Summary = "Earlier",
            StartTimeTicks = now.AddHours(1).Ticks,
        });

        var loaded = await _store.LoadCalendarEventsAsync();
        Assert.Equal(3, loaded.Count);
        Assert.Equal("earlier", loaded[0].Uid);
        Assert.Equal("later", loaded[1].Uid);
        Assert.Equal("no-time", loaded[2].Uid);
    }

    [Fact]
    public async Task DeleteSummaries_ClearsCalendarEventSourceLink()
    {
        var accountId = Guid.NewGuid();

        // Store a message detail with calendar ICS.
        var detail = new MailMessageDetail
        {
            MessageId = "msg-purge",
            AccountId = accountId,
            FolderName = "INBOX",
            From = "org@example.com",
            Subject = "Invite",
            Date = DateTimeOffset.UtcNow,
            CalendarIcs = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:purge-test\r\nSUMMARY:Purge meeting\r\nEND:VEVENT\r\nEND:VCALENDAR",
        };
        // UpsertDetailAsync requires a summary row first (LEFT JOIN is fine for loading,
        // but the summary row is needed so the delete has something to cascade from).
        await _store.UpsertSummariesAsync(new[] { new MailMessageSummary
        {
            MessageId = "msg-purge", AccountId = accountId, FolderName = "INBOX",
            From = "org@example.com", Subject = "Invite", Date = DateTimeOffset.UtcNow,
        }});
        await _store.UpsertDetailAsync(detail);

        // Harvest to create the CalendarEvent row.
        var provider = new LocalCacheCalendarProvider(_store);
        await provider.HarvestAsync();

        var before = await _store.LoadCalendarEventsAsync();
        Assert.Single(before);
        Assert.Equal("msg-purge", before[0].SourceMessageId);

        // Simulate the sync deleting the message from the local cache.
        await _store.DeleteSummariesAsync(accountId, "INBOX", new[] { "msg-purge" });

        // The CalendarEvent should still exist but with its source link cleared.
        var after = await _store.LoadCalendarEventsAsync();
        Assert.Single(after);
        Assert.Empty(after[0].SourceMessageId);
        Assert.Empty(after[0].SourceFolder);
    }

    [Fact]
    public async Task ClearOrphanedSourceLinks_ClearsLinkWhenDetailMissing()
    {
        var accountId = Guid.NewGuid();

        // Insert a CalendarEvent that references a message not in MessageDetail.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "orphan-uid",
            AccountId = accountId,
            Summary = "Orphaned event",
            SourceMessageId = "msg-orphan",
            SourceFolder = "INBOX",
        });

        var before = await _store.LoadCalendarEventsAsync();
        Assert.Equal("msg-orphan", before[0].SourceMessageId);

        // The cleanup should detect no matching MessageDetail and clear the link.
        await _store.ClearOrphanedCalendarSourceLinksAsync();

        var after = await _store.LoadCalendarEventsAsync();
        Assert.Single(after);
        Assert.Empty(after[0].SourceMessageId);
        Assert.Empty(after[0].SourceFolder);
    }

    [Fact]
    public async Task ClearOrphanedSourceLinks_KeepsLinkWhenDetailPresent()
    {
        var accountId = Guid.NewGuid();

        var detail = new MailMessageDetail
        {
            MessageId = "msg-present",
            AccountId = accountId,
            FolderName = "INBOX",
            From = "org@example.com",
            Subject = "Invite",
            Date = DateTimeOffset.UtcNow,
            CalendarIcs = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:present-uid\r\nSUMMARY:Present\r\nEND:VEVENT\r\nEND:VCALENDAR",
        };
        await _store.UpsertDetailAsync(detail);

        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "present-uid",
            AccountId = accountId,
            Summary = "Present event",
            SourceMessageId = "msg-present",
            SourceFolder = "INBOX",
        });

        await _store.ClearOrphanedCalendarSourceLinksAsync();

        var after = await _store.LoadCalendarEventsAsync();
        Assert.Single(after);
        // Source link must NOT be cleared — the detail row still exists.
        Assert.Equal("msg-present", after[0].SourceMessageId);
        Assert.Equal("INBOX", after[0].SourceFolder);
    }

    [Fact]
    public async Task HarvestAsync_ClearsOrphanedSourceLinks()
    {
        var accountId = Guid.NewGuid();

        // Insert a CalendarEvent whose source message was never stored in MessageDetail.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "harvest-orphan",
            AccountId = accountId,
            Summary = "Orphan",
            SourceMessageId = "msg-gone",
            SourceFolder = "INBOX",
        });

        // Run a harvest with no ICS rows in the cache.
        var provider = new LocalCacheCalendarProvider(_store);
        await provider.HarvestAsync();

        var after = await _store.LoadCalendarEventsAsync();
        Assert.Single(after);
        Assert.Empty(after[0].SourceMessageId);
    }

    [Fact]
    public async Task DeleteAccountData_RemovesCalendarEvents()
    {
        var accountId = Guid.NewGuid();
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "del-account-uid",
            AccountId = accountId,
            Summary = "Meeting",
        });

        await _store.DeleteAccountDataAsync(accountId);

        var after = await _store.LoadCalendarEventsAsync();
        Assert.Empty(after);
    }

    [Fact]
    public async Task Migration_v3ToV4_PreservesExistingTables()
    {
        // The store was initialized fresh (v4). Verify message tables still work.
        var summary = new MailMessageSummary
        {
            MessageId = "msg-x",
            AccountId = Guid.NewGuid(),
            FolderName = "INBOX",
            From = "sender@example.com",
            Subject = "Test",
            Date = DateTimeOffset.UtcNow,
        };
        await _store.UpsertSummariesAsync(new[] { summary });
        var all = await _store.LoadAllSummariesAsync();
        Assert.Contains(all, s => s.MessageId == "msg-x");

        // And the calendar table coexists.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "coexist@test", AccountId = Guid.NewGuid(), Summary = "Coexist",
        });
        var events = await _store.LoadCalendarEventsAsync();
        Assert.Single(events);
    }

    [Fact]
    public async Task LoadAllCalendarIcs_ReturnsOnlyRowsWithIcs()
    {
        var accountId = Guid.NewGuid();
        var detail = new MailMessageDetail
        {
            MessageId = "msg-ics",
            AccountId = accountId,
            FolderName = "INBOX",
            From = "org@example.com",
            Subject = "Invite",
            Date = DateTimeOffset.UtcNow,
            CalendarIcs = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:ics-test\r\nSUMMARY:Test\r\nEND:VEVENT\r\nEND:VCALENDAR",
        };
        await _store.UpsertDetailAsync(detail);

        // A detail without ICS
        await _store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "msg-no-ics", AccountId = accountId, FolderName = "INBOX",
            From = "x@x.com", Subject = "No ICS", Date = DateTimeOffset.UtcNow,
        });

        var rows = await _store.LoadAllCalendarIcsAsync();
        Assert.Single(rows);
        Assert.Equal("msg-ics", rows[0].MessageId);
        Assert.Contains("ics-test", rows[0].IcsText);
    }

    [Fact]
    public async Task Harvest_CancelMethod_SetsResponseStatusToCancelled()
    {
        var accountId = Guid.NewGuid();

        // First: store a REQUEST invite and accept it.
        var requestDetail = new MailMessageDetail
        {
            MessageId = "msg-request",
            AccountId = accountId,
            FolderName = "INBOX",
            From = "org@example.com",
            Subject = "Invite",
            Date = DateTimeOffset.UtcNow,
            CalendarIcs = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:cancel-test\r\nSUMMARY:Team standup\r\nDTSTART:20260701T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR",
        };
        await _store.UpsertDetailAsync(requestDetail);

        var provider = new LocalCacheCalendarProvider(_store);
        await provider.HarvestAsync();
        await _store.UpdateCalendarResponseStatusAsync("cancel-test", accountId, CalendarResponseStatus.Accepted);

        var events = await _store.LoadCalendarEventsAsync();
        Assert.Single(events);
        Assert.Equal(CalendarResponseStatus.Accepted, events[0].ResponseStatus);

        // Now: the organizer sends a CANCEL with the same UID.
        var cancelDetail = new MailMessageDetail
        {
            MessageId = "msg-cancel",
            AccountId = accountId,
            FolderName = "INBOX",
            From = "org@example.com",
            Subject = "Cancelled: Team standup",
            Date = DateTimeOffset.UtcNow,
            CalendarIcs = "BEGIN:VCALENDAR\r\nMETHOD:CANCEL\r\nBEGIN:VEVENT\r\nUID:cancel-test\r\nSUMMARY:Team standup\r\nDTSTART:20260701T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR",
        };
        await _store.UpsertDetailAsync(cancelDetail);

        // Re-harvest: the CANCEL should override the Accepted status.
        await provider.HarvestAsync();

        events = await _store.LoadCalendarEventsAsync();
        Assert.Single(events);
        Assert.Equal(CalendarResponseStatus.Cancelled, events[0].ResponseStatus);
    }
}