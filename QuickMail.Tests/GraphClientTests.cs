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
