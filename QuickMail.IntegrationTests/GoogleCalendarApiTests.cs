using System.Net.Http;
using QuickMail.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Google Calendar REST client against an in-process WireMock fake (live-content testing plan
/// Tier 2 / §5). No external server: WireMock listens on an ephemeral localhost port and the
/// client's base URL points at it. The headline coverage is <b>issue #293's literal bug
/// shape</b>: writes hardcoded to <c>calendars/primary</c> — asserted here by inspecting the
/// exact request lines the client emits when targeting a secondary calendar.
/// </summary>
public sealed class GoogleCalendarApiTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly GoogleCalendarClient _client;

    public GoogleCalendarApiTests()
    {
        _server = WireMockServer.Start();
        _client = new GoogleCalendarClient(new FakeGoogleOAuthService(), baseUrl: _server.Urls[0]);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    private static GoogleEventWriteBody Body(string summary) => new() { Summary = summary };

    [Fact]
    public async Task CreateEvent_TargetsTheGivenCalendar_NotPrimary()
    {
        _server.Given(Request.Create().WithPath("/calendars/team-cal/events").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new { id = "created-1", summary = "Standup" }));

        var created = await _client.CreateEventAsync(
            "user@gmail.test", Body("Standup"), calendarId: "team-cal",
            TestContext.Current.CancellationToken);

        Assert.Equal("created-1", created.Id);
        var request = Assert.Single(_server.LogEntries).RequestMessage!;
        // The #293 regression: this path was hardcoded to calendars/primary regardless of the
        // calendar the event belonged to, 404ing every secondary-calendar write.
        Assert.Equal("/calendars/team-cal/events", request.Path);
        Assert.DoesNotContain("primary", request.Path);
        Assert.Equal("Bearer fake-token", Assert.Contains("Authorization", request.Headers!)[0]);
    }

    [Fact]
    public async Task UpdateEvent_TargetsTheGivenCalendarAndEventId()
    {
        _server.Given(Request.Create().WithPath("/calendars/team-cal/events/evt-42").UsingPatch())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new { id = "evt-42", summary = "Renamed" }));

        var updated = await _client.UpdateEventAsync(
            "user@gmail.test", "evt-42", Body("Renamed"), calendarId: "team-cal",
            TestContext.Current.CancellationToken);

        Assert.Equal("evt-42", updated.Id);
        var request = Assert.Single(_server.LogEntries).RequestMessage!;
        Assert.Equal("/calendars/team-cal/events/evt-42", request.Path);
        Assert.Equal("PATCH", request.Method);
    }

    [Fact]
    public async Task DeleteEvent_TargetsTheGivenCalendarAndEventId()
    {
        _server.Given(Request.Create().WithPath("/calendars/team-cal/events/evt-42").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(204));

        await _client.DeleteEventAsync(
            "user@gmail.test", "evt-42", calendarId: "team-cal",
            TestContext.Current.CancellationToken);

        var request = Assert.Single(_server.LogEntries).RequestMessage!;
        Assert.Equal("/calendars/team-cal/events/evt-42", request.Path);
        Assert.Equal("DELETE", request.Method);
    }

    [Fact]
    public async Task GetCalendarList_FollowsNextPageToken_UntilExhausted()
    {
        _server.Given(Request.Create().WithPath("/users/me/calendarList").UsingGet()
                   .WithParam("pageToken", "page2"))
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new { items = new[] { new { id = "cal-b", summary = "Work" } } }));
        _server.Given(Request.Create().WithPath("/users/me/calendarList").UsingGet())
               .AtPriority(10) // fallback: first page (no pageToken param)
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new
                   {
                       items = new[] { new { id = "cal-a", summary = "Home", primary = true } },
                       nextPageToken = "page2",
                   }));

        var calendars = await _client.GetCalendarListAsync(
            "user@gmail.test", TestContext.Current.CancellationToken);

        Assert.Equal(2, calendars.Count);
        Assert.Contains(calendars, c => c.Id == "cal-a" && c.Primary);
        Assert.Contains(calendars, c => c.Id == "cal-b");
        Assert.Equal(2, _server.LogEntries.Count); // exactly two pages fetched
    }

    [Fact]
    public async Task WriteFailure_SurfacesStatusAndServerBody()
    {
        _server.Given(Request.Create().WithPath("/calendars/birthdays/events").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(403)
                   .WithBodyAsJson(new { error = new { code = 403, message = "Cannot add events to the birthdays calendar" } }));

        // The #294 family: provider-specific write rejections must surface as a diagnosable
        // error (status + server body), never vanish into a silent no-op.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _client.CreateEventAsync(
            "user@gmail.test", Body("Party"), calendarId: "birthdays",
            TestContext.Current.CancellationToken));

        Assert.Contains("403", ex.Message);
        Assert.Contains("birthdays calendar", ex.Message);
    }
}

/// <summary>Returns a fixed token; these tests assert it arrives as the Bearer header.
/// Interactive members throw — a WireMock test must never depend on interactive sign-in.</summary>
internal sealed class FakeGoogleOAuthService : IGoogleOAuthService
{
    public Task<string> GetAccessTokenAsync(string username, CancellationToken ct = default) =>
        Task.FromResult("fake-token");
    public Task<OAuthResult> SignInInteractiveAsync(string loginHint, CancellationToken ct = default) =>
        throw new NotSupportedException("Interactive sign-in is not available in tests.");
    public Task<OAuthResult> AuthorizeContactsAsync(string loginHint, CancellationToken ct = default) =>
        throw new NotSupportedException("Interactive sign-in is not available in tests.");
    public Task SignOutAsync(string username) =>
        throw new NotSupportedException("Sign-out is not available in tests.");
}
