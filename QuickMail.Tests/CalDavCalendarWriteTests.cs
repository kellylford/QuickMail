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
/// iCloud CalDAV WRITE (create / edit / delete of single events): the raw
/// <see cref="CalDavCalendarClient"/> PUT/DELETE wire format (resource URL, If-None-Match,
/// text/calendar body, cross-host redirect re-attaching Basic auth) and the
/// <see cref="GraphCalendarSyncService"/> iCloud write branch (ICS PUT, store upsert, missing
/// password). HTTP is stubbed with a queued handler; persistence uses a real temp-profile store,
/// mirroring <see cref="CalDavCalendarSyncTests"/>.
/// </summary>
public class CalDavCalendarWriteTests : IDisposable
{
    private const string Collection = "https://p42-caldav.icloud.com/123456/calendars/home";
    private const string Username   = "kelly@example.com";

    private readonly string _tempDir;
    private readonly LocalStoreService _store;
    private readonly Guid _accountId = Guid.NewGuid();

    public CalDavCalendarWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-caldav-write-{Guid.NewGuid():N}");
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

    private sealed record RecordedRequest(string Method, string Url, string? Auth, string? IfNoneMatch,
                                          string? ContentType, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<RecordedRequest> Requests { get; } = [];

        public RecordingHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("If-None-Match", out var m) ? m.First() : null,
                request.Content?.Headers.ContentType?.ToString(),
                request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
            return _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static HttpResponseMessage Created()   => new(HttpStatusCode.Created);
    private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    private sealed class StubCredentials : ICredentialService
    {
        private readonly Dictionary<Guid, string> _passwords = [];
        public StubCredentials(Guid? accountId = null, string? password = null)
        {
            if (accountId is { } id && password != null) _passwords[id] = password;
        }
        public void SavePassword(Guid accountId, string password) => _passwords[accountId] = password;
        public string? GetPassword(Guid accountId) => _passwords.TryGetValue(accountId, out var v) ? v : null;
        public void DeletePassword(Guid accountId) => _passwords.Remove(accountId);
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    private sealed class FixedAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public FixedAccountService(params AccountModel[] accounts) => _accounts = [.. accounts];
        public List<AccountModel> LoadAccounts() => _accounts;
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private AccountModel ICloudAccount() => new()
    {
        Id = _accountId,
        AuthType = AuthType.Password,
        BackendKind = BackendKind.ImapSmtp,
        ImapHost = "imap.mail.me.com",
        Username = Username,
        SyncCalendar = true,
    };

    private GraphCalendarSyncService Service(RecordingHandler handler, ICredentialService? credentials = null)
        => new(new FixedAccountService(ICloudAccount()),
               _store,
               new GraphClient(new StubOAuthService(), new HttpClient(new RecordingHandler()), defaultRetryDelay: TimeSpan.Zero),
               google: null,
               calDav: new CalDavCalendarClient(new HttpClient(handler)),
               credentials: credentials ?? new StubCredentials(_accountId, "app-pass"));

    private CalendarEvent TimedEvent(string uid = "lunch-1", string calendarId = Collection) => new()
    {
        Uid = uid,
        CalendarId = calendarId,
        Summary = "Lunch",
        Location = "Cafe",
        StartTimeTicks = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc).Ticks,
        EndTimeTicks   = new DateTime(2026, 8, 1, 13, 0, 0, DateTimeKind.Utc).Ticks,
        ResponseStatus = CalendarResponseStatus.Accepted,
    };

    // ── Raw client: PUT / DELETE wire format ─────────────────────────────────────

    [Fact]
    public async Task PutEvent_Create_SendsIfNoneMatch_TextCalendar_ToResourceUrl()
    {
        var handler = new RecordingHandler(Created());
        using var client = new CalDavCalendarClient(new HttpClient(handler));
        var ics = IcsModel.ExportEvent(TimedEvent());

        var url = await client.PutEventAsync($"{Collection}/lunch-1.ics", ics, Username, "app-pass",
                                             ifNoneMatch: true, TestContext.Current.CancellationToken);

        Assert.Equal($"{Collection}/lunch-1.ics", url);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("PUT", req.Method);
        Assert.Equal($"{Collection}/lunch-1.ics", req.Url);
        Assert.Equal("*", req.IfNoneMatch);
        Assert.StartsWith("text/calendar", req.ContentType);
        Assert.StartsWith("Basic ", req.Auth);
        Assert.Contains("UID:lunch-1", req.Body);
        Assert.Contains("SUMMARY:Lunch", req.Body);
    }

    [Fact]
    public async Task PutEvent_Update_OmitsIfNoneMatch()
    {
        var handler = new RecordingHandler(NoContent());
        using var client = new CalDavCalendarClient(new HttpClient(handler));
        var ics = IcsModel.ExportEvent(TimedEvent());

        await client.PutEventAsync($"{Collection}/lunch-1.ics", ics, Username, "app-pass",
                                   ifNoneMatch: false, TestContext.Current.CancellationToken);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("PUT", req.Method);
        Assert.Null(req.IfNoneMatch);
    }

    [Fact]
    public async Task DeleteEvent_SendsDelete_NoBody()
    {
        var handler = new RecordingHandler(NoContent());
        using var client = new CalDavCalendarClient(new HttpClient(handler));

        await client.DeleteEventAsync($"{Collection}/lunch-1.ics", Username, "app-pass",
                                      TestContext.Current.CancellationToken);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", req.Method);
        Assert.Equal($"{Collection}/lunch-1.ics", req.Url);
        Assert.Equal(string.Empty, req.Body);
        Assert.StartsWith("Basic ", req.Auth);
    }

    [Fact]
    public async Task Put_FollowsCrossHostRedirect_ReattachingBasicAuth()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
        redirect.Headers.Location = new Uri("https://p99-caldav.icloud.com/123456/calendars/home/lunch-1.ics");
        var handler = new RecordingHandler(redirect, Created());
        using var client = new CalDavCalendarClient(new HttpClient(handler));
        var ics = IcsModel.ExportEvent(TimedEvent());

        await client.PutEventAsync($"{Collection}/lunch-1.ics", ics, Username, "app-pass",
                                   ifNoneMatch: true, TestContext.Current.CancellationToken);

        // Redirected hop + re-issue; the re-issue keeps method PUT, the body, and Basic auth.
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.StartsWith("Basic ", r.Auth));
        Assert.All(handler.Requests, r => Assert.Equal("PUT", r.Method));
        Assert.Equal("https://p99-caldav.icloud.com/123456/calendars/home/lunch-1.ics", handler.Requests[1].Url);
        Assert.Contains("UID:lunch-1", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Put_NonSuccess_ThrowsWithBody()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.PreconditionFailed) { Content = new StringContent("resource exists") });
        using var client = new CalDavCalendarClient(new HttpClient(handler));
        var ics = IcsModel.ExportEvent(TimedEvent());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PutEventAsync($"{Collection}/lunch-1.ics", ics, Username, "app-pass", ifNoneMatch: true,
                                 TestContext.Current.CancellationToken));
        Assert.Contains("412", ex.Message);
        Assert.Contains("resource exists", ex.Message);
    }

    // ── Sync service iCloud write branch ─────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_ICloud_PutsIcsAndUpsertsServerRow()
    {
        var handler = new RecordingHandler(Created());
        var created = await Service(handler).CreateEventAsync(ICloudAccount(), TimedEvent(),
                                                              TestContext.Current.CancellationToken);

        Assert.True(created.IsGraph);              // server-synced row
        Assert.Equal(_accountId, created.AccountId);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("PUT", req.Method);
        Assert.Equal($"{Collection}/lunch-1.ics", req.Url);
        Assert.Equal("*", req.IfNoneMatch);

        var row = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.Equal("lunch-1", row.Uid);
        Assert.True(row.IsGraph);
        Assert.Equal(Collection, row.CalendarId);
        // The created row records the resource URL it PUT to, so an immediate edit (before the next
        // read-sync captures the server's real href) still targets the right resource.
        Assert.Equal($"{Collection}/lunch-1.ics", row.ResourceUrl);
    }

    [Fact]
    public async Task UpdateEvent_ICloud_TargetsStoredResourceUrl_WhenItDiffersFromUidName()
    {
        // Apple stored this event at a random filename ≠ UID; the stored ResourceUrl differs from
        // {collection}/{uid}.ics. The edit must PUT to the stored href, not the reconstructed one.
        const string realHref = "https://p42-caldav.icloud.com/123456/calendars/home/AB12-random.ics";
        var handler = new RecordingHandler(NoContent());
        var evt = TimedEvent();
        evt.ResourceUrl = realHref;
        evt.Summary = "Lunch (moved)";

        await Service(handler).UpdateEventAsync(ICloudAccount(), evt, TestContext.Current.CancellationToken);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("PUT", req.Method);
        Assert.Equal(realHref, req.Url);                        // stored href, not {collection}/lunch-1.ics
        Assert.NotEqual($"{Collection}/lunch-1.ics", req.Url);
        Assert.Equal(realHref, Assert.Single(await _store.LoadCalendarEventsAsync()).ResourceUrl);
    }

    [Fact]
    public async Task DeleteEvent_ICloud_TargetsStoredResourceUrl_WhenItDiffersFromUidName()
    {
        const string realHref = "https://p42-caldav.icloud.com/123456/calendars/home/AB12-random.ics";
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "lunch-1", AccountId = _accountId, IsGraph = true, CalendarId = Collection,
            Summary = "Lunch", ResourceUrl = realHref,
        });
        var handler = new RecordingHandler(NoContent());
        var evt = TimedEvent();
        evt.ResourceUrl = realHref;

        await Service(handler).DeleteEventAsync(ICloudAccount(), evt, TestContext.Current.CancellationToken);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", req.Method);
        Assert.Equal(realHref, req.Url);                        // stored href, not {collection}/lunch-1.ics
        Assert.NotEqual($"{Collection}/lunch-1.ics", req.Url);
        Assert.Empty(await _store.LoadCalendarEventsAsync());
    }

    [Fact]
    public async Task UpdateEvent_ICloud_PutsWithoutIfNoneMatch()
    {
        var handler = new RecordingHandler(NoContent());
        var evt = TimedEvent();
        evt.Summary = "Lunch (moved)";

        var updated = await Service(handler).UpdateEventAsync(ICloudAccount(), evt,
                                                             TestContext.Current.CancellationToken);

        Assert.True(updated.IsGraph);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("PUT", req.Method);
        Assert.Null(req.IfNoneMatch);
        Assert.Contains("SUMMARY:Lunch (moved)", req.Body);
        Assert.Equal("Lunch (moved)", Assert.Single(await _store.LoadCalendarEventsAsync()).Summary);
    }

    [Fact]
    public async Task DeleteEvent_ICloud_DeletesResourceAndRemovesRow()
    {
        // Seed a stored row for the event being deleted.
        await _store.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "lunch-1", AccountId = _accountId, IsGraph = true, CalendarId = Collection, Summary = "Lunch",
        });
        var handler = new RecordingHandler(NoContent());

        await Service(handler).DeleteEventAsync(ICloudAccount(), TimedEvent(),
                                               TestContext.Current.CancellationToken);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", req.Method);
        Assert.Equal($"{Collection}/lunch-1.ics", req.Url);
        Assert.Empty(await _store.LoadCalendarEventsAsync());
    }

    [Fact]
    public async Task CreateEvent_ICloud_MissingPassword_Throws_WithoutRequest()
    {
        var handler = new RecordingHandler(Created());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(handler, credentials: new StubCredentials()).CreateEventAsync(
                ICloudAccount(), TimedEvent(), TestContext.Current.CancellationToken));

        Assert.Contains("Manage Accounts", ex.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(await _store.LoadCalendarEventsAsync());
    }

    [Fact]
    public async Task CreateEvent_ICloud_Recurring_Rejected_BeforeAnyRequest()
    {
        var handler = new RecordingHandler(Created());
        var evt = TimedEvent();
        evt.RecurrenceRule = "FREQ=WEEKLY";

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Service(handler).CreateEventAsync(ICloudAccount(), evt, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }
}
