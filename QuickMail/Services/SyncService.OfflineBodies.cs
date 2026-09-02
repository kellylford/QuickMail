using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// The offline-bodies pass (#637): when <see cref="ConfigModel.OfflineBodyDays"/> is set, sync
/// downloads the full bodies of recent Inbox messages that have no cached body yet, so they read
/// offline. Runs after the startup sync (behind the previews), after each periodic sweep, on each
/// IDLE Inbox arrival, and on demand when Settings widens the window. Attachments are not
/// included; POP3 already keeps whole messages.
/// </summary>
public partial class SyncService
{
    private readonly IConnectivityService? _connectivity;

    /// <summary>Upper bound per pass; the next sweep picks up where this one stopped (newest first).</summary>
    internal const int MaxBodiesPerPass = 500;

    private const int ProgressEvery = 10;

    /// <summary>
    /// A short gap between fetches: this is background caching, and a burst of hundreds of
    /// back-to-back fetches is what pushes Graph into 503 and an IMAP server into throttling.
    /// Settable so tests do not pay it.
    /// </summary>
    internal TimeSpan FetchPacing { get; set; } = TimeSpan.FromMilliseconds(100);

    // One pass at a time: the startup pass, a sweep, and a Settings-widen backfill would otherwise
    // plan overlapping id lists and fetch the same bodies twice. An interlocked flag rather than a
    // SemaphoreSlim, which would make SyncService own a disposable it has no lifetime hook to release.
    private int _bodiesPassRunning;

    public event Action<int, int>? OfflineBodyProgressChanged;
    public event Action<int, int>? OfflineBodyPassCompleted;

    public Task BackfillOfflineBodiesAsync(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        CancellationToken ct)
        => DownloadOfflineBodiesAsync(accounts.ToList(), cachedFolders, ct);

    private static bool EligibleForBodies(AccountModel account)
        // POP3 downloads whole messages already; a shared mailbox reads through its parent and gets
        // no background work of its own (#31).
        => account.BackendKind != BackendKind.Pop3Smtp && !account.IsShared;

    private async Task DownloadOfflineBodiesAsync(
        List<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        CancellationToken ct)
    {
        if (_probeMode) return;
        var days = _config.Load().EffectiveOfflineBodyDays;
        if (days <= 0) return;

        if (Interlocked.CompareExchange(ref _bodiesPassRunning, 1, 0) != 0)
        {
            LogService.Debug("Offline bodies: a pass is already running; skipping this one.");
            return;
        }
        try
        {
            await RunPassAsync(accounts, cachedFolders, DateTimeOffset.UtcNow.AddDays(-days), ct);
        }
        finally
        {
            Volatile.Write(ref _bodiesPassRunning, 0);
        }
    }

    private async Task RunPassAsync(
        List<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        DateTimeOffset since,
        CancellationToken ct)
    {
        // Plan first so the progress total is the whole pass, not one account at a time.
        var work = new List<(AccountModel Account, MailFolderModel Folder, List<string> Ids)>();
        foreach (var account in accounts.Where(EligibleForBodies))
        {
            if (_connectivity != null && !_connectivity.IsAccountOnline(account.Id)) continue;
            if (!cachedFolders.TryGetValue(account.Id, out var folders)) continue;
            foreach (var folder in folders.Where(f => f.Kind == SpecialFolderKind.Inbox))
            {
                ct.ThrowIfCancellationRequested();
                var ids = await _store.GetMessageIdsMissingDetailAsync(account.Id, folder.FullName, since, MaxBodiesPerPass);
                if (ids.Count > 0) work.Add((account, folder, ids));
            }
        }

        var total = work.Sum(w => w.Ids.Count);
        if (total == 0) return;
        ReportProgress(0, total);

        var done = 0;
        foreach (var (account, folder, ids) in work)
        {
            var timer = Stopwatch.StartNew();
            var before = done;
            // Intermediate progress never reaches the total: the pass reports its own end, once,
            // with the count it actually cached.
            var fetched = await DownloadBodiesForIdsAsync(account, folder, ids, ct,
                n => { if (before + n < total) ReportProgress(before + n, total); });
            done += fetched;
            LogService.Log($"Offline bodies {account.AccountLabel}/{folder.DisplayName}: {fetched} of {ids.Count} downloaded in {timer.ElapsedMilliseconds} ms");
        }
        _ui.Post(() => OfflineBodyPassCompleted?.Invoke(done, total));
    }

    private void ReportProgress(int done, int total)
        => _ui.Post(() => OfflineBodyProgressChanged?.Invoke(done, total));

    /// <summary>
    /// Fetches and caches each body in turn, skipping any that arrived in the cache meanwhile (an
    /// open, a prefetch, the arrival hook). A connection failure stops this account's batch — there
    /// is no point hammering a server that just went away — and tells the connectivity service;
    /// any other failure is logged and the next id is tried. Returns how many were cached.
    /// </summary>
    private async Task<int> DownloadBodiesForIdsAsync(
        AccountModel account, MailFolderModel folder, IReadOnlyList<string> ids,
        CancellationToken ct, Action<int>? progress = null)
    {
        var fetched = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await _store.LoadDetailAsync(account.Id, folder.FullName, id) != null)
                    continue;

                // Background lease, and no \Seen: this is caching, not reading.
                var detail = await _imap.PrefetchMessageDetailAsync(account.Id, folder.FullName, id, ct);
                await _store.UpsertDetailAsync(detail);
                fetched++;
                if (fetched % ProgressEvery == 0) progress?.Invoke(fetched);
                if (FetchPacing > TimeSpan.Zero)
                    await Task.Delay(FetchPacing, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ConnectionFailure.IsConnectionFailure(ex, ct))
            {
                _connectivity?.NoteAccountUnreachable(account.Id, "offline-bodies");
                LogService.Log($"Offline bodies {account.AccountLabel}: server unreachable, stopping this pass", ex);
                break;
            }
            catch (Exception ex)
            {
                LogService.Log($"Offline bodies {account.AccountLabel}/{folder.DisplayName} msgId={id}", ex);
            }
        }
        return fetched;
    }

    /// <summary>
    /// The arrival hook, on the IDLE path only: new Inbox mail inside the window gets its body
    /// cached right away, so the setting stays true between sweeps. The startup sync and the
    /// periodic sweep run the full pass themselves, so they do not hook — hooking there would queue
    /// the whole first window while the sync is still using the same background leases, and then
    /// fetch it all again in the pass. Fire-and-forget, a handful of ids, no progress events.
    /// </summary>
    private void QueueArrivalBodies(AccountModel account, MailFolderModel folder, List<MailMessageSummary> arrivals, CancellationToken ct)
    {
        if (_probeMode || arrivals.Count == 0) return;
        if (folder.Kind != SpecialFolderKind.Inbox || !EligibleForBodies(account)) return;
        var days = _config.Load().EffectiveOfflineBodyDays;
        if (days <= 0) return;

        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var ids = arrivals.Where(m => m.Date >= since).Select(m => m.MessageId).ToList();
        if (ids.Count == 0) return;

        Task.Run(() => DownloadBodiesForIdsAsync(account, folder, ids, ct), ct)
            .LogFaults("offline bodies for arrivals");
    }
}
