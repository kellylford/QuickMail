using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The app's one answer to "are we online?" (#637). Two things have to be true of it: a blip must
/// not announce "Offline" to a screen-reader user, and coming back must be immediate — the Outbox
/// drain and the "Back online." announcement both hang off it.
/// </summary>
public class ConnectivityServiceTests
{
    private sealed class FakeNetworkSource : INetworkAvailabilitySource
    {
        public bool IsAvailable { get; private set; } = true;
        public event Action<bool>? AvailabilityChanged;
        public int Subscribers => AvailabilityChanged?.GetInvocationList().Length ?? 0;
        public bool Disposed { get; private set; }
        public void Set(bool available) { IsAvailable = available; AvailabilityChanged?.Invoke(available); }
        public void Dispose() => Disposed = true;
    }

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private sealed class Harness : IDisposable
    {
        public FakeNetworkSource Network { get; } = new();
        public ConnectivityService Service { get; }
        public List<bool> Online { get; } = [];
        public List<(Guid Id, bool Online)> Accounts { get; } = [];
        public List<bool> NetworkEvents { get; } = [];

        public Harness(bool networkUp = true)
        {
            if (!networkUp) Network.Set(false);
            Service = new ConnectivityService(Network, Debounce);
            Service.OnlineChanged += o => { lock (Online) Online.Add(o); };
            Service.AccountOnlineChanged += (id, o) => { lock (Accounts) Accounts.Add((id, o)); };
            Service.NetworkAvailabilityChanged += a => { lock (NetworkEvents) NetworkEvents.Add(a); };
        }

        public async Task SettleAsync() => await Task.Delay(Debounce * 4);

        public void Dispose() => Service.Dispose();
    }

    [Fact]
    public void StartsOnlineWithNoEvent()
    {
        using var h = new Harness();
        Assert.True(h.Service.IsOnline);
        Assert.True(h.Service.IsNetworkAvailable);
        Assert.Empty(h.Online);
    }

    [Fact]
    public void StartsOfflineWhenThereIsNoNetworkWithNoEvent()
    {
        using var h = new Harness(networkUp: false);
        Assert.False(h.Service.IsOnline);
        Assert.Empty(h.Online);
    }

    [Fact]
    public async Task NetworkLossIsAnnouncedOnlyAfterTheDebounce()
    {
        using var h = new Harness();

        h.Network.Set(false);

        Assert.Equal([false], h.NetworkEvents);   // the raw signal is immediate
        Assert.True(h.Service.IsOnline);          // the verdict is held
        Assert.Empty(h.Online);
        await h.SettleAsync();
        Assert.False(h.Service.IsOnline);
        Assert.Equal([false], h.Online);
    }

    [Fact]
    public async Task AFlapInsideTheDebounceAnnouncesNothing()
    {
        using var h = new Harness();

        h.Network.Set(false);
        h.Network.Set(true);
        await h.SettleAsync();

        Assert.True(h.Service.IsOnline);
        Assert.Empty(h.Online);
        Assert.Equal([false, true], h.NetworkEvents);
    }

    [Fact]
    public async Task EveryAccountUnreachableIsOfflineAndOneReachableIsOnlineAtOnce()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        h.Service.NoteAccountUnreachable(a, "initial-connect");
        Assert.True(h.Service.IsOnline);            // b is still unknown, i.e. presumed fine
        h.Service.NoteAccountUnreachable(b, "initial-connect");
        await h.SettleAsync();
        Assert.False(h.Service.IsOnline);
        Assert.Equal([false], h.Online);

        h.Service.NoteAccountReachable(a, "folder-loaded");
        Assert.True(h.Service.IsOnline);            // no debounce on the way back
        Assert.Equal([false, true], h.Online);
        Assert.True(h.Service.IsAccountOnline(a));
        Assert.False(h.Service.IsAccountOnline(b));
    }

    [Fact]
    public void OperationOutcomesAreClassified()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();

        h.Service.NoteOperationOutcome(a, new ImapCommandException(ImapCommandResponse.No, "denied"), "folder-load-failed");
        Assert.Equal(AccountConnectivity.Online, h.Service.AccountState(a));   // the server spoke

        h.Service.NoteOperationOutcome(a, new SocketException((int)SocketError.HostUnreachable), "folder-load-failed");
        Assert.Equal(AccountConnectivity.Offline, h.Service.AccountState(a));

        h.Service.NoteOperationOutcome(a, null, "folder-loaded");
        Assert.Equal(AccountConnectivity.Online, h.Service.AccountState(a));
    }

    [Fact]
    public void ACallerCancellationIsNotAnOutage()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        h.Service.NoteOperationOutcome(a, new OperationCanceledException(cts.Token), "message-load-failed", cts.Token);

        Assert.Equal(AccountConnectivity.Online, h.Service.AccountState(a));
    }

    [Fact]
    public void AccountFlipsFireOncePerFlipNotPerNote()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();

        h.Service.NoteAccountReachable(a, "x");      // Unknown → Online: not a flip (Unknown counts as online)
        h.Service.NoteAccountReachable(a, "x");
        h.Service.NoteAccountUnreachable(a, "y");    // flip
        h.Service.NoteAccountUnreachable(a, "y");
        h.Service.NoteAccountReachable(a, "z");      // flip
        h.Service.NoteAccountUnreachable(a, "first-ever");

        Assert.Equal([(a, false), (a, true), (a, false)], h.Accounts);
    }

    [Fact]
    public async Task ForgettingTheOnlyOfflineAccountReturnsTheAppToOnline()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();
        h.Service.NoteAccountUnreachable(a, "initial-connect");
        await h.SettleAsync();
        Assert.False(h.Service.IsOnline);

        h.Service.Forget(a);

        Assert.True(h.Service.IsOnline);
        Assert.Equal([false, true], h.Online);
    }

    [Fact]
    public async Task NetworkDownMakesEveryAccountOffline()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();
        h.Service.NoteAccountReachable(a, "x");

        h.Network.Set(false);
        await h.SettleAsync();

        Assert.False(h.Service.IsAccountOnline(a));
        Assert.Equal(AccountConnectivity.Online, h.Service.AccountState(a));   // the account itself was fine
    }

    [Fact]
    public async Task DisposeUnsubscribesAndSilences()
    {
        var h = new Harness();
        h.Service.Dispose();

        h.Network.Set(false);
        await h.SettleAsync();

        Assert.Equal(0, h.Network.Subscribers);
        Assert.True(h.Network.Disposed);
        Assert.Empty(h.Online);
        Assert.Empty(h.NetworkEvents);
    }
}
