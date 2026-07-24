using System.Net.Http;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Services.Graph;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Microsoft Graph client against an in-process WireMock fake (live-content testing plan
/// Tier 2 / §5). Pins the documenting behaviors from issue #304 item 4: calendar writes
/// resolve by GLOBAL event id via <c>/me/events/{id}</c> (no per-calendar path needed on
/// Graph, unlike Google), plus paging, 429 retry, and error-shape propagation.
/// </summary>
public sealed class GraphApiTests : IDisposable
{
    private sealed record TestItem(string Id, string Subject);

    private readonly WireMockServer _server;
    private readonly GraphClient _client;
    private readonly AccountModel _account = new()
    {
        AccountName = "graph-test",
        Username = "user@outlook.test",
        AuthType = AuthType.OAuth2Microsoft,
        BackendKind = BackendKind.MicrosoftGraph,
    };

    public GraphApiTests()
    {
        _server = WireMockServer.Start();
        _client = new GraphClient(new FakeMicrosoftOAuthService(),
            defaultRetryDelay: TimeSpan.FromMilliseconds(50), baseUrl: _server.Urls[0]);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task PatchRead_ResolvesEventByGlobalId_ViaMeEvents()
    {
        _server.Given(Request.Create().WithPath("/me/events/AAMkAGlobalId42").UsingPatch())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new { id = "AAMkAGlobalId42", subject = "Renamed" }));

        var updated = await _client.PatchReadAsync<TestItem>(
            _account, "/me/events/AAMkAGlobalId42", new { subject = "Renamed" },
            scopes: null, silentOnly: false, headers: null, TestContext.Current.CancellationToken);

        Assert.Equal("AAMkAGlobalId42", updated!.Id);
        var request = Assert.Single(_server.LogEntries).RequestMessage!;
        // Graph's documented contract (issue #304 item 4): the write resolves by the event's
        // GLOBAL id — no calendar-scoped path required, unlike Google's calendars/{id}/events.
        Assert.Equal("/me/events/AAMkAGlobalId42", request.Path);
        Assert.Equal("Bearer fake-graph-token", Assert.Contains("Authorization", request.Headers!)[0]);
    }

    [Fact]
    public async Task GetAllPages_FollowsODataNextLink_UntilExhausted()
    {
        // Raw JSON bodies: the "@odata.nextLink" property name is not expressible as an
        // anonymous-object member. The nextLink is absolute, exactly as Graph returns it —
        // the client must follow it as-is.
        _server.Given(Request.Create().WithPath("/me/messages").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody($$"""
                       { "value": [ { "id": "m1", "subject": "one" } ],
                         "@odata.nextLink": "{{_server.Urls[0]}}/me/messages-page2" }
                       """));
        _server.Given(Request.Create().WithPath("/me/messages-page2").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{ "value": [ { "id": "m2", "subject": "two" } ] }"""));

        var items = await _client.GetAllPagesAsync<TestItem>(
            _account, "/me/messages", TestContext.Current.CancellationToken);

        Assert.Equal(2, items.Count);
        Assert.Equal(["m1", "m2"], items.Select(i => i.Id));
    }

    [Fact]
    public async Task Throttled429_IsRetried_ThenSucceeds()
    {
        _server.Given(Request.Create().WithPath("/me/events/throttled").UsingPatch())
               .InScenario("throttle")
               .WillSetStateTo("retried")
               .RespondWith(Response.Create().WithStatusCode(429));
        _server.Given(Request.Create().WithPath("/me/events/throttled").UsingPatch())
               .InScenario("throttle")
               .WhenStateIs("retried")
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new { id = "throttled", subject = "ok" }));

        var result = await _client.PatchReadAsync<TestItem>(
            _account, "/me/events/throttled", new { subject = "ok" },
            scopes: null, silentOnly: false, headers: null, TestContext.Current.CancellationToken);

        Assert.Equal("throttled", result!.Id);
        Assert.Equal(2, _server.LogEntries.Count); // one 429 + one retry
    }

    [Fact]
    public async Task Failure_CarriesStatusCode_OnTheException()
    {
        _server.Given(Request.Create().WithPath("/me/events/gone").UsingPatch())
               .RespondWith(Response.Create().WithStatusCode(404)
                   .WithBodyAsJson(new { error = new { code = "ErrorItemNotFound", message = "The specified object was not found." } }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _client.PatchReadAsync<TestItem>(
            _account, "/me/events/gone", new { subject = "x" },
            scopes: null, silentOnly: false, headers: null, TestContext.Current.CancellationToken));

        // The status must ride on the typed property (callers branch on it — e.g. server rules
        // mapping 403 to a consent-required exception), not only in the message text.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("ErrorItemNotFound", ex.Message);
    }
}

/// <summary>Silent-token members return a fixed token; interactive members throw.</summary>
internal sealed class FakeMicrosoftOAuthService : IOAuthService
{
    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default) =>
        Task.FromResult("fake-graph-token");
    public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default) =>
        Task.FromResult("fake-graph-token");
    public Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default) =>
        Task.FromResult("fake-graph-token");
    public Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default) =>
        throw new NotSupportedException("Interactive sign-in is not available in tests.");
    public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel account, CancellationToken ct = default) =>
        throw new NotSupportedException("Interactive sign-in is not available in tests.");
    public Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default) =>
        throw new NotSupportedException("Consent flows are not available in tests.");
    public Task RequestCalendarConsentAsync(AccountModel account, CancellationToken ct = default) =>
        throw new NotSupportedException("Consent flows are not available in tests.");
    public Task SignOutAsync(AccountModel account) =>
        throw new NotSupportedException("Sign-out is not available in tests.");
}
