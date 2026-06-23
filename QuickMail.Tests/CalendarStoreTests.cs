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