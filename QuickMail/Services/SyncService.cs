using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

public class SyncService : ISyncService
{
    private readonly IMailService _imap;
    private readonly ILocalStoreService _store;
    private readonly IConfigService _config;
    private readonly IRuleService _rules;
    private readonly IUiDispatcher _ui;

    // In --ui-probe mode the mail backend is a no-op stub that lists zero server messages, while the
    // store is seeded with fixture mail. Reconcile would read that empty listing as "everything was
    // deleted remotely" and purge the fixtures, emptying every visual-QA capture. Suppress reconcile
    // in probe mode — mirrors the !probeMode guard the one-time cache rebuild uses (#366).
    private readonly bool _probeMode;

    public SyncService(IMailService imap, ILocalStoreService store, IConfigService config, IRuleService rules,
        IUiDispatcher? ui = null, bool probeMode = false)
    {
        _imap   = imap;
        _store  = store;
        _config = config;
        _rules  = rules;
        _probeMode = probeMode;
        // WpfUiDispatcher marshals only when the real QuickMail App is present, and runs inline
        // otherwise — a plain Application.Current null-check is NOT enough (tests create a pumpless
        // Application, so InvokeAsync would park forever).
        _ui     = ui ?? new WpfUiDispatcher();
    }

    public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
    public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
    public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
    public event Action<int>? RulesApplied;
    public event Action<int, int>? SyncProgressChanged;

    private readonly Dictionary<Guid, DateTimeOffset> _lastSyncedUtc = new();

    // Store-less (online) dedupe only. Message keys (account, folder, id) already run through client
    // rules this session, so a re-fetched last-50 batch doesn't re-run a rule. Persisted paths use
    // the local store as the first-sight authority instead (this dictionary would grow unbounded and
    // is redundant there). Seeded as a baseline on the first online fetch per folder — see the
    // chokepoint — so rules never fire retroactively on the initial batch.
    private readonly ConcurrentDictionary<(Guid Account, string Folder, string Id), byte> _rulesApplied = new();

    // (account, folder) pairs whose store-less baseline has been established (first online fetch seen).
    private readonly ConcurrentDictionary<(Guid Account, string Folder), byte> _onlineBaselined = new();

    // Accounts whose cache was wiped by the one-time immutable-id rebuild (#366): the first persisted
    // sync of each of their folders is a baseline (mark-seen, no rules) so pre-existing mail — already
    // processed when it first arrived — isn't re-run through rules on upgrade day. Seeded via
    // SeedRebuildBaseline; _rebuildBaselined tracks which (account, folder) pairs have been baselined.
    private readonly ConcurrentDictionary<Guid, byte> _rebuildAccounts = new();
    private readonly ConcurrentDictionary<(Guid Account, string Folder), byte> _rebuildBaselined = new();

    /// <summary>
    /// Marks the given accounts as freshly cache-wiped by the one-time immutable-id rebuild (#366).
    /// The first persisted sync of each of their folders then caches the fetched messages but does NOT
    /// run client rules on them — they are pre-existing mail (rules already ran when it first arrived),
    /// not new arrivals, and the wipe erased the store's "already seen" memory. Rules resume on
    /// genuinely-new mail from the next sync. No-op for accounts not passed here. Call once at startup,
    /// right after the rebuild clears the cache and before any sync runs.
    /// </summary>
    public void SeedRebuildBaseline(IEnumerable<Guid> accountIds)
    {
        foreach (var id in accountIds) _rebuildAccounts.TryAdd(id, 0);
    }

    public async Task SyncAllAccountsAsync(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        CancellationToken ct)
    {
        var previewJobs = new ConcurrentBag<(AccountModel Account, MailFolderModel Folder, List<MailMessageSummary> Incoming)>();
        var accountList = accounts.ToList();

        int totalFolders = accountList.Sum(a =>
            cachedFolders.TryGetValue(a.Id, out var fl) ? fl.Count(f => !f.ExcludeFromAllMail) : 0);

        // int[] so Interlocked.Increment works inside async lambdas (can't use ref locals there).
        int[] completedFolders = { 0 };

        // Group accounts by IMAP host. Accounts on the same server sync sequentially within
        // their group to avoid hitting per-IP connection limits (which trigger "Server shutting
        // down" BYEs on shared hosting). Groups on different servers still run in parallel.
        var accountsByHost = accountList
            .GroupBy(a => a.ImapHost, StringComparer.OrdinalIgnoreCase)
            .ToList();

        async Task SyncPassAsync(Func<MailFolderModel, bool> folderFilter)
        {
            await Task.WhenAll(accountsByHost.Select(async hostGroup =>
            {
                foreach (var account in hostGroup)
                {
                    if (!cachedFolders.TryGetValue(account.Id, out var folders)) continue;
                    foreach (var folder in folders)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (folder.ExcludeFromAllMail || !folderFilter(folder)) continue;
                        try
                        {
                            var incoming = await SyncFolderAsync(account, folder, ct);
                            var previewLines = _config.Load().GetPreviewLines(account.Id);
                            if (incoming.Count > 0 && previewLines > 0
                                && incoming.Any(s => string.IsNullOrEmpty(s.Preview)))
                                previewJobs.Add((account, folder, incoming));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Log($"Sync {account.AccountLabel}/{folder.DisplayName}", ex);
                        }

                        var count = Interlocked.Increment(ref completedFolders[0]);
                        _ui.Post(() => SyncProgressChanged?.Invoke(count, totalFolders));
                    }
                }
            }));
        }

        // NOOP: one per host group in parallel, sequential within each group.
        await Task.WhenAll(accountsByHost.Select(async hostGroup =>
        {
            foreach (var account in hostGroup)
            {
                try { await _imap.NoOpAsync(account.Id, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { LogService.Log($"NoOp {account.AccountLabel}", ex); }
            }
        }));

        // Pass 1: Inbox folders first, all accounts in parallel — fastest path to new-mail visibility.
        await SyncPassAsync(f => f.Kind == SpecialFolderKind.Inbox);
        ct.ThrowIfCancellationRequested();
        // Pass 2: All remaining non-excluded folders, all accounts in parallel.
        await SyncPassAsync(f => f.Kind != SpecialFolderKind.Inbox);

        foreach (var account in accountList)
            _lastSyncedUtc[account.Id] = DateTimeOffset.UtcNow;

        // Fetch previews only after ALL folder syncs complete so preview IMAP calls
        // don't race with the sync IMAP calls on the same shared client.
        // They run sequentially — fire-and-forget the whole batch so SyncAllAccounts
        // returns promptly and the status bar updates, while previews trickle in.
        if (!previewJobs.IsEmpty)
            FetchAllPreviewsAsync(previewJobs.ToList(), ct)
                .LogFaults("sync: preview fetch batch");
    }

    private async Task FetchAllPreviewsAsync(
        List<(AccountModel Account, MailFolderModel Folder, List<MailMessageSummary> Incoming)> jobs,
        CancellationToken ct)
    {
        foreach (var (account, folder, incoming) in jobs)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await FetchAndApplyPreviewsAsync(account, folder, incoming, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One folder's preview failure must not kill the rest of the batch.
                LogService.Log($"Preview fetch failed for {account.AccountLabel}/{folder.DisplayName}", ex);
            }
        }
    }

    /// <summary>
    /// The single point where client mail rules run against freshly fetched messages. Every sync
    /// path — the initial/periodic full sync and the live IDLE/change-notifier syncs — funnels its
    /// batch through here, so a rule fires exactly once per message no matter which path first sees
    /// it. (Previously only the full sync applied rules, so mail arriving while the app was running
    /// slipped past every client rule.)
    ///
    /// Determines genuinely-new arrivals — persisted paths dedupe against the local store; the
    /// store-less online path uses an in-session guard, and treats its first fetch per folder as a
    /// baseline (marked seen, never run) so rules can't fire retroactively on the reconciliation
    /// batch. Persists the batch, runs rules on the new arrivals, deletes any rule-moved/deleted
    /// messages from the store, raises <see cref="RulesApplied"/> / <see cref="MessagesRemoved"/>,
    /// and returns the batch with those messages stripped so the UI never shows them in the origin
    /// folder. Callers own <see cref="FolderSynced"/>.
    /// </summary>
    private async Task<List<MailMessageSummary>> ApplyRulesToArrivalsAsync(
        AccountModel account, MailFolderModel folder,
        List<MailMessageSummary> fetched, bool persisted, bool consumeRebuildBaseline, CancellationToken ct)
    {
        if (fetched.Count == 0)
        {
            // F4: an empty folder at upgrade has no pre-existing mail to baseline, but still consume the
            // baseline on the full sync so a later genuinely-new message runs rules normally rather than
            // being swallowed by a baseline deferred to it.
            if (consumeRebuildBaseline && _rebuildAccounts.ContainsKey(account.Id))
                _rebuildBaselined.TryAdd((account.Id, folder.FullName), 0);
            return fetched;
        }

        // No enabled rules → no id scan, no guard bookkeeping; a rule-less profile pays nothing.
        // (LoadRules() is cached after first load.) Still persist so the cache/UI reflect the fetch.
        var hasEnabledRules = _rules.LoadRules().Any(r => r.IsEnabled);

        // Persisted dedupe authority is the store — snapshot which fetched ids it already holds,
        // BEFORE the upsert, so freshly-fetched messages still read as new. A bounded IN over the
        // batch avoids scanning every id in a large cached folder; the full scan is only cheaper for
        // an unusually large batch (a fresh account's first sync, where the store is empty anyway).
        var knownInStore = !(persisted && hasEnabledRules) ? []
            : fetched.Count <= 500
                ? await _store.GetExistingMessageIdsAsync(account.Id, folder.FullName, fetched.Select(m => m.MessageId))
                : await _store.GetAllMessageIdsAsync(account.Id, folder.FullName);

        if (persisted)
            await _store.UpsertSummariesAsync(fetched);

        if (!hasEnabledRules) return fetched;

        // #336: client rules fire ONLY on the Inbox. Non-Inbox folders are still fetched and cached
        // above — we just don't run rules against them. This is the classic mail-rules model (rules
        // process mail as it arrives in the Inbox) and it prevents double-processing: a server-side
        // rule (or a manual move) that files a message into another folder must not then be re-acted
        // on by a matching client rule when QuickMail syncs that folder, and a rule must never yank
        // back mail the user manually filed elsewhere. Matches the Inbox test used across the VM.
        //
        // IMPORTANT (review L5): for Graph accounts, folder.FullName is an opaque id that never equals
        // "INBOX", so folder.Kind == Inbox is the ONLY thing keeping client rules alive on a Graph
        // inbox. Every current caller resolves the inbox model from _cachedFolders (where Kind is set),
        // so this holds — but any new sync entry point that hands this method a Graph inbox with
        // Kind == None would silently stop running rules on it. Keep Kind set on the inbox model, or
        // route inbox resolution through the shared predicate. Pinned by GraphInbox_ByKind_RunsRules.
        if (folder.Kind != SpecialFolderKind.Inbox &&
            !string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase))
            return fetched;

        // Store-less (online) baseline: the first fetch per folder is the last-50 reconciliation
        // batch, not new mail. Mark it seen WITHOUT running rules, so a move/delete/mark-read rule
        // never rewrites up-to-50 pre-existing messages on a delete or archive reconciliation.
        // Retroactive application has its own user-invoked home in ApplyRulesToExistingAsync.
        if (!persisted && _onlineBaselined.TryAdd((account.Id, folder.FullName), 0))
        {
            foreach (var m in fetched)
                _rulesApplied.TryAdd((account.Id, folder.FullName, m.MessageId), 0);
            return fetched;
        }

        // Persisted rebuild baseline (#366/N5): after the one-time immutable-id cache wipe, the store is
        // empty, so a re-fetch reads every pre-existing message as "new" and would re-run rules over old
        // mail on upgrade day (move/delete/mark-read). While a wiped account's folder is not yet
        // baselined, skip rules on its re-fetched mail.
        //
        // F2 (race): the delta poll's IDLE last-50 fetch runs concurrently with the full sync's larger
        // window on upgrade launches, and nothing serializes them. Only the FULL sync consumes (marks
        // the folder baselined); the IDLE path skips rules WITHOUT consuming. So if IDLE wins the race it
        // can't burn the baseline on 50 messages and leave the full sync's larger remainder — read as new
        // against the just-upserted 50 — to re-fire. The full sync always finds the folder un-baselined
        // and skips its whole batch. Once the full sync consumes, rules resume normally on both paths.
        if (persisted && _rebuildAccounts.ContainsKey(account.Id)
            && !_rebuildBaselined.ContainsKey((account.Id, folder.FullName)))
        {
            if (consumeRebuildBaseline)
                _rebuildBaselined.TryAdd((account.Id, folder.FullName), 0);
            return fetched;
        }

        var newArrivals = new List<MailMessageSummary>();
        foreach (var m in fetched)
        {
            var isNew = persisted
                ? !knownInStore.Contains(m.MessageId)
                : _rulesApplied.TryAdd((account.Id, folder.FullName, m.MessageId), 0);
            if (isNew) newArrivals.Add(m);
        }

        if (newArrivals.Count == 0) return fetched;

        int matchedCount = 0;
        List<MailMessageSummary> removedMessages = [];
        try
        {
            LogService.Debug($"ApplyRules: {account.AccountLabel}/{folder.FullName} — {newArrivals.Count} new of {fetched.Count} fetched");
            (matchedCount, removedMessages) = await _rules.ApplyRulesAsync(newArrivals, account.Id, ct);
            LogService.Debug($"ApplyRules: done — {matchedCount} matched, {removedMessages.Count} removed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Log($"Applying rules for {account.AccountLabel}/{folder.FullName} failed", ex);
            // Un-mark the store-less guard so the next poll retries instead of skipping these
            // forever. (The persisted path is store-guarded; the upsert already recorded them, and
            // rolling the store back on a transient rule failure would be worse than a retry.)
            if (!persisted)
                foreach (var m in newArrivals)
                    _rulesApplied.TryRemove((account.Id, folder.FullName, m.MessageId), out _);
            return fetched;
        }

        // Delete rule-moved/deleted messages from the store so they don't reappear on cache load.
        if (persisted && removedMessages.Count > 0)
        {
            foreach (var group in removedMessages.GroupBy(m => (m.AccountId, m.FolderName)))
            {
                try
                {
                    await _store.DeleteSummariesAsync(
                        group.Key.AccountId, group.Key.FolderName, group.Select(m => m.MessageId));
                }
                catch (Exception ex)
                {
                    LogService.Log($"Rule cleanup: failed to delete {group.Count()} summaries from {group.Key.FolderName}", ex);
                }
            }
        }

        // Strip moved/deleted from the batch so the UI doesn't show them in the origin folder.
        if (removedMessages.Count > 0)
        {
            var removedKeys = removedMessages
                .Select(m => (m.MessageId, m.AccountId, m.FolderName)).ToHashSet();
            fetched.RemoveAll(m => removedKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));
        }

        if (matchedCount > 0 || removedMessages.Count > 0)
        {
            _ui.Post(() =>
            {
                if (matchedCount > 0) RulesApplied?.Invoke(matchedCount);
                if (removedMessages.Count > 0) MessagesRemoved?.Invoke(removedMessages);
            });
        }

        return fetched;
    }

    public async Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // IDLE-triggered sync in non-online (SQLite cache) mode.
        //
        // We intentionally mirror SyncOneFolderOnlineAsync rather than calling
        // SyncFolderAsync here.  SyncFolderAsync queries the max message key from the store
        // and fetches only messages *after* that key — but by the time IDLE fires,
        // RefreshFolderFromServerAsync has usually already stored the new messages and
        // advanced the max key.  That causes SyncFolderAsync to see incoming.Count == 0,
        // skip FolderSynced, and produce no announcement.
        //
        // Fetching the last 50 by count (sinceMessageId: "0") guarantees FolderSynced fires
        // whenever the server has messages.  OnFolderSynced deduplicates by message id so
        // already-visible messages are discarded; only genuinely new arrivals are inserted.
        LogService.Log($"IDLE targeted sync: fetching {account.AccountLabel}/{folder.FullName}");
        var incoming = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, sinceMessageId: "0", initialCount: 50, ct);
        LogService.Log($"IDLE targeted sync: {incoming.Count} messages fetched from {account.AccountLabel}/{folder.FullName}");
        if (incoming.Count > 0)
        {
            // Upsert + client rules happen inside the shared chokepoint so live-arriving mail is
            // subject to rules exactly like the full sync.
            incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: true, consumeRebuildBaseline: false, ct);
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        }
        return incoming;
    }

    public async Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // Fetch the last 50 messages. OnFolderSynced deduplicates by UID so already-visible
        // messages are harmlessly skipped; only truly new arrivals are inserted.
        LogService.Log($"IDLE targeted sync: fetching {account.AccountLabel}/{folder.FullName}");
        var incoming = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, sinceMessageId: "0", initialCount: 50, ct);
        LogService.Log($"IDLE targeted sync: {incoming.Count} messages fetched from {account.AccountLabel}/{folder.FullName}");
        if (incoming.Count > 0)
        {
            // Online mode keeps no local store, so rules dedupe via the in-session guard only.
            incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: false, consumeRebuildBaseline: false, ct);
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        }
        return incoming;
    }

    /// <summary>
    /// Full sync of a single folder: fetches messages newer than the local high-water mark (raising
    /// <see cref="FolderSynced"/> so the current view merges them in) and then reconciles remote
    /// deletions (raising <see cref="MessagesRemoved"/>). This is the same work the startup full sync
    /// does per folder, exposed for the periodic all-folder sweep — non-Inbox folders have no live
    /// watcher (Graph delta and IMAP IDLE cover only the Inbox), so mail a server-side rule files into
    /// a custom folder is otherwise invisible until the folder is opened or the app restarts (#366).
    /// Returns the genuinely-new arrivals (empty when none).
    /// </summary>
    public async Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
        => await SyncFolderAsync(account, folder, ct);

    private async Task<List<MailMessageSummary>> SyncFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // ── New messages ─────────────────────────────────────────────────────────
        var maxKey   = await _store.GetMaxMessageKeyAsync(account.Id, folder.FullName);
        var cfg      = _config.Load();

        // maxKey is "0" for a genuinely-fresh folder AND for every Graph folder (Graph message ids are
        // non-numeric, so GetMaxMessageKeyAsync's CAST-to-integer high-water mark is always 0). Without
        // a guard, a fully-cached Graph folder re-fetched its whole SyncDays window on EVERY periodic
        // sweep — ~all its recent mail, every cycle (#462). The id-diff path below fetches only when the
        // server actually holds something new. (IMAP folders with mail have a real numeric maxKey and
        // take the incremental branch below, so this changes nothing for them.)
        if (maxKey == "0" && cfg.SyncDays > 0)
            return await SyncFolderByIdDiffAsync(account, folder, cfg, ct);

        var incoming = await _imap.GetMessagesSinceAsync(
            account.Id, folder.FullName, maxKey, cfg.InitialSyncCount, ct);

        if (incoming.Count > 0)
        {
            // Upsert + client rules run inside the shared chokepoint (the same path the live IDLE
            // syncs use). It strips rule-moved/deleted messages from the batch and raises
            // RulesApplied / MessagesRemoved; here we just surface the survivors to the UI —
            // immediately, without waiting for body preview fetches.
            incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: true, consumeRebuildBaseline: true, ct);
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        }

        // ── Remote deletions ─────────────────────────────────────────────────────
        await ReconcileFolderAsync(account, folder, ct);

        return incoming;
    }

    /// <summary>
    /// Sync path for a folder whose numeric high-water mark is always "0" — every Graph folder (Graph
    /// ids are non-numeric) and any genuinely-fresh folder. Instead of re-fetching the whole SyncDays
    /// window on every periodic sweep (#462), it lists the server ids WITH their received dates ONCE and
    /// gates on that: it fetches the window only when the server holds a <em>within-window</em> id the
    /// cache lacks, so an unchanged folder costs one id listing and no message fetch.
    ///
    /// Why window-scoped and not a plain id diff: the local cache only ever holds mail inside the
    /// SyncDays window (the fetch is date-filtered), while the server lists mail of every age. A naïve
    /// "server has an id the cache lacks" test is therefore true forever on any folder containing mail
    /// older than the window, and would re-fetch every cycle — defeating the fix. Filtering the server
    /// ids to the window before diffing compares like with like.
    ///
    /// Why the fetch pulls the WHOLE window (not "newest-cached-date forward"): that still surfaces mail
    /// filed into the folder with an <em>older</em> receivedDateTime than the newest we already hold — a
    /// server-side rule batch-filing older mail, or old mail moved in from another client — as long as it
    /// falls inside the window. A date-forward filter would silently miss it. The single date-bearing
    /// listing also drives the deletion reconcile (cached ids missing from the FULL set), so there is no
    /// second round-trip.
    /// </summary>
    private async Task<List<MailMessageSummary>> SyncFolderByIdDiffAsync(
        AccountModel account, MailFolderModel folder, ConfigModel cfg, CancellationToken ct)
    {
        var windowStart = DateTime.UtcNow.AddDays(-cfg.SyncDays);

        // Probe mode: the fixture stub lists no server ids, so an id-diff would skip the seed fetch and
        // (via reconcile) delete the seeded fixture mail. Fetch the window and skip reconcile, exactly
        // as this branch did before #462.
        if (_probeMode)
        {
            var seeded = await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, windowStart, ct);
            return await SurfaceArrivalsAsync(account, folder, seeded, ct);
        }

        // id → is_read for everything cached in this folder. The key set is the folder's cached-id set
        // (drives the addition/deletion diff), and the values let us reconcile read/unread changed by
        // another client — so this one query replaces a separate GetAllMessageIdsAsync here.
        var cacheReadStates = await _store.LoadFolderReadStatesAsync(account.Id, folder.FullName);

        // Fresh/empty cache: fetch the full initial window and skip the id listing entirely — there is
        // nothing to diff against and nothing to reconcile (both no-op on an empty cache), so the listing
        // would be a wasted round-trip on each folder's first sync.
        if (cacheReadStates.Count == 0)
        {
            var initial = await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, windowStart, ct);
            return await SurfaceArrivalsAsync(account, folder, initial, ct);
        }

        var serverIdDates = await _imap.GetFolderMessageIdDatesAsync(account.Id, folder.FullName, ct);

        // Fetch only when the server lists a WITHIN-WINDOW id we don't yet hold — old mail the cache never
        // captured (older than the window) is not a reason to fetch.
        var hasNew = serverIdDates.Any(m => m.ReceivedUtc >= windowStart && !cacheReadStates.ContainsKey(m.Id));

        var fetched = hasNew
            ? await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, windowStart, ct)
            : new List<MailMessageSummary>();

        // Always run the (possibly empty) batch through the chokepoint. An empty batch is a no-op except
        // that it consumes a pending #366 rebuild baseline (F4) — preserving the pre-#462 behavior where
        // every sweep passed through ApplyRulesToArrivalsAsync.
        var incoming = await SurfaceArrivalsAsync(account, folder, fetched, ct);

        // ── Read/unread reconcile ── the old full-window re-fetch refreshed read state from the server as
        // a side effect (UpsertSummariesAsync carries is_read = excluded.is_read); with the fetch now
        // skipped, do it explicitly from the same listing. A cached message the server now reports with a
        // different read state — read (or unread) elsewhere, e.g. Outlook on the phone — gets its cache
        // row updated and the change surfaced. Messages just fetched above already carry current read
        // state, so exclude them.
        var fetchedIds = fetched.Count == 0 ? null : new HashSet<string>(fetched.Select(m => m.MessageId));
        await ReconcileReadStatesAsync(account, folder, serverIdDates, cacheReadStates, fetchedIds);

        // ── Remote deletions ── the FULL server id set (any age) vs the cache; reuse the listing we
        // already have (no second server round-trip).
        var localIds = new HashSet<string>(cacheReadStates.Keys);
        var serverIds = serverIdDates.Select(m => m.Id).ToList();
        await ReconcileDeletionsAsync(account, folder, localIds, serverIds);

        return incoming;
    }

    /// <summary>
    /// Updates the cache and the UI for messages whose read/unread state changed on the server since we
    /// last saw them (#462). Diffs the server's read state (from the id listing) against the cached
    /// state; for the rows that differ it updates only <c>is_read</c> in the store (never touching other
    /// columns) and raises <see cref="FolderReadStatesReconciled"/> with minimal summaries so the view
    /// can refresh the matching rows and folder counts. Deliberately NOT routed through FolderSynced: a
    /// read change must not fire a new-mail toast or reconcile flag state.
    /// </summary>
    private async Task ReconcileReadStatesAsync(
        AccountModel account, MailFolderModel folder,
        IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)> serverIdDates,
        Dictionary<string, bool> cacheReadStates,
        HashSet<string>? justFetchedIds)
    {
        var toRead   = new List<(Guid, string, string)>();
        var toUnread = new List<(Guid, string, string)>();
        var changed  = new List<MailMessageSummary>();

        foreach (var m in serverIdDates)
        {
            if (justFetchedIds != null && justFetchedIds.Contains(m.Id)) continue; // already current from the fetch
            if (!cacheReadStates.TryGetValue(m.Id, out var cachedRead)) continue;  // not cached (or a new arrival)
            if (cachedRead == m.IsRead) continue;

            (m.IsRead ? toRead : toUnread).Add((account.Id, folder.FullName, m.Id));
            changed.Add(new MailMessageSummary
            {
                MessageId  = m.Id,
                AccountId  = account.Id,
                FolderName = folder.FullName,
                IsRead     = m.IsRead,
            });
        }

        if (changed.Count == 0) return;

        // is_read-only updates — never an upsert, which would blank the row's other columns.
        if (toRead.Count   > 0) await _store.UpdateIsReadBatchAsync(toRead,   isRead: true);
        if (toUnread.Count > 0) await _store.UpdateIsReadBatchAsync(toUnread, isRead: false);

        LogService.Log($"Read-state reconcile {account.AccountLabel}/{folder.FullName}: {changed.Count} changed");
        _ui.Post(() => FolderReadStatesReconciled?.Invoke(changed));
    }

    /// <summary>
    /// Runs a fetched batch through the shared rules/upsert chokepoint and raises
    /// <see cref="FolderSynced"/> for the survivors. An empty batch is a cheap no-op (aside from
    /// consuming a pending rebuild baseline).
    /// </summary>
    private async Task<List<MailMessageSummary>> SurfaceArrivalsAsync(
        AccountModel account, MailFolderModel folder, List<MailMessageSummary> fetched, CancellationToken ct)
    {
        var incoming = await ApplyRulesToArrivalsAsync(account, folder, fetched, persisted: true, consumeRebuildBaseline: true, ct);
        if (incoming.Count > 0)
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        return incoming;
    }

    /// <summary>
    /// Reconciles a single folder's local cache against the server: any message id we hold locally
    /// but the server no longer lists (deleted or moved away by another client — Outlook web/desktop/
    /// mobile, a server-side rule, or Exchange Online rebalancing) is removed from the store and
    /// raised via <see cref="MessagesRemoved"/> so the UI drops the ghost row.
    ///
    /// Backend-agnostic — <see cref="IMailService.GetFolderMessageIdsAsync"/> routes to IMAP or Graph
    /// (Graph reads with immutable ids, #366). Add-only sync paths (live IDLE, Graph delta poll, the
    /// periodic fallback) do NOT reconcile, so this is the piece that catches deletions made elsewhere
    /// while the app is running. Cheap: one id-only listing plus a set difference. No-ops (returns 0)
    /// when the folder has no local data yet. Returns the number of ghosts removed.
    /// </summary>
    public async Task<int> ReconcileFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // Never reconcile against the probe stub: its empty server listing would delete the seeded
        // fixture mail and blank the visual-QA captures (see _probeMode).
        if (_probeMode) return 0;

        // Only meaningful when we already have local data for this folder.
        var localIds = await _store.GetAllMessageIdsAsync(account.Id, folder.FullName);
        if (localIds.Count == 0) return 0;

        var serverIds  = await _imap.GetFolderMessageIdsAsync(account.Id, folder.FullName, ct);
        return await ReconcileDeletionsAsync(account, folder, localIds, serverIds);
    }

    /// <summary>
    /// Deletion half of the reconcile, given id sets that have already been listed — removes cached ids
    /// the server no longer lists and raises <see cref="MessagesRemoved"/>. Split out of
    /// <see cref="ReconcileFolderAsync"/> so the Graph sweep path (<see cref="SyncFolderByIdDiffAsync"/>)
    /// can drive both addition detection and deletion from a single server-id listing (#462). Callers own
    /// the _probeMode guard and the empty-cache no-op.
    /// </summary>
    private async Task<int> ReconcileDeletionsAsync(
        AccountModel account, MailFolderModel folder, HashSet<string> localIds, IList<string> serverIds)
    {
        var serverSet  = new HashSet<string>(serverIds);
        var deletedIds = localIds.Where(id => !serverSet.Contains(id)).ToList();

        if (deletedIds.Count == 0) return 0;

        LogService.Log($"Reconcile {account.AccountLabel}/{folder.FullName}: {deletedIds.Count} remote deletion(s)");
        await _store.DeleteSummariesAsync(account.Id, folder.FullName, deletedIds);

        var removed = deletedIds
            .Select(id => new MailMessageSummary
            {
                MessageId  = id,
                AccountId  = account.Id,
                FolderName = folder.FullName,
            })
            .ToList();

        _ui.Post(() => MessagesRemoved?.Invoke(removed));

        return deletedIds.Count;
    }

    private async Task FetchAndApplyPreviewsAsync(
        AccountModel account, MailFolderModel folder,
        List<MailMessageSummary> incoming, CancellationToken ct)
    {
        try
        {
            // Only fetch bodies for messages the server didn't fill via IMAP PREVIEW.
            var ids = incoming
                .Where(s => string.IsNullOrEmpty(s.Preview))
                .OrderByDescending(s => s.Date)
                .Take(100)
                .Select(s => s.MessageId)
                .ToList();
            if (ids.Count == 0) return;

            var previewLines = _config.Load().GetPreviewLines(account.Id);
            if (previewLines <= 0) return;
            var previews = await _imap.FetchPreviewsAsync(
                account.Id, folder.FullName, ids, previewLines, ct);

            // Match each summary in 'incoming' to its preview, building both the
            // UI-apply list and the persistence list in one pass.
            var updates = new List<(string MessageId, string Preview)>(previews.Count);
            var uiApply = new List<(MailMessageSummary Summary, string Preview)>(previews.Count);
            foreach (var s in incoming)
            {
                if (!previews.TryGetValue(s.MessageId, out var p)) continue;
                uiApply.Add((s, p));
                updates.Add((s.MessageId, p));
            }
            if (uiApply.Count == 0) return;

            // One dispatcher hop for the whole batch instead of N — N dispatcher
            // invocations during a fast sync flood the UI thread with continuations.
            _ui.Post(() =>
            {
                foreach (var (s, p) in uiApply) s.Preview = p;
            });

            // One transaction for the whole batch instead of N opens/commits.
            await _store.UpdatePreviewsBatchAsync(account.Id, folder.FullName, updates);
        }
        catch (OperationCanceledException) { /* sync cancelled — normal */ }
        catch (Exception ex)
        {
            LogService.Log($"FetchAndApplyPreviews {account.AccountLabel}/{folder.FullName}", ex);
        }
    }

    public DateTimeOffset? LastSyncedUtc(Guid accountId) =>
        _lastSyncedUtc.TryGetValue(accountId, out var t) ? t : null;
}
