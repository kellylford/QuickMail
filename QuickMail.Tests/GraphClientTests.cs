using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services.Graph;
using Xunit;

namespace QuickMail.Tests;

public class GraphClientTests
{
    /// <summary>Returns a queued sequence of responses, counting how many requests it served.</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public int Calls { get; private set; }

        public SequenceHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var resp = _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            return Task.FromResult(resp);
        }
    }

    /// <summary>An HttpResponseMessage that records whether it was disposed.</summary>
    private sealed class TrackingResponse : HttpResponseMessage
    {
        public bool Disposed { get; private set; }
        public TrackingResponse(HttpStatusCode code) : base(code)
            => Content = new StringContent("{}", Encoding.UTF8, "application/json");
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static HttpResponseMessage Resp(HttpStatusCode code, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (retryAfter.HasValue) r.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        return r;
    }

    private static GraphClient Client(HttpMessageHandler handler)
        => new(new StubOAuthService(), new HttpClient(handler), defaultRetryDelay: TimeSpan.Zero);

    private static AccountModel Account() => new() { Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph };

    [Fact]
    public async Task Retries429ThenSucceeds()
    {
        var handler = new SequenceHandler(Resp((HttpStatusCode)429), Resp(HttpStatusCode.OK));
        var client = Client(handler);

        var result = await client.GetAsync<JsonElement>(Account(), "/me", TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls); // one 429 retry, then success
    }

    [Fact]
    public async Task RequestCount_CountsScopedRequests_IncludingRetries()
    {
        // #462: a 429 then OK is two physical requests; the scoped counter sees both.
        var handler = new SequenceHandler(Resp((HttpStatusCode)429), Resp(HttpStatusCode.OK));
        var client = Client(handler);

        var box = GraphClient.BeginRequestCount();
        await client.GetAsync<JsonElement>(Account(), "/me", TestContext.Current.CancellationToken);
        GraphClient.EndRequestCount();

        Assert.Equal(2, box.Value);
    }

    [Fact]
    public async Task RequestCount_FreshScope_ExcludesRequestsMadeOutsideIt()
    {
        // A request made with no active scope must not be attributed to a later scope (#462).
        var handler = new SequenceHandler(Resp(HttpStatusCode.OK), Resp(HttpStatusCode.OK));
        var client = Client(handler);

        await client.GetAsync<JsonElement>(Account(), "/me", TestContext.Current.CancellationToken); // unscoped

        var box = GraphClient.BeginRequestCount();
        await client.GetAsync<JsonElement>(Account(), "/me", TestContext.Current.CancellationToken); // scoped
        GraphClient.EndRequestCount();

        Assert.Equal(1, box.Value);
    }

    [Fact]
    public async Task RequestCount_ConcurrentScopes_DoNotBleedIntoEachOther()
    {
        // Justifies AsyncLocal over a shared static: two independent flows counting at the same time
        // must each see only their own requests. A global counter would report the sum (8) in both.
        var client = Client(new SequenceHandler());   // empty queue → every response defaults to 200 OK
        var ct = TestContext.Current.CancellationToken;

        async Task<long> ScopedRequests(int n)
        {
            var box = GraphClient.BeginRequestCount();
            for (var i = 0; i < n; i++)
                await client.GetAsync<JsonElement>(Account(), "/me", ct);
            GraphClient.EndRequestCount();
            return box.Value;
        }

        var counts = await Task.WhenAll(Task.Run(() => ScopedRequests(3)), Task.Run(() => ScopedRequests(5)));

        Assert.Contains(3L, counts);
        Assert.Contains(5L, counts);
        Assert.DoesNotContain(8L, counts);
    }

    [Fact]
    public async Task HonorsRetryAfterHeader_AndStillSucceeds()
    {
        var handler = new SequenceHandler(Resp((HttpStatusCode)429, retryAfter: TimeSpan.Zero), Resp(HttpStatusCode.OK));
        var client = Client(handler);

        await client.GetAsync<JsonElement>(Account(), "/me", TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task MissingRetryAfter_UsesDefaultDelay_AndStillRetries()
    {
        // No Retry-After header → falls back to the (injected, zero) default delay.
        var handler = new SequenceHandler(Resp((HttpStatusCode)429), Resp(HttpStatusCode.OK));
        var client = Client(handler);

        await client.GetAsync<JsonElement>(Account(), "/me", TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AfterThreeAttempts_429_IsReturnedToCaller()
    {
        // Three 429s → after attempt 2 the loop gives up and returns the 429, which GetAsync surfaces.
        var handler = new SequenceHandler(Resp((HttpStatusCode)429), Resp((HttpStatusCode)429), Resp((HttpStatusCode)429));
        var client = Client(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync<JsonElement>(Account(), "/me", TestContext.Current.CancellationToken));
        Assert.Equal(3, handler.Calls); // attempts 0, 1, 2 — no fourth
    }

    [Fact]
    public async Task IntermediateResponses_AreDisposedBetweenRetries()
    {
        var throttled = new TrackingResponse((HttpStatusCode)429);
        var handler = new SequenceHandler(throttled, Resp(HttpStatusCode.OK));
        var client = Client(handler);

        await client.GetAsync<JsonElement>(Account(), "/me", TestContext.Current.CancellationToken);

        Assert.True(throttled.Disposed, "the retried 429 response should be disposed before the next attempt");
    }
}
