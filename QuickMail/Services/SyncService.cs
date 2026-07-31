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

    public SyncService(IMailService imap, ILocalStoreService store, IConfigService config, IRuleService rules,
        IUiDispatcher? ui = null)
    {
        _imap   = imap;
        _store  = store;
        _config = config;
        _rules  = rules;
        // WpfUiDispatcher marshals only when the real QuickMail App is present, and runs inline
        // otherwise — a plain Application.Current null-check is NOT enough (tests create a pumpless
        // Application, so InvokeAsync would park forever).
        _ui     = ui ?? new WpfUiDispatcher();
    }

    public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
    public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
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
        List<MailMessageSummary> fetched, bool persisted, CancellationToken ct)
    {
        if (fetched.Count == 0) return fetched;

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
            incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: true, ct);
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
            incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: false, ct);
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        }
        return incoming;
    }

    private async Task<List<MailMessageSummary>> SyncFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // ── New messages ─────────────────────────────────────────────────────────
        var maxKey   = await _store.GetMaxMessageKeyAsync(account.Id, folder.FullName);
        var cfg      = _config.Load();
        List<MailMessageSummary> incoming;

        if (maxKey == "0" && cfg.SyncDays > 0)
        {
            // Fresh start with a date filter: use SEARCH SINCE rather than count-based fallback.
            incoming = await _imap.GetMessagesSinceDateAsync(
                account.Id, folder.FullName, DateTime.UtcNow.AddDays(-cfg.SyncDays), ct);
        }
        else
        {
            incoming = await _imap.GetMessagesSinceAsync(
                account.Id, folder.FullName, maxKey, cfg.InitialSyncCount, ct);
        }

        if (incoming.Count > 0)
        {
            // Upsert + client rules run inside the shared chokepoint (the same path the live IDLE
            // syncs use). It strips rule-moved/deleted messages from the batch and raises
            // RulesApplied / MessagesRemoved; here we just surface the survivors to the UI —
            // immediately, without waiting for body preview fetches.
            incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: true, ct);
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        }

        // ── Remote deletions ─────────────────────────────────────────────────────
        // Only meaningful when we already have local data for this folder.
        var localIds = await _store.GetAllMessageIdsAsync(account.Id, folder.FullName);
        if (localIds.Count == 0) return incoming;

        var serverIds  = await _imap.GetFolderMessageIdsAsync(account.Id, folder.FullName, ct);
        var serverSet  = new HashSet<string>(serverIds);
        var deletedIds = localIds.Where(id => !serverSet.Contains(id)).ToList();

        if (deletedIds.Count == 0) return incoming;

        LogService.Log($"Sync {account.AccountLabel}/{folder.FullName}: {deletedIds.Count} remote deletion(s)");
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

        return incoming;
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
