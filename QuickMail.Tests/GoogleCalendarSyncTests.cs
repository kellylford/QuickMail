using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Services.Graph;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Google calendar sync (read-down v1) through the shared calendar sync service: mapping (timed
/// RFC3339 offsets → UTC, all-day date-only, cancelled skipped, self response status), paging via
/// nextPageToken, replace-slice, and failure isolation. HTTP is stubbed with a queued
/// <see cref="HttpMessageHandler"/>; persistence uses a real temp-profile LocalStoreService,
/// mirroring GraphCalendarSyncServiceTests.
/// </summary>
public class GoogleCalendarSyncTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;
    private readonly Guid _accountId = Guid.NewGuid();

    public GoogleCalendarSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-gcalg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── Test plumbing ────────────────────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public int Calls { get; private set; }
        public List<string> Urls { get; } = [];
        public List<string> Methods { get; } = [];

        public RecordingHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Urls.Add(request.RequestUri!.ToString());
            Methods.Add(request.Method.Method);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : Json("""{ "items": [] }"""));
        }
    }

    private sealed class FixedAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public FixedAccountService(params AccountModel[] accounts) => _accounts = [.. accounts];
        public List<AccountModel> LoadAccounts() => _accounts;
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private AccountModel GoogleAccount() => new()
    {
        Id = _accountId,
        AuthType = AuthType.OAuth2Google,
        BackendKind = BackendKind.ImapSmtp, // Gmail mail is IMAP — the identity provider is what matters
        Username = "kelly@gmail.com",
        SyncCalendar = true, // calendar sync is opt-in per account (#282)
    };

    /// <summary>The shared sync service with only the Google client's HTTP stubbed (no Graph accounts).</summary>
    private GraphCalendarSyncService Service(RecordingHandler handler, params AccountModel[] accounts)
        => new(new FixedAccountService(accounts.Length > 0 ? accounts : [GoogleAccount()]),
               _store,
               new GraphClient(new StubOAuthService(), new HttpClient(new RecordingHandler()), defaultRetryDelay: TimeSpan.Zero),
               new GoogleCalendarClient(new StubGoogleOAuthService(), new HttpClient(handler)));

    private static string EventJson(string id, string summary,
        string? startDateTime = null, string? endDateTime = null,
        string? startDate = null, string? endDate = null,
        string status = "confirmed", string? attendeesJson = null,
        string? location = null, string? description = null)
        => $$"""
           {
             "id": "{{id}}",
             "status": "{{status}}",
             "summary": "{{summary}}",
             "description": "{{description ?? ""}}",
             "location": "{{location ?? ""}}",
             "organizer": { "email": "org@example.com", "displayName": "Org Anizer" },
             "start": { {{(startDate != null ? $"\"date\": \"{startDate}\"" : $"\"dateTime\": \"{startDateTime}\"")}} },
             "end": { {{(endDate != null ? $"\"date\": \"{endDate}\"" : $"\"dateTime\": \"{endDateTime}\"")}} }{{(attendeesJson is null ? "" : $", \"attendees\": {attendeesJson}")}}
           }
           """;

    private static string Page(string? nextPageToken, params string[] events)
        => $$"""{ "items": [{{string.Join(",", events)}}]{{(nextPageToken is null ? "" : $$""", "nextPageToken": "{{nextPageToken}}" """)}} }""";

    /// <summary>A <c>users/me/calendarList</c> response — the enumeration step multi-calendar sync issues first.</summary>
    private static string CalendarListPage(params (string Id, string Summary)[] cals)
    {
        var items = string.Join(",", cals.Select(c => $"{{ \"id\": \"{c.Id}\", \"summary\": \"{c.Summary}\", \"primary\": true }}"));
        return $"{{ \"items\": [{items}] }}";
    }

    /// <summary>Handler for a single-calendar account: the calendar list, then that calendar's event pages.</summary>
    private static RecordingHandler OneCalendarHandler(params HttpResponseMessage[] eventPages)
    {
        var responses = new List<HttpResponseMessage> { Json(CalendarListPage(("primary", "kelly@gmail.com"))) };
        responses.AddRange(eventPages);
        return new RecordingHandler(responses.ToArray());
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_MapsTimedEvent_OffsetStamps_ToUtcTicks()
    {
        // 09:00 at -07:00 == 16:00 UTC.
        var handler = OneCalendarHandler(Json(Page(null,
            EventJson("gid-1", "Standup",
                      startDateTime: "2026-08-01T09:00:00-07:00", endDateTime: "2026-08-01T09:30:00-07:00",
                      location: "Meet", description: "daily"))));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AccountsSynced);
        Assert.Equal(1, result.EventsFetched);
        Assert.Null(result.Error);

        var row = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.Equal("gid-1", row.Uid);
        Assert.Equal(_accountId, row.AccountId);
        Assert.True(row.IsGraph); // is_graph = "server-synced row", Google included
        Assert.Equal("primary", row.CalendarId);          // tagged with the calendar it came from
        Assert.Equal("kelly@gmail.com", row.CalendarName);
        Assert.False(row.IsUserCreated);
        Assert.Equal("Standup", row.Summary);
        Assert.Equal("Meet", row.Location);
        Assert.Equal("daily", row.Description);
        Assert.Equal("org@example.com", row.Organizer);
        Assert.Equal("Org Anizer", row.OrganizerName);
        Assert.Equal(new DateTime(2026, 8, 1, 16, 0, 0, DateTimeKind.Utc).Ticks, row.StartTimeTicks);
        Assert.Equal(new DateTime(2026, 8, 1, 16, 30, 0, DateTimeKind.Utc).Ticks, row.EndTimeTicks);
        Assert.False(row.IsAllDay);
        Assert.Equal(string.Empty, row.SourceMessageId);
        Assert.Null(row.RecurrenceRule);
    }

    [Fact]
    public async Task Sync_AllDayDateOnly_AnchorsToLocalDay_EndExclusive()
    {
        var handler = OneCalendarHandler(Json(Page(null,
            EventJson("gid-allday", "Offsite", startDate: "2026-08-03", endDate: "2026-08-05"))));

        await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.True(row.IsAllDay);
        // Exclusive end 08-05 → last day 08-04; stored like all other all-day rows.
        Assert.Equal(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Local), row.StartTime);
        Assert.Equal(new DateTime(2026, 8, 4, 23, 59, 59, DateTimeKind.Local), row.EndTime);
    }

    [Fact]
    public async Task Sync_CancelledEvents_AreSkipped()
    {
        var handler = OneCalendarHandler(Json(Page(null,
            EventJson("gid-live", "Live", startDateTime: "2026-08-01T09:00:00Z", endDateTime: "2026-08-01T10:00:00Z"),
            EventJson("gid-cancelled", "Gone", status: "cancelled",
                      startDateTime: "2026-08-02T09:00:00Z", endDateTime: "2026-08-02T10:00:00Z"))));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.EventsFetched);
        Assert.Equal("gid-live", Assert.Single(await _store.LoadCalendarEventsAsync()).Uid);
    }

    [Theory]
    [InlineData("""[{ "self": true, "responseStatus": "accepted" }]""",    CalendarResponseStatus.Accepted)]
    [InlineData("""[{ "self": true, "responseStatus": "declined" }]""",    CalendarResponseStatus.Declined)]
    [InlineData("""[{ "self": true, "responseStatus": "tentative" }]""",   CalendarResponseStatus.Tentative)]
    [InlineData("""[{ "self": true, "responseStatus": "needsAction" }]""", CalendarResponseStatus.Pending)]
    [InlineData("""[{ "self": false, "responseStatus": "declined" }]""",   CalendarResponseStatus.Accepted)]
    [InlineData(null,                                                      CalendarResponseStatus.Accepted)]
    public async Task Sync_MapsSelfAttendeeResponseStatus(string? attendeesJson, CalendarResponseStatus expected)
    {
        var handler = OneCalendarHandler(Json(Page(null,
            EventJson("gid-r", "R", startDateTime: "2026-08-01T09:00:00Z", endDateTime: "2026-08-01T10:00:00Z",
                      attendeesJson: attendeesJson))));

        await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(await _store.LoadCalendarEventsAsync()).ResponseStatus);
    }

    // ── Request shape and paging ─────────────────────────────────────────────────

    [Fact]
    public async Task Sync_EnumeratesCalendarList_ThenRequestsEachCalendar_SingleEvents_WithWindow()
    {
        var handler = OneCalendarHandler(Json(Page(null)));

        await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls); // calendar list, then that calendar's events
        Assert.Contains("/calendar/v3/users/me/calendarList", handler.Urls[0]);
        var url = handler.Urls[1];
        Assert.Contains("/calendar/v3/calendars/primary/events?", url);
        Assert.Contains("timeMin=", url);
        Assert.Contains("timeMax=", url);
        Assert.Contains("singleEvents=true", url);
    }

    [Fact]
    public async Task Sync_FollowsNextPageToken()
    {
        var handler = OneCalendarHandler(
            Json(Page("tok-2",
                EventJson("gid-a", "A", startDateTime: "2026-08-01T09:00:00Z", endDateTime: "2026-08-01T10:00:00Z"))),
            Json(Page(null,
                EventJson("gid-b", "B", startDateTime: "2026-08-02T09:00:00Z", endDateTime: "2026-08-02T10:00:00Z"))));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Calls); // calendar list, event page 1, event page 2
        Assert.Contains("pageToken=tok-2", handler.Urls[2]);
        Assert.Equal(2, result.EventsFetched);
        Assert.Equal(new[] { "gid-a", "gid-b" },
            (await _store.LoadCalendarEventsAsync()).Select(r => r.Uid).OrderBy(u => u).ToArray());
    }

    // ── Multiple calendars per account ───────────────────────────────────────────

    [Fact]
    public async Task Sync_EnumeratesEveryCalendar_TaggingRowsWithTheirCalendar()
    {
        var handler = new RecordingHandler(
            Json(CalendarListPage(("primary", "kelly@gmail.com"), ("fam123@group.calendar.google.com", "Family"))),
            Json(Page(null, EventJson("gid-me", "Mine", startDateTime: "2026-08-01T09:00:00Z", endDateTime: "2026-08-01T10:00:00Z"))),
            Json(Page(null, EventJson("gid-fam", "Soccer", startDateTime: "2026-08-02T09:00:00Z", endDateTime: "2026-08-02T10:00:00Z"))));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.EventsFetched);
        Assert.Equal(3, handler.Calls);
        Assert.Contains("/calendars/primary/events?", handler.Urls[1]);
        Assert.Contains("/calendars/fam123%40group.calendar.google.com/events?", handler.Urls[2]);

        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal("kelly@gmail.com", rows.Single(r => r.Uid == "gid-me").CalendarName);
        Assert.Equal("Family", rows.Single(r => r.Uid == "gid-fam").CalendarName);
        Assert.Equal("fam123@group.calendar.google.com", rows.Single(r => r.Uid == "gid-fam").CalendarId);
    }

    // ── Replace-slice and failure isolation ──────────────────────────────────────

    [Fact]
    public async Task SecondSync_RemovesEventsThatVanishedOnServer()
    {
        // Two syncs — each enumerates the calendar list, then pulls the one calendar's events.
        var handler = new RecordingHandler(
            Json(CalendarListPage(("primary", "kelly@gmail.com"))),
            Json(Page(null,
                EventJson("gid-keep", "Keep", startDateTime: "2026-08-01T09:00:00Z", endDateTime: "2026-08-01T10:00:00Z"),
                EventJson("gid-drop", "Drop", startDateTime: "2026-08-02T09:00:00Z", endDateTime: "2026-08-02T10:00:00Z"))),
            Json(CalendarListPage(("primary", "kelly@gmail.com"))),
            Json(Page(null,
                EventJson("gid-keep", "Keep", startDateTime: "2026-08-01T09:00:00Z", endDateTime: "2026-08-01T10:00:00Z"))));

        var service = Service(handler);
        await service.SyncAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, (await _store.LoadCalendarEventsAsync()).Count);

        await service.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal("gid-keep", Assert.Single(await _store.LoadCalendarEventsAsync()).Uid);
    }

    [Fact]
    public async Task Sync_HttpFailure_ReportsError_AndStoreUnchanged()
    {
        await _store.ReplaceGraphCalendarEventsAsync(_accountId, [new CalendarEvent
        {
            Uid = "gid-old", AccountId = _accountId, Summary = "Old slice",
        }]);
        var handler = new RecordingHandler(Json("""{ "error": "denied" }""", HttpStatusCode.Forbidden));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.AccountsSynced);
        Assert.NotNull(result.Error);
        // The previous slice survives — replace happens only after a full successful fetch.
        Assert.Equal("gid-old", Assert.Single(await _store.LoadCalendarEventsAsync()).Uid);
    }

    [Fact]
    public async Task Sync_NonGoogleNonGraphAccounts_AreSkipped()
    {
        var imapPassword = new AccountModel
        {
            Id = Guid.NewGuid(), AuthType = AuthType.Password, BackendKind = BackendKind.ImapSmtp,
        };
        var handler = new RecordingHandler();

        var result = await Service(handler, imapPassword).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.Calls);
        Assert.Equal(GraphCalendarSyncResult.None, result);
    }

    // ── Write-path calendar targeting (regression for #293) ──────────────────────
    // The Google write path was hardcoded to calendars/primary/events, so create/edit/delete of an
    // event living on a SECONDARY calendar hit the primary calendar and 404'd ("could not update the
    // online calendar"). Each write must target the event's own CalendarId (URL-escaped), with a
    // "primary" fallback when the event carries no calendar id.

    private CalendarEvent GoogleEvt(string uid, string calendarId) => new()
    {
        Uid            = uid,
        AccountId      = _accountId,
        CalendarId     = calendarId,
        CalendarName   = "Family",
        Summary        = "Lunch",
        StartTimeTicks = new DateTime(2026, 8, 1, 16, 0, 0, DateTimeKind.Utc).Ticks,
        EndTimeTicks   = new DateTime(2026, 8, 1, 17, 0, 0, DateTimeKind.Utc).Ticks,
        IsGraph        = true,
    };

    private static HttpResponseMessage GoogleEventResponse(string id) => Json(EventJson(
        id, "Lunch", startDateTime: "2026-08-01T16:00:00+00:00", endDateTime: "2026-08-01T17:00:00+00:00"));

    [Fact]
    public async Task DeleteEvent_OnSecondaryCalendar_TargetsThatCalendar_NotPrimary()
    {
        var handler = new RecordingHandler(); // default 200 OK is fine for DELETE
        var evt = GoogleEvt("gid-x", "fam123@group.calendar.google.com");

        await Service(handler).DeleteEventAsync(GoogleAccount(), evt, TestContext.Current.CancellationToken);

        Assert.Equal("DELETE", handler.Methods[0]);
        Assert.Contains("/calendars/fam123%40group.calendar.google.com/events/gid-x", handler.Urls[0]);
        Assert.DoesNotContain("/calendars/primary/", handler.Urls[0]);
    }

    [Fact]
    public async Task UpdateEvent_OnSecondaryCalendar_PatchesThatCalendar_AndKeepsTag()
    {
        var handler = new RecordingHandler(GoogleEventResponse("gid-x"));
        var evt = GoogleEvt("gid-x", "fam123@group.calendar.google.com");

        await Service(handler).UpdateEventAsync(GoogleAccount(), evt, TestContext.Current.CancellationToken);

        Assert.Equal("PATCH", handler.Methods[0]);
        Assert.Contains("/calendars/fam123%40group.calendar.google.com/events/gid-x", handler.Urls[0]);
        // The stored row stays tagged with the secondary calendar so a follow-up edit before the
        // next full sync still targets the right calendar.
        Assert.Equal("fam123@group.calendar.google.com",
            Assert.Single(await _store.LoadCalendarEventsAsync()).CalendarId);
    }

    [Fact]
    public async Task CreateEvent_OnSecondaryCalendar_PostsToThatCalendar()
    {
        var handler = new RecordingHandler(GoogleEventResponse("gid-new"));
        var evt = GoogleEvt("", "fam123@group.calendar.google.com");

        await Service(handler).CreateEventAsync(GoogleAccount(), evt, TestContext.Current.CancellationToken);

        Assert.Equal("POST", handler.Methods[0]);
        Assert.Contains("/calendars/fam123%40group.calendar.google.com/events", handler.Urls[0]);
        Assert.DoesNotContain("/calendars/primary/", handler.Urls[0]);
    }

    [Fact]
    public async Task CreateEvent_BlankCalendarId_FallsBackToPrimary()
    {
        var handler = new RecordingHandler(GoogleEventResponse("gid-new"));
        var evt = GoogleEvt("", ""); // no calendar specified → "primary" fallback

        await Service(handler).CreateEventAsync(GoogleAccount(), evt, TestContext.Current.CancellationToken);

        Assert.Equal("POST", handler.Methods[0]);
        Assert.Contains("/calendars/primary/events", handler.Urls[0]);
    }
}
