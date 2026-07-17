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
/// Generic CalDAV calendar sync (read-down v1, iCloud-first) through the shared calendar sync
/// service and the raw <see cref="CalDavCalendarClient"/>: discovery (PROPFIND principal → home
/// → first VEVENT calendar, with a cross-host redirect that must keep Basic auth), REPORT
/// calendar-query parsing (timed + all-day + recurring w/ EXDATE from one multistatus),
/// replace-slice under the synthetic account id, and failure isolation. HTTP is stubbed with a
/// queued <see cref="HttpMessageHandler"/>; persistence uses a real temp-profile
/// LocalStoreService, mirroring GoogleCalendarSyncTests.
/// </summary>
public class CalDavCalendarSyncTests : IDisposable
{
    private const string ServerUrl = "https://caldav.icloud.com";
    private const string Username  = "kelly@example.com";

    private readonly string _tempDir;
    private readonly LocalStoreService _store;
    private readonly Guid _accountId = CalDavCalendarClient.AccountIdFor(ServerUrl, Username);

    public CalDavCalendarSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-caldav-test-{Guid.NewGuid():N}");
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

    private sealed record RecordedRequest(string Method, string Url, string? Depth, string? Auth, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<RecordedRequest> Requests { get; } = [];

        public RecordingHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Depth", out var d) ? d.First() : null,
                request.Headers.Authorization?.ToString(),
                request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
            return _responses.Count > 0 ? _responses.Dequeue() : Xml(EmptyMultistatus);
        }
    }

    private sealed class SecretCredentialService : ICredentialService
    {
        private readonly Dictionary<string, string> _secrets = [];
        public SecretCredentialService(string? key = null, string? value = null)
        {
            if (key != null && value != null) _secrets[key] = value;
        }
        public void SavePassword(Guid accountId, string password) { }
        public string? GetPassword(Guid accountId) => null;
        public void DeletePassword(Guid accountId) { }
        public void SaveSecret(string key, string value) => _secrets[key] = value;
        public string? GetSecret(string key) => _secrets.TryGetValue(key, out var v) ? v : null;
        public void DeleteSecret(string key) => _secrets.Remove(key);
    }

    private sealed class CalDavConfigService : IConfigService
    {
        private ConfigModel _config;
        public CalDavConfigService(bool configured = true)
            => _config = configured
                ? new ConfigModel { CalDavUrl = ServerUrl, CalDavUsername = Username, CalDavDisplayName = "iCloud" }
                : new ConfigModel();
        public ConfigModel Load() => _config;
        public void Save(ConfigModel config) => _config = config;
    }

    private static HttpResponseMessage Xml(string body, HttpStatusCode code = (HttpStatusCode)207)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private GraphCalendarSyncService Service(RecordingHandler handler,
                                             IConfigService? config = null,
                                             ICredentialService? credentials = null)
        => new(new StubAccountService(), // no mail accounts — only the CalDAV source syncs
               _store,
               new GraphClient(new StubOAuthService(), new HttpClient(new RecordingHandler()), defaultRetryDelay: TimeSpan.Zero),
               google: null,
               calDav: new CalDavCalendarClient(new HttpClient(handler)),
               config: config ?? new CalDavConfigService(),
               credentials: credentials ?? new SecretCredentialService(CalDavCalendarClient.SecretKeyFor(Username), "app-pass"));

    // ── XML fixtures ─────────────────────────────────────────────────────────────

    private const string EmptyMultistatus =
        """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"/>""";

    private const string PrincipalXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/</d:href>
            <d:propstat>
              <d:prop>
                <d:current-user-principal><d:href>/123456/principal/</d:href></d:current-user-principal>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    // iCloud reports the calendar home on the user's partition host — an ABSOLUTE href.
    private const string HomeSetXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/123456/principal/</d:href>
            <d:propstat>
              <d:prop>
                <c:calendar-home-set><d:href>https://p42-caldav.icloud.com/123456/calendars/</d:href></c:calendar-home-set>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    // Home children: the home itself (plain collection), a VTODO-only Reminders list (must be
    // skipped), then the first VEVENT-capable calendar.
    private const string CollectionsXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/123456/calendars/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
            <d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/123456/calendars/tasks/</d:href>
            <d:propstat><d:prop>
              <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
              <d:displayname>Reminders</d:displayname>
              <c:supported-calendar-component-set><c:comp name="VTODO"/></c:supported-calendar-component-set>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/123456/calendars/home/</d:href>
            <d:propstat><d:prop>
              <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
              <d:displayname>Home</d:displayname>
              <c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    /// <summary>Wraps ICS bodies (already XML-safe in these fixtures) in a REPORT multistatus.</summary>
    private static string ReportXml(params string[] icsBodies)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.Append("""<d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">""");
        var i = 0;
        foreach (var ics in icsBodies)
        {
            sb.Append($"<d:response><d:href>/123456/calendars/home/{++i}.ics</d:href>")
              .Append("<d:propstat><d:prop><d:getetag>\"etag\"</d:getetag><c:calendar-data>")
              .Append(ics)
              .Append("</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
        }
        sb.Append("</d:multistatus>");
        return sb.ToString();
    }

    private static string TimedIcs => string.Join("\n",
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "BEGIN:VEVENT",
        "UID:timed@icloud",
        "SUMMARY:Dentist",
        "LOCATION:Downtown",
        "DESCRIPTION:Checkup",
        "DTSTART:20260801T160000Z",
        "DTEND:20260801T170000Z",
        "END:VEVENT",
        "END:VCALENDAR");

    private static string AllDayIcs => string.Join("\n",
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "BEGIN:VEVENT",
        "UID:allday@icloud",
        "SUMMARY:Offsite",
        "DTSTART;VALUE=DATE:20260803",
        "DTEND;VALUE=DATE:20260805",
        "END:VEVENT",
        "END:VCALENDAR");

    // Recurring master + an overridden instance (RECURRENCE-ID — skipped in v1) in ONE body,
    // exactly how CalDAV servers deliver a modified series.
    private static string RecurringIcs => string.Join("\n",
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "BEGIN:VEVENT",
        "UID:series@icloud",
        "SUMMARY:Weekly sync",
        "DTSTART:20260804T090000",
        "DTEND:20260804T093000",
        "RRULE:FREQ=WEEKLY;BYDAY=TU",
        "EXDATE:20260811T090000",
        "END:VEVENT",
        "BEGIN:VEVENT",
        "UID:series@icloud",
        "SUMMARY:Weekly sync (moved)",
        "RECURRENCE-ID:20260818T090000",
        "DTSTART:20260818T100000",
        "END:VEVENT",
        "END:VCALENDAR");

    private static string CancelledIcs => string.Join("\n",
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "BEGIN:VEVENT",
        "UID:gone@icloud",
        "SUMMARY:Cancelled thing",
        "DTSTART:20260805T090000Z",
        "STATUS:CANCELLED",
        "END:VEVENT",
        "END:VCALENDAR");

    private RecordingHandler FullSyncHandler(params string[] icsBodies)
        => new(Xml(PrincipalXml), Xml(HomeSetXml), Xml(CollectionsXml), Xml(ReportXml(icsBodies)));

    // ── Full pipeline: discovery + REPORT + mapping ──────────────────────────────

    [Fact]
    public async Task Sync_FullPipeline_MapsTimedAllDayAndRecurring()
    {
        var handler = FullSyncHandler(TimedIcs, AllDayIcs, RecurringIcs, CancelledIcs);

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(1, result.AccountsSynced);
        Assert.Equal(3, result.EventsFetched); // cancelled skipped; override folded into master

        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(_accountId, r.AccountId); // the synthetic, deterministic source id
            Assert.True(r.IsGraph);                // "server-synced row" → read-only in UI
            Assert.False(r.IsUserCreated);
            Assert.Equal(CalendarResponseStatus.Accepted, r.ResponseStatus);
            Assert.Equal(string.Empty, r.SourceMessageId);
        });

        var timed = rows.Single(r => r.Uid == "timed@icloud");
        Assert.Equal("Dentist", timed.Summary);
        Assert.Equal("Downtown", timed.Location);
        Assert.Equal("Checkup", timed.Description);
        Assert.Equal(new DateTime(2026, 8, 1, 16, 0, 0, DateTimeKind.Utc).Ticks, timed.StartTimeTicks);
        Assert.Equal(new DateTime(2026, 8, 1, 17, 0, 0, DateTimeKind.Utc).Ticks, timed.EndTimeTicks);
        Assert.False(timed.IsAllDay);
        Assert.Null(timed.RecurrenceRule);

        var allDay = rows.Single(r => r.Uid == "allday@icloud");
        Assert.True(allDay.IsAllDay);
        // Exclusive DTEND 08-05 → last day 08-04, re-anchored like every other all-day row.
        Assert.Equal(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Local), allDay.StartTime);
        Assert.Equal(new DateTime(2026, 8, 4, 23, 59, 59, DateTimeKind.Local), allDay.EndTime);

        // Unlike Graph/Google there is no server-side expansion: the master keeps its RRULE so
        // the local RecurrenceExpander shows occurrences, and the EXDATE hides the deleted one.
        var series = rows.Single(r => r.Uid == "series@icloud");
        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", series.RecurrenceRule);
        Assert.True(series.IsRecurring);
        Assert.Equal(new DateTime(2026, 8, 11, 9, 0, 0), Assert.Single(series.GetExDates()));
        Assert.Equal("Weekly sync", series.Summary); // the master, not the skipped override
    }

    [Fact]
    public async Task Sync_Discovery_WalksPrincipalHomeAndCollections_WithAuthAndDepth()
    {
        var handler = FullSyncHandler(TimedIcs);

        await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, handler.Requests.Count);
        var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:app-pass"));
        Assert.All(handler.Requests, r => Assert.Equal(expectedAuth, r.Auth));

        Assert.Equal(("PROPFIND", "https://caldav.icloud.com/", "0"),
                     (handler.Requests[0].Method, handler.Requests[0].Url, handler.Requests[0].Depth));
        Assert.Contains("current-user-principal", handler.Requests[0].Body);

        Assert.Equal(("PROPFIND", "https://caldav.icloud.com/123456/principal/", "0"),
                     (handler.Requests[1].Method, handler.Requests[1].Url, handler.Requests[1].Depth));
        Assert.Contains("calendar-home-set", handler.Requests[1].Body);

        // The home-set href was ABSOLUTE on the partition host — must be honored.
        Assert.Equal(("PROPFIND", "https://p42-caldav.icloud.com/123456/calendars/", "1"),
                     (handler.Requests[2].Method, handler.Requests[2].Url, handler.Requests[2].Depth));

        var report = handler.Requests[3];
        Assert.Equal("REPORT", report.Method);
        Assert.Equal("https://p42-caldav.icloud.com/123456/calendars/home/", report.Url);
        Assert.Equal("1", report.Depth);
        Assert.Contains("calendar-query", report.Body);
        Assert.Contains("""comp-filter name="VEVENT""", report.Body);
        Assert.Contains("time-range start=", report.Body);
    }

    [Fact]
    public async Task Sync_SecondPass_ReusesDiscoveredCalendar_AndReplacesSlice()
    {
        var handler = FullSyncHandler(TimedIcs, AllDayIcs);
        var service = Service(handler);

        await service.SyncAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, (await _store.LoadCalendarEventsAsync()).Count);

        // Second pass: the all-day event vanished on the server.
        handler.Enqueue(Xml(ReportXml(TimedIcs)));
        await service.SyncAllAsync(TestContext.Current.CancellationToken);

        // 4 discovery+report calls, then ONE more (REPORT only — discovery cached).
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("REPORT", handler.Requests[4].Method);
        Assert.Equal("timed@icloud", Assert.Single(await _store.LoadCalendarEventsAsync()).Uid);
    }

    [Fact]
    public async Task Sync_LeavesLocalAndInviteRows_Untouched()
    {
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "local-appt", AccountId = CalendarEvent.LocalAccountId, Summary = "Mine",
        });

        var handler = FullSyncHandler(TimedIcs);
        await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        var rows = await _store.LoadCalendarEventsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Uid == "local-appt" && r.IsUserCreated);
    }

    // ── Failure isolation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_HttpFailure_ReportsError_AndPriorSliceSurvives()
    {
        await _store.ReplaceGraphCalendarEventsAsync(_accountId, [new CalendarEvent
        {
            Uid = "old-slice", AccountId = _accountId, Summary = "Old slice",
        }]);
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad app password") });

        var result = await Service(handler).SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.AccountsSynced);
        Assert.NotNull(result.Error);
        Assert.Contains("401", result.Error);
        // Replace happens only after a full successful fetch — the previous slice survives.
        Assert.Equal("old-slice", Assert.Single(await _store.LoadCalendarEventsAsync()).Uid);
    }

    [Fact]
    public async Task Sync_MissingSavedPassword_ReportsError_WithoutAnyRequest()
    {
        var handler = FullSyncHandler(TimedIcs);

        var result = await Service(handler, credentials: new SecretCredentialService())
            .SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result.Error);
        Assert.Contains("Settings", result.Error);
        Assert.Empty(handler.Requests);
        Assert.Empty(await _store.LoadCalendarEventsAsync());
    }

    [Fact]
    public async Task Sync_NoCalDavConfigured_DoesNothing()
    {
        var handler = FullSyncHandler(TimedIcs);

        var result = await Service(handler, config: new CalDavConfigService(configured: false))
            .SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GraphCalendarSyncResult.None, result);
        Assert.Empty(handler.Requests);
    }

    // ── Redirect handling (iCloud partition hosts) ───────────────────────────────

    [Fact]
    public async Task Discovery_FollowsCrossHostRedirect_ReattachingBasicAuth()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
        redirect.Headers.Location = new Uri("https://p42-caldav.icloud.com/");
        var handler = new RecordingHandler(redirect, Xml(PrincipalXml), Xml(HomeSetXml), Xml(CollectionsXml));
        using var client = new CalDavCalendarClient(new HttpClient(handler));

        var info = await client.DiscoverCalendarAsync(ServerUrl, Username, "app-pass",
                                                      TestContext.Current.CancellationToken);

        // 1 redirected principal hop + re-issue, then home-set, then collections.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("https://p42-caldav.icloud.com/", handler.Requests[1].Url);
        // .NET strips Authorization on cross-host auto-redirects; the manual re-issue must not.
        Assert.All(handler.Requests, r => Assert.StartsWith("Basic ", r.Auth));
        // Relative hrefs resolve against the host that ANSWERED (the redirect target).
        Assert.Equal("https://p42-caldav.icloud.com/123456/calendars/home/", info.Url);
        Assert.Equal("Home", info.DisplayName);
    }

    // ── Client parsing units ─────────────────────────────────────────────────────

    [Fact]
    public void ParseInnerHref_FindsNestedHref_RegardlessOfPrefix()
    {
        Assert.Equal("/123456/principal/",
            CalDavCalendarClient.ParseInnerHref(PrincipalXml, "current-user-principal"));
        Assert.Equal("https://p42-caldav.icloud.com/123456/calendars/",
            CalDavCalendarClient.ParseInnerHref(HomeSetXml, "calendar-home-set"));
        Assert.Null(CalDavCalendarClient.ParseInnerHref(EmptyMultistatus, "current-user-principal"));
    }

    [Fact]
    public void ParseFirstVEventCalendar_SkipsPlainCollectionsAndVTodoLists()
    {
        var (href, name) = CalDavCalendarClient.ParseFirstVEventCalendar(CollectionsXml)!.Value;
        Assert.Equal("/123456/calendars/home/", href);
        Assert.Equal("Home", name);
    }

    [Fact]
    public void ParseFirstVEventCalendar_MissingComponentSet_CountsAsVEventCapable()
    {
        const string xml =
            """
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/cal/</d:href>
                <d:propstat><d:prop>
                  <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                </d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        var (href, name) = CalDavCalendarClient.ParseFirstVEventCalendar(xml)!.Value;
        Assert.Equal("/cal/", href);
        Assert.Equal("Calendar", name); // no displayname → fallback
    }

    [Fact]
    public void ParseFirstVEventCalendar_NoCalendars_ReturnsNull()
    {
        Assert.Null(CalDavCalendarClient.ParseFirstVEventCalendar(EmptyMultistatus));
    }

    [Fact]
    public void ParseCalendarData_ExtractsEveryIcsBody()
    {
        var bodies = CalDavCalendarClient.ParseCalendarData(ReportXml(TimedIcs, AllDayIcs));
        Assert.Equal(2, bodies.Count);
        Assert.Contains("UID:timed@icloud", bodies[0]);
        Assert.Contains("UID:allday@icloud", bodies[1]);
    }

    // ── Synthetic account id ─────────────────────────────────────────────────────

    [Fact]
    public void AccountIdFor_IsDeterministic_AndNormalized()
    {
        var a = CalDavCalendarClient.AccountIdFor("https://caldav.icloud.com", "kelly@example.com");
        Assert.Equal(a, CalDavCalendarClient.AccountIdFor("https://caldav.icloud.com/", "Kelly@Example.com "));
        Assert.NotEqual(Guid.Empty, a);
        Assert.NotEqual(a, CalDavCalendarClient.AccountIdFor("https://caldav.icloud.com", "other@example.com"));
        Assert.NotEqual(a, CalDavCalendarClient.AccountIdFor("https://caldav.fastmail.com", "kelly@example.com"));
    }

    // ── Mapping units ────────────────────────────────────────────────────────────

    [Fact]
    public void MapCalDavEvents_DuplicateUids_CollapseToOneRow()
    {
        var rows = GraphCalendarSyncService.MapCalDavEvents([TimedIcs, TimedIcs], _accountId);
        Assert.Single(rows);
    }

    [Fact]
    public void MapCalDavEvents_MissingUid_GetsStableSyntheticUid()
    {
        var ics = string.Join("\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "SUMMARY:No uid",
            "DTSTART:20260801T090000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var first  = Assert.Single(GraphCalendarSyncService.MapCalDavEvents([ics], _accountId));
        var second = Assert.Single(GraphCalendarSyncService.MapCalDavEvents([ics], _accountId));

        Assert.StartsWith("caldav-", first.Uid);
        Assert.Equal(first.Uid, second.Uid); // stable across passes → replace-slice keeps identity
    }
}
