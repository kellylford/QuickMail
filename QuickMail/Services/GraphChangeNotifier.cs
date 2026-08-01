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
    public event Action<Guid, IReadOnlyList<string>>? InboxMessagesRemoved;
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
        // Single-lifecycle assumption: StartWatchers once at startup, StopWatchers once on exit.
        // Clearing _watchers and cancelling the CTS signals the poll tasks to exit, but does NOT
        // await them — they unwind when they next observe the cancelled token. That's fine for the
        // start-once/stop-once lifecycle; a caller that restarts watchers dynamically would see the
        // old tasks briefly race the new ones (harmless — they hold the old CTS and independent
        // accounts) but has no fence guaranteeing the old tasks have exited.
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

                var sawNewMail = false;
                var removedIds = new List<string>();
                string? nextDeltaLink = null;

                // Drain this tick's pages: follow @odata.nextLink to exhaustion, then keep the final
                // page's @odata.deltaLink as the cursor for the next tick.
                while (!string.IsNullOrEmpty(url))
                {
                    // Immutable ids on every delta request (initial + nextLink + deltaLink), so
                    // delta-delivered ids match the immutable ids the folder sync stores (#366 part B).
                    var resp = await _client.GetAsync<GraphDeltaResponse>(account, url, GraphHeaders.ImmutableId, ct);
                    // A delta page mixes two kinds of entry: added/changed messages (new mail) and
                    // @removed tombstones (deleted or moved out of the Inbox by another client, #366).
                    // An @removed entry carries only @removed + id, so route it to reconciliation, not
                    // to the new-mail signal. Ids match the store because the folder sync now stores the
                    // same immutable ids this request asks for (header above).
                    foreach (var m in resp?.Value ?? System.Array.Empty<GraphMessage>())
                    {
                        if (m.Removed != null)
                        {
                            if (!string.IsNullOrEmpty(m.Id)) removedIds.Add(m.Id);
                        }
                        else
                        {
                            sawNewMail = true;
                        }
                    }

                    nextDeltaLink = resp?.DeltaLink ?? nextDeltaLink; // set only on the final page
                    url = resp?.NextLink;                             // null on the final page
                }

                // At-least-once semantics, by design: the events fire BEFORE the cursor is persisted.
                // If the app exits in this window, the next startup re-polls from the old/absent cursor
                // and may re-notify for the same messages — but the resulting sync/removal is idempotent
                // (same ids aren't re-inserted; removing an already-gone id is a no-op), so nothing is
                // lost or duplicated. Persisting first would risk the opposite (a missed event), worse.
                if (sawNewMail)
                    InboxNewMailDetected?.Invoke(account.Id);
                if (removedIds.Count > 0)
                    InboxMessagesRemoved?.Invoke(account.Id, removedIds);

                if (!string.IsNullOrEmpty(nextDeltaLink))
                    await _store.SetDeltaTokenAsync(account.Id, "Inbox", nextDeltaLink);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { LogService.Log($"GraphChangeNotifier {account.AccountLabel}", ex); }

            // Re-read each tick so an edit to GraphPollSeconds in config.ini takes effect without an
            // app restart. _config.Load() is a file read, but once per interval (>=30 s) is negligible.
            var intervalSec = Math.Clamp(_config?.Load().GraphPollSeconds ?? 60, 30, 600);
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
