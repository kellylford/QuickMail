using System;
using System.Collections.Concurrent;
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
/// Tests for <see cref="GraphChangeNotifier"/> delta polling and the delta-cursor store methods.
/// Each test uses a fresh temp-directory profile so the real SQLite DeltaToken table is exercised.
/// </summary>
public class GraphChangeNotifierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;

    public GraphChangeNotifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-graphnotif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>Routes each request to a canned JSON response by URL, recording the requested URLs in order.</summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, string Json)> _respond;
        public ConcurrentQueue<string> Requests { get; } = new();

        public StubHttpHandler(Func<string, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Enqueue(url);
            var (status, json) = _respond(url);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AccountModel GraphAccount() => new()
    {
        Id = Guid.NewGuid(),
        AccountName = "M365",
        Username = "user@contoso.com",
        BackendKind = BackendKind.MicrosoftGraph,
    };

    private GraphChangeNotifier MakeNotifier(StubHttpHandler handler) =>
        new(new GraphClient(new StubOAuthService(), new HttpClient(handler)), _store);

    private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition()) return;
            await Task.Delay(15);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }

    [Fact]
    public async Task FirstPoll_WithMessages_RaisesInboxNewMail_AndPersistsDeltaLink()
    {
        const string deltaLink = "https://graph.microsoft.com/v1.0/me/mailFolders/Inbox/messages/delta?$deltatoken=NEW";
        var handler = new StubHttpHandler(_ =>
            (HttpStatusCode.OK, $$"""{"value":[{"id":"m1"}],"@odata.deltaLink":"{{deltaLink}}"}"""));

        var account = GraphAccount();
        using var notifier = MakeNotifier(handler);
        var raised = new TaskCompletionSource<Guid>();
        notifier.InboxNewMailDetected += id => raised.TrySetResult(id);

        notifier.StartWatchers(new[] { account }, TestContext.Current.CancellationToken);

        var raisedId = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(account.Id, raisedId);

        // First request used the fresh delta enumeration URL (no stored cursor yet).
        Assert.Contains("/messages/delta?$select=id", handler.Requests.First());

        // The final page's deltaLink is persisted for the next tick.
        await WaitForAsync(async () => await _store.GetDeltaTokenAsync(account.Id, "Inbox") == deltaLink);
        notifier.StopWatchers();
    }

    [Fact]
    public async Task Poll_WithStoredCursor_RequestsStoredDeltaLinkVerbatim()
    {
        var account = GraphAccount();
        const string storedCursor = "https://graph.microsoft.com/v1.0/me/mailFolders/Inbox/messages/delta?$deltatoken=SEEDED";
        await _store.SetDeltaTokenAsync(account.Id, "Inbox", storedCursor);

        var firstRequest = new TaskCompletionSource<string>();
        var handler = new StubHttpHandler(url =>
        {
            firstRequest.TrySetResult(url);
            return (HttpStatusCode.OK, """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/x?$deltatoken=NEXT"}""");
        });

        using var notifier = MakeNotifier(handler);
        notifier.StartWatchers(new[] { account }, TestContext.Current.CancellationToken);

        var requested = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(storedCursor, requested); // requested verbatim, not the fresh delta URL
        notifier.StopWatchers();
    }

    [Fact]
    public async Task Poll_FollowsNextLinkAcrossPages_PersistsFinalDeltaLink()
    {
        const string page2 = "https://graph.microsoft.com/v1.0/me/mailFolders/Inbox/messages/delta?$skiptoken=PAGE2";
        const string finalDelta = "https://graph.microsoft.com/v1.0/me/mailFolders/Inbox/messages/delta?$deltatoken=FINAL";
        var handler = new StubHttpHandler(url =>
            url.Contains("$skiptoken=PAGE2")
                ? (HttpStatusCode.OK, $$"""{"value":[{"id":"m2"}],"@odata.deltaLink":"{{finalDelta}}"}""")
                : (HttpStatusCode.OK, $$"""{"value":[{"id":"m1"}],"@odata.nextLink":"{{page2}}"}"""));

        var account = GraphAccount();
        using var notifier = MakeNotifier(handler);
        notifier.StartWatchers(new[] { account }, TestContext.Current.CancellationToken);

        // The final page's deltaLink (not the within-tick nextLink) is what gets persisted.
        await WaitForAsync(async () => await _store.GetDeltaTokenAsync(account.Id, "Inbox") == finalDelta);
        Assert.Contains(handler.Requests, u => u.Contains("$skiptoken=PAGE2")); // second page was followed
        notifier.StopWatchers();
    }

    [Fact]
    public void StartWatchers_IgnoresNonGraphAccounts()
    {
        var handler = new StubHttpHandler(_ => (HttpStatusCode.OK, "{}"));
        var imapAccount = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp };
        using var notifier = MakeNotifier(handler);

        notifier.StartWatchers(new[] { imapAccount }, TestContext.Current.CancellationToken);
        notifier.StopWatchers();

        // No Graph account → no poll task → no HTTP request ever issued.
        Assert.Empty(handler.Requests);
    }
}

/// <summary>Round-trip tests for the DeltaToken store methods on the real SQLite store.</summary>
public class DeltaTokenStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;

    public DeltaTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-delta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetDeltaToken_ReturnsNull_WhenNoneStored()
        => Assert.Null(await _store.GetDeltaTokenAsync(Guid.NewGuid(), "Inbox"));

    [Fact]
    public async Task SetThenGet_RoundTrips()
    {
        var account = Guid.NewGuid();
        await _store.SetDeltaTokenAsync(account, "Inbox", "https://graph/delta?$deltatoken=ABC");
        Assert.Equal("https://graph/delta?$deltatoken=ABC", await _store.GetDeltaTokenAsync(account, "Inbox"));
    }

    [Fact]
    public async Task Set_OverwritesExistingCursor()
    {
        var account = Guid.NewGuid();
        await _store.SetDeltaTokenAsync(account, "Inbox", "first");
        await _store.SetDeltaTokenAsync(account, "Inbox", "second");
        Assert.Equal("second", await _store.GetDeltaTokenAsync(account, "Inbox"));
    }

    [Fact]
    public async Task DeltaToken_IsScopedPerAccount()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await _store.SetDeltaTokenAsync(a, "Inbox", "token-a");
        Assert.Equal("token-a", await _store.GetDeltaTokenAsync(a, "Inbox"));
        Assert.Null(await _store.GetDeltaTokenAsync(b, "Inbox"));
    }
}
