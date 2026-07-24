using System.Diagnostics;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// IDLE-watcher behavior against a real server (live-content testing plan §4.4):
/// <list type="bullet">
/// <item>#126 — connecting an additional account must NOT drop/restart the existing accounts'
/// IDLE connections (end-to-end half; the pure-logic half is WatcherReconcilerTests).</item>
/// <item>IDLE fidelity — GreenMail pushes EXISTS on new mail and the watcher surfaces it as
/// InboxNewMailDetected (the Phase-2 acceptance check from the plan).</item>
/// <item>#314 — AccountReachabilityChanged tracks real socket outcomes: unreachable on drop,
/// reachable again once the watcher reconnects.</item>
/// </list>
/// Each account gets its own <see cref="TcpRelay"/> so connection counts and kills are
/// per-account observable.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class WatcherTests : IDisposable
{
    private readonly GreenMailFixture _greenMail;
    private readonly List<TcpRelay> _relays = new();

    public WatcherTests(GreenMailFixture greenMail) => _greenMail = greenMail;

    public void Dispose()
    {
        foreach (var relay in _relays)
            relay.Dispose();
    }

    private (AccountModel Account, TcpRelay Relay) CreateRelayedAccount(string prefix)
    {
        var relay = new TcpRelay(GreenMailFixture.Host, GreenMailFixture.ImapPort);
        _relays.Add(relay);
        var account = _greenMail.CreateAccount(prefix);
        account.ImapPort = relay.Port;
        return (account, relay);
    }

    [Fact]
    public async Task StartWatchers_AddingThirdAccount_DoesNotRestartExistingWatchers()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var (a1, r1) = CreateRelayedAccount("watch1");
        var (a2, r2) = CreateRelayedAccount("watch2");
        var (a3, r3) = CreateRelayedAccount("watch3");

        using var imap = new ImapMailService(new NoOpOAuthService());
        try
        {
            await imap.ConnectAsync(a1, "pw", ct);
            await imap.ConnectAsync(a2, "pw", ct);
            var pool1 = r1.TotalAccepted;
            var pool2 = r2.TotalAccepted;

            imap.StartWatchers([a1, a2], ct);
            await WaitUntilAsync(() => r1.TotalAccepted == pool1 + 1 && r2.TotalAccepted == pool2 + 1,
                TimeSpan.FromSeconds(15), "watchers for accounts 1 and 2 never connected", ct);
            var watched1 = r1.TotalAccepted;
            var watched2 = r2.TotalAccepted;

            // The #126 regression: reconciling the watcher set for a third account restarted
            // every watcher, dropping accounts 1 and 2 mid-IDLE. Adding account 3 must open
            // exactly one new connection — on relay 3 — and none on relays 1 and 2.
            await imap.ConnectAsync(a3, "pw", ct);
            var pool3 = r3.TotalAccepted;
            imap.StartWatchers([a1, a2, a3], ct);
            await WaitUntilAsync(() => r3.TotalAccepted == pool3 + 1,
                TimeSpan.FromSeconds(15), "watcher for account 3 never connected", ct);

            // Settle window: a restart of watchers 1/2 would show up as extra accepts.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            Assert.Equal(watched1, r1.TotalAccepted);
            Assert.Equal(watched2, r2.TotalAccepted);
        }
        finally
        {
            imap.StopWatchers();
            await imap.DisconnectAsync(a1.Id, ct);
            await imap.DisconnectAsync(a2.Id, ct);
            await imap.DisconnectAsync(a3.Id, ct);
        }
    }

    [Fact]
    public async Task IdleWatcher_SurfacesNewMail_AsInboxNewMailDetected()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var (account, relay) = CreateRelayedAccount("idle-push");

        using var imap = new ImapMailService(new NoOpOAuthService());
        var detected = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        imap.InboxNewMailDetected += id => detected.TrySetResult(id);

        try
        {
            await imap.ConnectAsync(account, "pw", ct);
            var pool = relay.TotalAccepted;
            imap.StartWatchers([account], ct);
            await WaitUntilAsync(() => relay.TotalAccepted == pool + 1,
                TimeSpan.FromSeconds(15), "watcher never connected", ct);
            // Brief grace so the watcher is inside IdleAsync before mail arrives.
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            await SeedAsync(account.Username, ct);

            var winner = await Task.WhenAny(detected.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            Assert.True(winner == detected.Task, "IDLE watcher never surfaced the new message.");
            Assert.Equal(account.Id, await detected.Task);
        }
        finally
        {
            imap.StopWatchers();
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    [Fact]
    public async Task Reachability_TracksSocketOutcomes_DownOnDropUpOnReconnect()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var (account, relay) = CreateRelayedAccount("reach");

        using var imap = new ImapMailService(new NoOpOAuthService());
        var events = new List<(Guid Id, bool Reachable)>();
        var gotDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotUp   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        imap.AccountReachabilityChanged += (id, reachable) =>
        {
            lock (events) events.Add((id, reachable));
            if (!reachable) gotDown.TrySetResult();
            else gotUp.TrySetResult();
        };

        try
        {
            await imap.ConnectAsync(account, "pw", ct);
            var pool = relay.TotalAccepted;
            imap.StartWatchers([account], ct);
            await WaitUntilAsync(() => relay.TotalAccepted == pool + 1,
                TimeSpan.FromSeconds(15), "watcher never connected", ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            // Drop the watcher's socket: IdleAsync faults, the first retry marks the account
            // unreachable (#314 — the UI 'connected' state must track real socket outcomes).
            Assert.True(relay.KillActiveConnections() >= 1, "Kill hit no connections — nothing to recover from.");
            var downWinner = await Task.WhenAny(gotDown.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            Assert.True(downWinner == gotDown.Task, "No unreachable event after the watcher socket was severed.");

            // The watcher's first retry is scheduled at 30s ±30% jitter; when it reconnects
            // (the relay listener stayed up) it must flip the account back to reachable.
            // This wait dominates the test's runtime — that's the price of asserting the
            // recovery half of #314 for real.
            var upWinner = await Task.WhenAny(gotUp.Task, Task.Delay(TimeSpan.FromSeconds(75), ct));
            Assert.True(upWinner == gotUp.Task, "No reachable event after the watcher reconnected.");

            lock (events)
            {
                Assert.Equal(account.Id, events[0].Id);
                Assert.False(events[0].Reachable);
                Assert.Contains(events, e => e.Reachable);
            }
        }
        finally
        {
            imap.StopWatchers();
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException(failure);
    }

    private static async Task SeedAsync(string recipient, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = "idle push test";
        message.Body = new TextPart("plain") { Text = "new mail for the idle watcher" };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.SmtpPort, SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
