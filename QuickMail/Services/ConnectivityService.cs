using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace QuickMail.Services;

/// <summary>
/// See <see cref="IConnectivityService"/>. Two inputs, one verdict: the machine-level network signal
/// and the per-account outcomes the mail code reports. Going offline is held for a debounce so a
/// momentary blip never announces itself; coming back online publishes at once.
/// </summary>
public sealed class ConnectivityService : IConnectivityService, IDisposable
{
    public static readonly TimeSpan DefaultOfflineDebounce = TimeSpan.FromSeconds(5);

    private readonly INetworkAvailabilitySource _network;
    private readonly TimeSpan _offlineDebounce;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AccountConnectivity> _accounts = [];
    private bool _networkAvailable;
    private bool _publishedOnline;
    private Timer? _offlineTimer;
    private bool _disposed;

    public ConnectivityService(INetworkAvailabilitySource network, TimeSpan? offlineDebounce = null)
    {
        _network = network;
        _offlineDebounce = offlineDebounce ?? DefaultOfflineDebounce;
        _networkAvailable = network.IsAvailable;
        // The starting state is not a transition: a launch with no network is simply offline from
        // the first moment, with nothing to debounce and nothing to announce yet.
        _publishedOnline = ComputeOnline();
        _network.AvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public event Action<bool>? OnlineChanged;
    public event Action<Guid, bool>? AccountOnlineChanged;
    public event Action<bool>? NetworkAvailabilityChanged;

    public bool IsNetworkAvailable { get { lock (_gate) return _networkAvailable; } }

    public bool IsOnline { get { lock (_gate) return _publishedOnline; } }

    public bool IsAccountOnline(Guid accountId)
    {
        lock (_gate)
            return _networkAvailable && AccountStateCore(accountId) != AccountConnectivity.Offline;
    }

    public AccountConnectivity AccountState(Guid accountId)
    {
        lock (_gate) return AccountStateCore(accountId);
    }

    private AccountConnectivity AccountStateCore(Guid accountId)
        => _accounts.TryGetValue(accountId, out var s) ? s : AccountConnectivity.Unknown;

    // ── Feeders ─────────────────────────────────────────────────────────────────

    public void NoteAccountReachable(Guid accountId, string source)
        => NoteAccount(accountId, AccountConnectivity.Online, source);

    public void NoteAccountUnreachable(Guid accountId, string source)
        => NoteAccount(accountId, AccountConnectivity.Offline, source);

    public void NoteOperationOutcome(Guid accountId, Exception? ex, string source, CancellationToken callerToken = default)
    {
        if (ex != null && ConnectionFailure.IsConnectionFailure(ex, callerToken))
            NoteAccountUnreachable(accountId, source);
        else
            NoteAccountReachable(accountId, source);
    }

    public void Forget(Guid accountId)
    {
        lock (_gate)
        {
            if (!_accounts.Remove(accountId)) return;
        }
        Recompute($"forget:{accountId}");
    }

    private void NoteAccount(Guid accountId, AccountConnectivity state, string source)
    {
        bool flipped;
        lock (_gate)
        {
            if (_disposed) return;
            var was = AccountStateCore(accountId);
            if (was == state) return;
            _accounts[accountId] = state;
            // Unknown counts as online, so Unknown → Offline is a flip the app should hear about;
            // Unknown → Online is not.
            flipped = state == AccountConnectivity.Offline || was == AccountConnectivity.Offline;
        }

        if (flipped)
        {
            var online = state == AccountConnectivity.Online;
            ConnectionJournal.Record(ConnectionEventKind.Status, accountId.ToString(), "-",
                online ? "account-online" : "account-offline", $"source={source}");
            Raise(() => AccountOnlineChanged?.Invoke(accountId, online), "AccountOnlineChanged");
        }
        Recompute(source);
    }

    private void OnNetworkAvailabilityChanged(bool available)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_networkAvailable == available) return;
            _networkAvailable = available;
        }
        ConnectionJournal.Record(ConnectionEventKind.Status, "-", "-",
            available ? "network-available" : "network-unavailable", "source=NetworkChange");
        Raise(() => NetworkAvailabilityChanged?.Invoke(available), "NetworkAvailabilityChanged");
        Recompute(available ? "network-available" : "network-unavailable");
    }

    // ── The verdict ─────────────────────────────────────────────────────────────

    // Network up, and not every account we know about is unreachable. One reachable account keeps
    // the app online; the others show their own state in the account list.
    private bool ComputeOnline()
        => _networkAvailable && !(_accounts.Count > 0 && _accounts.Values.All(s => s == AccountConnectivity.Offline));

    private void Recompute(string source)
    {
        bool publishOnline = false;
        lock (_gate)
        {
            if (_disposed) return;
            var now = ComputeOnline();
            if (now)
            {
                // Back (or still) online: any pending offline verdict is void.
                CancelOfflineTimerLocked();
                if (!_publishedOnline)
                {
                    _publishedOnline = true;
                    publishOnline = true;
                }
            }
            else if (_publishedOnline && _offlineTimer == null)
            {
                // Hold the offline verdict: a flap that resolves inside the window publishes nothing.
                _offlineTimer = new Timer(_ => PublishOfflineIfStillOffline(), null, _offlineDebounce, Timeout.InfiniteTimeSpan);
            }
        }

        if (publishOnline)
        {
            ConnectionJournal.Record(ConnectionEventKind.Status, "-", "-", "app-online", $"source={source}");
            Raise(() => OnlineChanged?.Invoke(true), "OnlineChanged");
        }
    }

    private void PublishOfflineIfStillOffline()
    {
        lock (_gate)
        {
            CancelOfflineTimerLocked();
            if (_disposed || !_publishedOnline || ComputeOnline()) return;
            _publishedOnline = false;
        }
        ConnectionJournal.Record(ConnectionEventKind.Status, "-", "-", "app-offline",
            () => $"network={IsNetworkAvailable} accounts={DescribeAccounts()}");
        Raise(() => OnlineChanged?.Invoke(false), "OnlineChanged");
    }

    private void CancelOfflineTimerLocked()
    {
        _offlineTimer?.Dispose();
        _offlineTimer = null;
    }

    private string DescribeAccounts()
    {
        lock (_gate)
            return string.Join(",", _accounts.Select(kv => $"{kv.Key:N}={kv.Value}"));
    }

    private static void Raise(Action raise, string what)
    {
        try { raise(); }
        catch (Exception ex) { LogService.Log($"Connectivity: {what} handler", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelOfflineTimerLocked();
        }
        _network.AvailabilityChanged -= OnNetworkAvailabilityChanged;
        _network.Dispose();
    }
}
