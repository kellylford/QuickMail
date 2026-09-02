using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Offline detection (#637): the view model feeds <see cref="IConnectivityService"/> from every
/// real outcome against a server (see <c>ApplyAccountStatus</c> and the fetch paths) and reacts to
/// its verdict — reconnecting when the network returns, retrying on a backoff while it is not, and
/// dropping an account from the connected set when it goes unreachable.
/// </summary>
public partial class MainViewModel
{
    private readonly IConnectivityService? _connectivity;
    private Action<bool>? _onOnlineChanged;
    private Action<Guid, bool>? _onAccountOnlineChanged;
    private Action<bool>? _onNetworkAvailabilityChanged;
    private CancellationTokenSource? _offlineRetryCts;
    private volatile bool _offlineRetryRunning;

    /// <summary>First wait of the offline retry loop; doubles to five minutes. Settable so tests do not wait.</summary>
    internal TimeSpan OfflineRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    private void SubscribeConnectivity()
    {
        if (_connectivity == null) return;
        // The service raises on ThreadPool and timer threads; everything here touches UI-owned state.
        _onOnlineChanged              = online => _ui.Post(() => OnOnlineChanged(online));
        _onAccountOnlineChanged       = (id, online) => _ui.Post(() => OnAccountOnlineChanged(id, online));
        _onNetworkAvailabilityChanged = available => _ui.Post(() => OnNetworkAvailabilityChanged(available));
        _connectivity.OnlineChanged              += _onOnlineChanged;
        _connectivity.AccountOnlineChanged       += _onAccountOnlineChanged;
        _connectivity.NetworkAvailabilityChanged += _onNetworkAvailabilityChanged;
    }

    private void UnsubscribeConnectivity()
    {
        if (_connectivity == null) return;
        if (_onOnlineChanged != null)              _connectivity.OnlineChanged              -= _onOnlineChanged;
        if (_onAccountOnlineChanged != null)       _connectivity.AccountOnlineChanged       -= _onAccountOnlineChanged;
        if (_onNetworkAvailabilityChanged != null) _connectivity.NetworkAvailabilityChanged -= _onNetworkAvailabilityChanged;
        _onOnlineChanged = null;
        _onAccountOnlineChanged = null;
        _onNetworkAvailabilityChanged = null;
    }

    /// <summary>True when the app is known to be offline; null service means "assume online".</summary>
    private bool IsKnownOffline => _connectivity is { IsOnline: false };

    // ── What a failed connect says about the network ────────────────────────────

    public enum ConnectFailureKind
    {
        /// <summary>Nothing reached the server: no stored password, a sign-in the user must do. Says nothing about the network.</summary>
        NotAttempted,
        /// <summary>The server answered and refused. It is reachable.</summary>
        ServerRefused,
        /// <summary>The server could not be reached.</summary>
        Transport,
    }

    // Written by ConnectOneAccountAsync on a background thread, read by ApplyAccountStatus on the UI
    // thread, cleared on a successful connect.
    private readonly ConcurrentDictionary<Guid, ConnectFailureKind> _lastConnectFailure = new();

    internal static ConnectFailureKind ClassifyConnectFailure(Exception ex)
        => ConnectionFailure.IsConnectionFailure(ex, CancellationToken.None)
            ? ConnectFailureKind.Transport
            : ConnectFailureKind.ServerRefused;

    /// <summary>
    /// Feeds the connectivity service from a disconnected outcome, using what the connect recorded.
    /// Callers other than the connect paths (the IDLE watcher's reachability event, a folder load
    /// that failed) record nothing and are transport failures by construction.
    /// </summary>
    private void NoteConnectOutcome(Guid accountId, string source)
    {
        if (_connectivity == null) return;
        if (!_lastConnectFailure.TryRemove(accountId, out var kind))
            kind = ConnectFailureKind.Transport;
        switch (kind)
        {
            case ConnectFailureKind.Transport:     _connectivity.NoteAccountUnreachable(accountId, source); break;
            case ConnectFailureKind.ServerRefused: _connectivity.NoteAccountReachable(accountId, source);   break;
            default: break;   // NotAttempted: no verdict either way
        }
    }

    /// <summary>A server on this machine needs no network; the no-network shortcut must not skip it.</summary>
    internal static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    // ── The connection status label ─────────────────────────────────────────────

    public enum ConnectionPhase { Idle, Connecting, Syncing }

    private ConnectionPhase _connectionPhase;
    private bool _announcedOffline;

    /// <summary>
    /// Sets the phase and recomputes <see cref="ConnectionStatusText"/>. The label used to be
    /// written from eight sites with eight different ideas of what it meant; now it is derived
    /// from the phase, the connectivity verdict and the connected count (#637).
    /// </summary>
    private void SetConnectionPhase(ConnectionPhase phase)
    {
        _connectionPhase = phase;
        UpdateConnectionStatusText();
    }

    private void UpdateConnectionStatusText() =>
        ConnectionStatusText = ConnectionStatusFor(_connectionPhase, !IsKnownOffline, _connectedAccountIds.Count, Accounts.Count);

    internal static string ConnectionStatusFor(ConnectionPhase phase, bool online, int connected, int accounts) => phase switch
    {
        ConnectionPhase.Connecting => "Connecting…",
        ConnectionPhase.Syncing    => "Syncing…",
        _ when accounts == 0       => "No accounts",
        _ when !online             => "Offline",
        _ when connected == 0      => "Offline",
        _                          => $"{connected} account{(connected == 1 ? "" : "s")} connected",
    };

    /// <summary>"Offline. Showing cached messages." once per outage; "Back online." only after it was said.</summary>
    private void AnnounceOfflineOnce()
    {
        if (_announcedOffline) return;
        _announcedOffline = true;
        Announce(OnlineMode ? "Offline." : "Offline. Showing cached messages.", AnnouncementCategory.Status);
    }

    /// <summary>Chooses the status-bar wording for a failed server call: offline wording for a transport failure, the raw error otherwise.</summary>
    private static string OfflineOrErrorStatus(Exception ex, CancellationToken ct, Func<string> offline, Func<string> error)
        => ConnectionFailure.IsConnectionFailure(ex, ct) ? offline() : error();

    private static string CachedCountText(int count)
        => $"Offline — showing {count} cached {(count == 1 ? "message" : "messages")}.";

    // ── Reacting to the verdict ─────────────────────────────────────────────────

    private void OnNetworkAvailabilityChanged(bool available)
    {
        // Nothing to do on loss: in-flight operations fail on their own and feed the service, and
        // the debounced OnlineChanged(false) is what the user hears. On return, reconnect at once.
        if (available)
            ReconnectOfflineAccountsAsync("network-returned").LogFaults("reconnect after network returned");
    }

    private void OnOnlineChanged(bool online)
    {
        UpdateConnectionStatusText();
        if (online)
        {
            StopOfflineRetryLoop();
            if (_announcedOffline)
            {
                _announcedOffline = false;
                Announce("Back online.", AnnouncementCategory.Status);
            }
        }
        else
        {
            AnnounceOfflineOnce();
            StartOfflineRetryLoop();
        }
    }

    private void OnAccountOnlineChanged(Guid accountId, bool online)
    {
        UpdateConnectionStatusText();
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null) return;

        if (online)
        {
            // Back: a folder listing or a message fetch just succeeded, so the backend session is
            // live. Put the account back in the connected set and on its row — the mirror of the
            // removal below, which used to have no counterpart.
            if (!_connectedAccountIds.Contains(accountId)
                && _imap.IsConnected(accountId)
                && _cachedFolders.TryGetValue(accountId, out var folders))
            {
                _connectedAccountIds.Add(accountId);
                ApplyAccountStatus(account, folders, "connectivity-online");
                UpdateConnectionStatusText();
            }
            return;
        }

        // This is the one place an id leaves the connected set other than account removal: every
        // reader of _connectedAccountIds wants "currently reachable", and a sweep or a watcher
        // started against a dead account is wasted work at best.
        _connectedAccountIds.Remove(accountId);
        if (account.IsConnected)
            ApplyAccountStatus(account, null, "connectivity-offline");
        UpdateConnectionStatusText();
    }

    /// <summary>
    /// While the app is offline with the network up (a captive portal, a server outage), retries the
    /// connect on a jittered backoff — 30 s, 60 s, 120 s, then every 5 minutes — until something
    /// answers. Attempts are skipped while the NIC is down; the network-returned handler covers that.
    /// The loop waits first and asks the service second: at the moment it is started the offline
    /// verdict may still be inside its debounce.
    /// </summary>
    private void StartOfflineRetryLoop()
    {
        if (_connectivity == null) return;
        if (_offlineRetryRunning) return;
        _offlineRetryRunning = true;
        ReplaceCts(ref _offlineRetryCts, out var ct);
        _ = RunOfflineRetryLoopAsync(ct);
    }

    private void StopOfflineRetryLoop()
    {
        DrainCts(ref _offlineRetryCts);
        _offlineRetryRunning = false;
    }

    private async Task RunOfflineRetryLoopAsync(CancellationToken ct)
    {
        var delay = OfflineRetryBaseDelay;
        var cap = TimeSpan.FromMinutes(5);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var jittered = TimeSpan.FromSeconds(JitteredBackoffSeconds((int)Math.Max(1, delay.TotalSeconds)));
                await Task.Delay(delay < TimeSpan.FromSeconds(2) ? delay : jittered, ct).ConfigureAwait(false);
                delay = delay * 2 > cap ? cap : delay * 2;

                if (ct.IsCancellationRequested) break;
                if (_connectivity is not { IsOnline: false }) break;      // online again (or was never offline)
                if (!_connectivity.IsNetworkAvailable) continue;          // the network handler covers this case

                var connected = await ReconnectOfflineAccountsAsync("offline-retry").ConfigureAwait(false);
                if (connected > 0)
                {
                    // Something answered: the reconnect already rewired watchers; refresh what the
                    // user is looking at so the "Offline — cached" status gives way to server truth.
                    _ui.Post(() => RefreshCommand.Execute(null));
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Log("Offline retry loop", ex); }
        finally
        {
            _offlineRetryRunning = false;
        }
    }
}
