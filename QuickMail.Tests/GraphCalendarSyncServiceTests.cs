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
/// Graph calendar sync (read-down v1): mapping, paging, 429 retry, replace-slice semantics, and
/// the is_graph store guards. HTTP is stubbed with a queued <see cref="HttpMessageHandler"/>
/// (the GraphClientTests style); persistence uses a real temp-profile LocalStoreService so the
/// replace-slice and guard SQL are exercised for real.
/// </summary>
public class GraphCalendarSyncServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;
    private readonly Guid _accountId = Guid.NewGuid();

    public GraphCalendarSyncServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-gcal-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Test plumbing ────────────────────────────────────────────────────────────

    /// <summary>Serves queued responses; records each request's URL and Prefer header.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public int Calls { get; private set; }
        public List<string> Urls { get; } = [];
        public List<string?> PreferHeaders { get; } = [];

        public RecordingHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        public List<string> Methods { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Urls.Add(request.RequestUri!.ToString());
            Methods.Add(request.Method.Method);
            PreferHeaders.Add(request.Headers.TryGetValues("Prefer", out var v) ? string.Join(",", v) : null);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : Json("""{ "value": [] }""");
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

    /// <summary>Silent token acquisition fails — the calendar grant has lapsed.</summary>
    private sealed class SignInRequiredOAuthService : IOAuthService
    {
        public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
            => throw new InteractiveSignInRequiredException("consent required");
        public Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;
        public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(new OAuthResult(string.Empty, string.Empty));
        public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(new OAuthResult(string.Empty, string.Empty));
        public Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequestCalendarConsentAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;
        public Task SignOutAsync(AccountModel account) => Task.CompletedTask;
    }

    private static HttpResponseMessage Json(string body, TimeSpan? retryAfter = null, HttpStatusCode code = HttpStatusCode.OK)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter.HasValue)
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
        return r;
    }

    private AccountModel GraphAccount() => new()
    {
        Id = _accountId,
        BackendKind = BackendKind.MicrosoftGraph,
        Username = "user@example.com",
        SyncCalendar = true, // calendar sync is opt-in per account (#282)
    };

    private GraphCalendarSyncService Service(RecordingHandler handler, params AccountModel[] accounts)
        => new(new FixedAccountService(accounts.Length > 0 ? accounts : [GraphAccount()]),
               _store,
               new GraphClient(new StubOAuthService(), new HttpClient(handler), defaultRetryDelay: TimeSpan.Zero));

    private static string EventJson(string id, string subject, string startUtc, string endUtc,
        bool allDay = false, bool isOrganizer = false, string response = "accepted",
        string? location = null, string? preview = null)
        => $$"""
           {
             "id": "{{id}}",
             "subject": "{{subject}}",
             "bodyPreview": "{{preview ?? ""}}",
             "location": { "displayName": "{{location ?? ""}}" },
             "organizer": { "emailAddress": { "name": "Alex Organizer", "address": "alex@example.com" } },
             "start": { "dateTime": "{{startUtc}}", "timeZone": "UTC" },
             "end": { "dateTime": "{{endUtc}}", "timeZone": "UTC" },
             "isAllDay": {{(allDay ? "true" : "false")}},
             "isOrganizer": {{(isOrganizer ? "true" : "false")}},
             "responseStatus": { "response": "{{response}}" }
           }
           """;

    private static string Page(string? nextLink, params string[] events)
        => $$"""{ "value": [{{string.Join(",", events)}}]{{(nextLink is null ? "" : $$""", "@odata.nextLink": "{{nextLink}}" """)}} }""";

    /// <summary>A <c>/me/calendars</c> list response — the enumeration step multi-calendar sync issues first.</summary>
    private static string CalendarsPage(params (string Id, string Name)[] cals)
    {
        var items = string.Join(",", cals.Select(c => $"{{ \"id\": \"{c.Id}\", \"name\": \"{c.Name}\" }}"));
        return $"{{ \"value\": [{items}] }}";
    }

    /// <summary>Handler for a single-calendar account: the calendars list, then that calendar's view pages.</summary>
    private static RecordingHandler OneCalendarHandler(params HttpResponseMessage[] viewPages)
    {
        var responses = new List<HttpResponseMessage> { Json(CalendarsPage(("cal-1", "Calendar"))) };
        responses.AddRange(viewPages);
        return new RecordingHandler(responses.ToArray());
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_MapsTimedEvent_StoringUtcTicksAndGraphFlag()
    {
        var handler = OneCalendarHandler(Json(Page(null,
            EventJson("gid-1", "Team sync", "2026-08-01T14:00:00.0000000", "2026-08-01T15:00:00.0000000",
                      location: "Room 1", preview: "agenda"))));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AccountsSynced);
        Assert.Equal(1, result.EventsFetched);
        Assert.Null(result.Error);

        var row = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.Equal("gid-1", row.Uid);
        Assert.Equal(_accountId, row.AccountId);
        Assert.True(row.IsGraph);
        Assert.Equal("cal-1", row.CalendarId);     // tagged with the calendar it came from
        Assert.Equal("Calendar", row.CalendarName);
        Assert.False(row.IsUserCreated); // real account id → read-only in the UI
        Assert.Equal("Team sync", row.Summary);
        Assert.Equal("Room 1", row.Location);
        Assert.Equal("agenda", row.Description);
        Assert.Equal("alex@example.com", row.Organizer);
        Assert.Equal("Alex Organizer", row.OrganizerName);
        Assert.Equal(new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc).Ticks, row.StartTimeTicks);
        Assert.Equal(new DateTime(2026, 8, 1, 15, 0, 0, DateTimeKind.Utc).Ticks, row.EndTimeTicks);
        Assert.False(row.IsAllDay);
        Assert.Equal(CalendarResponseStatus.Accepted, row.ResponseStatus);
        // Not an invite email, and calendarView already expanded any series server-side.
        Assert.Equal(string.Empty, row.SourceMessageId);
        Assert.Equal(string.Empty, row.SourceFolder);
        Assert.Null(row.RecurrenceRule);
        Assert.False(row.IsRecurring);
    }

    [Fact]
    public async Task Sync_AllDayEvent_AnchorsToLocalDay()
    {
        // Graph all-day: midnight-to-midnight with an EXCLUSIVE end, in the preferred (UTC) zone.
        var handler = OneCalendarHandler(Json(Page(null,
            EventJson("gid-allday", "Holiday", "2026-08-03T00:00:00.0000000", "2026-08-04T00:00:00.0000000", allDay: true))));

        await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.True(row.IsAllDay);
        // Stored like locally-authored all-day rows: local midnight .. 23:59:59 of the same day,
        // so the event shows on August 3 in the user's zone regardless of UTC offset.
        Assert.Equal(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Local), row.StartTime);
        Assert.Equal(new DateTime(2026, 8, 3, 23, 59, 59, DateTimeKind.Local), row.EndTime);
    }

    [Theory]
    [InlineData("accepted",            false, CalendarResponseStatus.Accepted)]
    [InlineData("declined",            false, CalendarResponseStatus.Declined)]
    [InlineData("tentativelyAccepted", false, CalendarResponseStatus.Tentative)]
    [InlineData("organizer",           true,  CalendarResponseStatus.Accepted)]
    [InlineData("none",                true,  CalendarResponseStatus.Accepted)]
    [InlineData("none",                false, CalendarResponseStatus.Pending)]
    [InlineData("notResponded",        false, CalendarResponseStatus.Pending)]
    [InlineData(null,                  false, CalendarResponseStatus.Pending)]
    public void MapResponseStatus_CoversGraphValues(string? response, bool isOrganizer, CalendarResponseStatus expected)
        => Assert.Equal(expected, GraphCalendarSyncService.MapResponseStatus(response, isOrganizer));

    // ── Request shape ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_EnumeratesCalendars_ThenSendsPreferUtcHeader_AndCalendarViewWindow()
    {
        var handler = OneCalendarHandler(Json(Page(null)));

        await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls); // calendars list, then that calendar's view
        // First: the calendar enumeration.
        Assert.Contains("/me/calendars?", handler.Urls[0]);
        // Second: the per-calendar calendarView, with the machine's own Windows zone Prefer header so
        // all-day events come back as local midnights (a UTC header shifted them a day east of Greenwich).
        Assert.Equal($"outlook.timezone=\"{TimeZoneInfo.Local.Id}\"", handler.PreferHeaders[1]);
        var url = handler.Urls[1];
        Assert.Contains("/me/calendars/cal-1/calendarView?", url);
        Assert.Contains("startDateTime=", url);
        Assert.Contains("endDateTime=", url);
        Assert.Contains("$select=", url);
    }

    // ── Multiple calendars per account ───────────────────────────────────────────

    [Fact]
    public async Task Sync_EnumeratesEveryCalendar_TaggingRowsWithTheirCalendar()
    {
        // Two calendars; each returns one event. Sync issues the list call, then one view per calendar.
        var handler = new RecordingHandler(
            Json(CalendarsPage(("cal-home", "Home"), ("cal-work", "Work"))),
            Json(Page(null, EventJson("gid-home", "Dinner", "2026-08-01T18:00:00.0000000", "2026-08-01T19:00:00.0000000"))),
            Json(Page(null, EventJson("gid-work", "Standup", "2026-08-02T09:00:00.0000000", "2026-08-02T09:30:00.0000000"))));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.EventsFetched);
        Assert.Equal(3, handler.Calls);
        Assert.Contains("/me/calendars/cal-home/calendarView?", handler.Urls[1]);
        Assert.Contains("/me/calendars/cal-work/calendarView?", handler.Urls[2]);

        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal("Home", rows.Single(r => r.Uid == "gid-home").CalendarName);
        Assert.Equal("cal-home", rows.Single(r => r.Uid == "gid-home").CalendarId);
        Assert.Equal("Work", rows.Single(r => r.Uid == "gid-work").CalendarName);
        Assert.Equal("cal-work", rows.Single(r => r.Uid == "gid-work").CalendarId);
    }

    // ── Paging ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_FollowsNextLink_AcrossPages()
    {
        var handler = OneCalendarHandler(
            Json(Page("https://graph.microsoft.com/v1.0/me/calendarView?page=2",
                EventJson("gid-a", "A", "2026-08-01T09:00:00.0000000", "2026-08-01T10:00:00.0000000"),
                EventJson("gid-b", "B", "2026-08-02T09:00:00.0000000", "2026-08-02T10:00:00.0000000"))),
            Json(Page(null,
                EventJson("gid-c", "C", "2026-08-03T09:00:00.0000000", "2026-08-03T10:00:00.0000000"))));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Calls); // calendars list, view page 1, view page 2
        Assert.Contains("page=2", handler.Urls[2]);
        Assert.Equal(3, result.EventsFetched);
        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal(new[] { "gid-a", "gid-b", "gid-c" }, rows.Select(r => r.Uid).OrderBy(u => u).ToArray());
    }

    // ── 429 retry ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_RetriesOn429_HonoringRetryAfter()
    {
        var handler = OneCalendarHandler(
            Json("{}", retryAfter: TimeSpan.Zero, code: (HttpStatusCode)429),
            Json(Page(null, EventJson("gid-429", "Throttled", "2026-08-01T09:00:00.0000000", "2026-08-01T10:00:00.0000000"))));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Calls); // calendars list, one 429 on the view, one successful retry
        Assert.Null(result.Error);
        Assert.Equal("gid-429", Assert.Single(await _store.LoadCalendarEventsAsync()).Uid);
    }

    // ── Replace-slice ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecondSync_RemovesEventsThatVanishedOnServer()
    {
        // Two syncs — each enumerates calendars, then pulls the one calendar's view.
        var handler = new RecordingHandler(
            Json(CalendarsPage(("cal-1", "Calendar"))),
            Json(Page(null,
                EventJson("gid-keep", "Keep", "2026-08-01T09:00:00.0000000", "2026-08-01T10:00:00.0000000"),
                EventJson("gid-drop", "Drop", "2026-08-02T09:00:00.0000000", "2026-08-02T10:00:00.0000000"))),
            Json(CalendarsPage(("cal-1", "Calendar"))),
            Json(Page(null,
                EventJson("gid-keep", "Keep", "2026-08-01T09:00:00.0000000", "2026-08-01T10:00:00.0000000"))));

        var service = Service(handler);
        await service.SyncAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, (await _store.LoadCalendarEventsAsync()).Count);

        await service.SyncAllAsync(TestContext.Current.CancellationToken);

        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal("gid-keep", Assert.Single(rows).Uid);
    }

    [Fact]
    public async Task ReplaceSlice_LeavesLocalAndInviteRowsAlone()
    {
        // A locally-authored appointment and a harvested invite for the SAME account.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "local-1", AccountId = CalendarEvent.LocalAccountId, Summary = "My appointment",
        });
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "invite-1", AccountId = _accountId, Summary = "Invite",
            SourceMessageId = "msg-1", SourceFolder = "INBOX",
        });

        var handler = OneCalendarHandler(Json(Page(null,
            EventJson("gid-1", "Graph event", "2026-08-01T09:00:00.0000000", "2026-08-01T10:00:00.0000000"))));
        await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Uid == "local-1" && !r.IsGraph);
        Assert.Contains(rows, r => r.Uid == "invite-1" && !r.IsGraph && r.SourceMessageId == "msg-1");
        Assert.Contains(rows, r => r.Uid == "gid-1" && r.IsGraph);
    }

    // ── Store guards for is_graph rows ───────────────────────────────────────────

    [Fact]
    public async Task HarvestUpsert_DoesNotOverwriteGraphRow_OnUidCollision()
    {
        await _store.ReplaceGraphCalendarEventsAsync(_accountId, [new CalendarEvent
        {
            Uid = "shared-uid", AccountId = _accountId, Summary = "Graph copy",
            ResponseStatus = CalendarResponseStatus.Accepted,
        }]);

        // The harvest path re-upserts by (uid, account) — it must not clobber the Graph row.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "shared-uid", AccountId = _accountId, Summary = "Harvested copy",
            SourceMessageId = "msg-x", SourceFolder = "INBOX",
        });

        var row = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.True(row.IsGraph);
        Assert.Equal("Graph copy", row.Summary);
        Assert.Equal(string.Empty, row.SourceMessageId);
    }

    [Fact]
    public async Task OrphanCleanup_SkipsGraphRows()
    {
        // A Graph row that (hypothetically) carries a source link must not be touched by the
        // invite harvest's orphan cleanup — Graph rows are owned by the sync's replace-slice.
        await _store.ReplaceGraphCalendarEventsAsync(_accountId, [new CalendarEvent
        {
            Uid = "gid-src", AccountId = _accountId, Summary = "Graph",
            SourceMessageId = "not-a-real-message", SourceFolder = "INBOX",
        }]);
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "invite-orphan", AccountId = _accountId, Summary = "Invite",
            SourceMessageId = "also-gone", SourceFolder = "INBOX",
        });

        await _store.ClearOrphanedCalendarSourceLinksAsync();

        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal("not-a-real-message", rows.Single(r => r.Uid == "gid-src").SourceMessageId);
        Assert.Equal(string.Empty, rows.Single(r => r.Uid == "invite-orphan").SourceMessageId);
    }

    [Fact]
    public async Task DeleteAccountData_CascadesGraphRows()
    {
        await _store.ReplaceGraphCalendarEventsAsync(_accountId, [new CalendarEvent
        {
            Uid = "gid-cascade", AccountId = _accountId, Summary = "Graph",
        }]);

        await _store.DeleteAccountDataAsync(_accountId);

        Assert.Empty(await _store.LoadCalendarEventsAsync());
    }

    // ── Eligibility and failure handling ─────────────────────────────────────────

    [Fact]
    public async Task Sync_SkipsNonGraphAccounts()
    {
        var imapAccount = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp };
        var handler = new RecordingHandler();

        var result = await Service(handler, imapAccount).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.Calls);
        Assert.Equal(GraphCalendarSyncResult.None, result);
    }

    [Fact]
    public async Task Sync_SkipsAccountsWithoutCalendarOptIn()
    {
        // A Microsoft account that has NOT opted into calendar sync (#282) is skipped — no request.
        var notOptedIn = new AccountModel
        {
            Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph,
            Username = "user@example.com", SyncCalendar = false,
        };
        var handler = new RecordingHandler();

        var result = await Service(handler, notOptedIn).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.Calls);
        Assert.Equal(GraphCalendarSyncResult.None, result);
    }

    [Fact]
    public async Task Sync_SignInRequired_ReportsErrorWithoutThrowing()
    {
        var handler = new RecordingHandler();
        var graph = new GraphClient(new SignInRequiredOAuthService(), new HttpClient(handler), defaultRetryDelay: TimeSpan.Zero);
        var service = new GraphCalendarSyncService(new FixedAccountService(GraphAccount()), _store, graph);

        var result = await service.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.AccountsSynced);
        Assert.NotNull(result.Error);
        Assert.Contains("sign-in", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _store.LoadCalendarEventsAsync()); // nothing was deleted or inserted
    }

    // ── Create push (POST /me/events) ────────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_Timed_PostsExpectedShape_AndStoresServerCopy()
    {
        var handler = new RecordingHandler(Json(
            EventJson("srv-1", "Board meeting", "2026-08-01T14:00:00.0000000", "2026-08-01T15:00:00.0000000",
                      isOrganizer: true, response: "organizer", location: "Room 9", preview: "quarterly")));
        var service = Service(handler);
        var evt = new CalendarEvent
        {
            Uid = "local-tmp", AccountId = _accountId, Summary = "Board meeting",
            Description = "quarterly", Location = "Room 9",
            StartTimeTicks = new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc).Ticks,
            EndTimeTicks   = new DateTime(2026, 8, 1, 15, 0, 0, DateTimeKind.Utc).Ticks,
        };

        var created = await service.CreateEventAsync(GraphAccount(), evt, TestContext.Current.CancellationToken);

        Assert.Equal("POST", Assert.Single(handler.Methods));
        Assert.EndsWith("/me/events", handler.Urls[0]);
        // Prefer header carries the machine's Windows zone (see Sync test); the body below still
        // sends explicit UTC start/end, so the created event's times are unambiguous.
        Assert.Equal($"outlook.timezone=\"{TimeZoneInfo.Local.Id}\"", handler.PreferHeaders[0]);

        using var body = System.Text.Json.JsonDocument.Parse(handler.Bodies[0]!);
        var root = body.RootElement;
        Assert.Equal("Board meeting", root.GetProperty("subject").GetString());
        Assert.Equal("text", root.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("quarterly", root.GetProperty("body").GetProperty("content").GetString());
        Assert.Equal("2026-08-01T14:00:00", root.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("UTC", root.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Equal("2026-08-01T15:00:00", root.GetProperty("end").GetProperty("dateTime").GetString());
        Assert.Equal("Room 9", root.GetProperty("location").GetProperty("displayName").GetString());
        Assert.False(root.GetProperty("isAllDay").GetBoolean());

        // The server's copy is stored (Uid = the Graph id, is_graph set) so it shows immediately
        // and is simply re-fetched by the next replace-slice sync.
        Assert.Equal("srv-1", created.Uid);
        var row = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.Equal("srv-1", row.Uid);
        Assert.True(row.IsGraph);
        Assert.Equal(CalendarResponseStatus.Accepted, row.ResponseStatus);
    }

    [Fact]
    public async Task CreateEvent_AllDay_SendsMidnightBoundaries_EndDayAfterLastDay()
    {
        var handler = new RecordingHandler(Json(
            EventJson("srv-allday", "Offsite", "2026-08-03T00:00:00.0000000", "2026-08-05T00:00:00.0000000", allDay: true)));
        var service = Service(handler);
        // Local all-day storage: local midnight of first day .. 23:59:59 of the last day (Aug 3-4).
        var evt = new CalendarEvent
        {
            Uid = "local-tmp", AccountId = _accountId, Summary = "Offsite", IsAllDay = true,
            StartTimeTicks = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks,
            EndTimeTicks   = new DateTime(2026, 8, 4, 23, 59, 59, DateTimeKind.Local).ToUniversalTime().Ticks,
        };

        await service.CreateEventAsync(GraphAccount(), evt, TestContext.Current.CancellationToken);

        using var body = System.Text.Json.JsonDocument.Parse(handler.Bodies[0]!);
        var root = body.RootElement;
        Assert.True(root.GetProperty("isAllDay").GetBoolean());
        Assert.Equal("2026-08-03T00:00:00", root.GetProperty("start").GetProperty("dateTime").GetString());
        // Graph requires an EXCLUSIVE end: the day after the last day (Aug 4 → Aug 5).
        Assert.Equal("2026-08-05T00:00:00", root.GetProperty("end").GetProperty("dateTime").GetString());
    }

    [Fact]
    public async Task CreateEvent_Recurring_ThrowsNotSupported()
    {
        var handler = new RecordingHandler();
        var service = Service(handler);
        var evt = new CalendarEvent
        {
            Uid = "local-tmp", AccountId = _accountId, Summary = "Weekly",
            RecurrenceRule = "FREQ=WEEKLY",
            StartTimeTicks = DateTime.UtcNow.Ticks,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.CreateEventAsync(GraphAccount(), evt, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls); // rejected before any network traffic
    }

    [Fact]
    public async Task CreateEvent_HttpFailure_Throws_AndStoresNothing()
    {
        var handler = new RecordingHandler(Json("""{ "error": "denied" }""", code: HttpStatusCode.Forbidden));
        var service = Service(handler);
        var evt = new CalendarEvent
        {
            Uid = "local-tmp", AccountId = _accountId, Summary = "Doomed",
            StartTimeTicks = DateTime.UtcNow.Ticks,
        };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.CreateEventAsync(GraphAccount(), evt, TestContext.Current.CancellationToken));
        Assert.Empty(await _store.LoadCalendarEventsAsync());
    }

    [Fact]
    public async Task Sync_HttpFailure_ReportsErrorAndPreservesExistingRows()
    {
        // Seed a previous successful sync, then fail the next one — the old slice must survive
        // (the replace happens only after a full successful fetch).
        await _store.ReplaceGraphCalendarEventsAsync(_accountId, [new CalendarEvent
        {
            Uid = "gid-old", AccountId = _accountId, Summary = "Old slice",
        }]);
        var handler = new RecordingHandler(Json("""{ "error": "boom" }""", code: HttpStatusCode.InternalServerError));

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.AccountsSynced);
        Assert.NotNull(result.Error);
        Assert.Equal("gid-old", Assert.Single(await _store.LoadCalendarEventsAsync()).Uid);
    }
}
