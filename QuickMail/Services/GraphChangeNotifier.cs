using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services.Graph;

namespace QuickMail.Services;

/// <summary>
/// Microsoft Graph implementation of <see cref="IChangeNotifier"/>. Graph has no held-connection
/// push for desktop clients (subscriptions need a public callback URL), so new mail is detected by
/// polling each account's INBOX <c>/messages/delta</c> query on an interval. The stored
/// <c>@odata.deltaLink</c> cursor makes each poll incremental: it returns only what changed since the
/// previous tick. IMAP accounts use a held IDLE connection (<see cref="ImapMailService"/>) instead.
/// See dev spec §6.12.
/// </summary>
public sealed class GraphChangeNotifier : IChangeNotifier, IDisposable
{
    private readonly GraphClient _client;
    private readonly ILocalStoreService _store; // delta-cursor persistence
    private readonly IConfigService? _config;
    private readonly Dictionary<Guid, Task> _watchers = new();
    private CancellationTokenSource? _cts;

    public event Action<Guid>? InboxNewMailDetected;
#pragma warning disable CS0067 // Graph polling has no reachability signal; declared to satisfy IChangeNotifier.
    public event Action<Guid, bool>? AccountReachabilityChanged;
#pragma warning restore CS0067

    public GraphChangeNotifier(GraphClient client, ILocalStoreService store, IConfigService? config = null)
    {
        _client = client;
        _store  = store;
        _config = config;
    }

    public void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default)
    {
        StopWatchers();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        foreach (var account in accounts.Where(a => a.BackendKind == BackendKind.MicrosoftGraph))
        {
            var captured = account;
            _watchers[captured.Id] = Task.Run(() => PollLoopAsync(captured, _cts.Token), _cts.Token);
        }
    }

    public void StopWatchers()
    {
        var cts = _cts;
        _cts = null;
        _watchers.Clear();
        if (cts != null)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* best effort */ }
        }
    }

    private async Task PollLoopAsync(AccountModel account, CancellationToken ct)
    {
        var intervalSec = Math.Clamp(_config?.Load().GraphPollSeconds ?? 60, 30, 600);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // The stored cursor is a full @odata.deltaLink URL captured at the end of the previous
                // tick (null on the very first poll → start a fresh delta enumeration). IMPORTANT:
                // @odata.deltaLink (persisted; drives the NEXT tick) and @odata.nextLink/$skipToken
                // (paging WITHIN this tick) are two different cursors. Only the deltaLink is persisted.
                var deltaLink = await _store.GetDeltaTokenAsync(account.Id, "Inbox");
                var url = string.IsNullOrEmpty(deltaLink)
                    ? "/me/mailFolders/Inbox/messages/delta?$select=id"
                    : deltaLink; // request the stored deltaLink URL verbatim

                var sawMessages = false;
                string? nextDeltaLink = null;

                // Drain this tick's pages: follow @odata.nextLink to exhaustion, then keep the final
                // page's @odata.deltaLink as the cursor for the next tick.
                while (!string.IsNullOrEmpty(url))
                {
                    var resp = await _client.GetAsync<GraphDeltaResponse>(account, url, ct);
                    if (resp?.Value?.Length > 0)
                        sawMessages = true;

                    nextDeltaLink = resp?.DeltaLink ?? nextDeltaLink; // set only on the final page
                    url = resp?.NextLink;                             // null on the final page
                }

                if (sawMessages)
                    InboxNewMailDetected?.Invoke(account.Id);

                if (!string.IsNullOrEmpty(nextDeltaLink))
                    await _store.SetDeltaTokenAsync(account.Id, "Inbox", nextDeltaLink);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { LogService.Log($"GraphChangeNotifier {account.AccountLabel}", ex); }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        StopWatchers();
        GC.SuppressFinalize(this);
    }
}
