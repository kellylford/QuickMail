using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickMail.Models;

namespace QuickMail.Services;

public class SyncService : ISyncService
{
    private readonly IMailService _imap;
    private readonly ILocalStoreService _store;
    private readonly IConfigService _config;
    private readonly IRuleService _rules;

    public SyncService(IMailService imap, ILocalStoreService store, IConfigService config, IRuleService rules)
    {
        _imap   = imap;
        _store  = store;
        _config = config;
        _rules  = rules;
    }

    public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
    public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
    public event Action<int>? RulesApplied;

    public async Task SyncAllAccountsAsync(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        CancellationToken ct)
    {
        var previewJobs = new List<(AccountModel Account, MailFolderModel Folder, List<MailMessageSummary> Incoming)>();

        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            if (!cachedFolders.TryGetValue(account.Id, out var folders)) continue;

            // Send NOOP to keep the connection alive and flush any server-side
            // BYE before we start syncing. NoOpAsync discards the stale client
            // internally so GetOrReconnectAsync will create a fresh one when needed.
            try { await _imap.NoOpAsync(account.Id, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LogService.Log($"NoOp {account.AccountLabel}", ex); }

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                if (folder.ExcludeFromAllMail) continue;

                try
                {
                    var incoming = await SyncFolderAsync(account, folder, ct);
                    // Only queue body-download fallback for messages the server didn't
                    // already fill via the IMAP PREVIEW extension.
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
            }
        }

        // Fetch previews only after ALL folder syncs complete so preview IMAP calls
        // don't race with the sync IMAP calls on the same shared client.
        // They run sequentially — fire-and-forget the whole batch so SyncAllAccounts
        // returns promptly and the status bar updates, while previews trickle in.
        if (previewJobs.Count > 0)
            _ = FetchAllPreviewsAsync(previewJobs, ct);
    }

    private async Task FetchAllPreviewsAsync(
        List<(AccountModel Account, MailFolderModel Folder, List<MailMessageSummary> Incoming)> jobs,
        CancellationToken ct)
    {
        foreach (var (account, folder, incoming) in jobs)
        {
            if (ct.IsCancellationRequested) return;
            await FetchAndApplyPreviewsAsync(account, folder, incoming, ct);
        }
    }

    public async Task SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // IDLE-triggered sync in non-online (SQLite cache) mode.
        //
        // We intentionally mirror SyncOneFolderOnlineAsync rather than calling
        // SyncFolderAsync here.  SyncFolderAsync queries maxUid from the store and
        // fetches only messages *after* that UID — but by the time IDLE fires,
        // RefreshFolderFromServerAsync has usually already stored the new messages and
        // advanced maxUid.  That causes SyncFolderAsync to see incoming.Count == 0,
        // skip FolderSynced, and produce no announcement.
        //
        // Fetching the last 50 by count (sinceUid: 0) guarantees FolderSynced fires
        // whenever the server has messages.  OnFolderSynced deduplicates by UID so
        // already-visible messages are discarded; only genuinely new arrivals are inserted.
        LogService.Log($"IDLE targeted sync: fetching {account.AccountLabel}/{folder.FullName}");
        var incoming = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, sinceUid: 0, initialCount: 50, ct);
        LogService.Log($"IDLE targeted sync: {incoming.Count} messages fetched from {account.AccountLabel}/{folder.FullName}");
        if (incoming.Count > 0)
        {
            await _store.UpsertSummariesAsync(incoming);
            await Application.Current.Dispatcher.InvokeAsync(() => FolderSynced?.Invoke(incoming));
        }
    }

    public async Task SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // Fetch the last 50 messages. OnFolderSynced deduplicates by UID so already-visible
        // messages are harmlessly skipped; only truly new arrivals are inserted.
        LogService.Log($"IDLE targeted sync: fetching {account.AccountLabel}/{folder.FullName}");
        var incoming = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, sinceUid: 0, initialCount: 50, ct);
        LogService.Log($"IDLE targeted sync: {incoming.Count} messages fetched from {account.AccountLabel}/{folder.FullName}");
        if (incoming.Count > 0)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => FolderSynced?.Invoke(incoming));
        }
    }

    private async Task<List<MailMessageSummary>> SyncFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // ── New messages ─────────────────────────────────────────────────────────
        var maxUid   = await _store.GetMaxUidAsync(account.Id, folder.FullName);
        var cfg      = _config.Load();
        List<MailMessageSummary> incoming;

        if (maxUid == 0 && cfg.SyncDays > 0)
        {
            // Fresh start with a date filter: use SEARCH SINCE rather than count-based fallback.
            incoming = await _imap.GetMessagesSinceDateAsync(
                account.Id, folder.FullName, DateTime.UtcNow.AddDays(-cfg.SyncDays), ct);
        }
        else
        {
            incoming = await _imap.GetMessagesSinceAsync(
                account.Id, folder.FullName, maxUid, cfg.InitialSyncCount, ct);
        }

        if (incoming.Count > 0)
        {
            await _store.UpsertSummariesAsync(incoming);

            // Apply mail rules before notifying the UI so the user sees
            // the post-rule state (moved, flagged, etc.) immediately.
            int matchedCount = 0;
            List<MailMessageSummary> removedMessages = [];
            try
            {
                LogService.Debug($"SyncFolderAsync: applying rules for {account.AccountLabel}/{folder.FullName}, {incoming.Count} incoming");
                (matchedCount, removedMessages) = await _rules.ApplyRulesAsync(incoming, account.Id, ct);
                LogService.Debug($"SyncFolderAsync: rules done — {matchedCount} matched, {removedMessages.Count} removed, {incoming.Count} remaining in incoming");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Rules execution failed for {account.AccountLabel}", ex);
            }

            // Delete rule-moved/deleted messages from the local store so they
            // don't reappear on the next cache load.
            if (removedMessages.Count > 0)
            {
                var byFolder = removedMessages.GroupBy(m => (m.AccountId, m.FolderName));
                foreach (var group in byFolder)
                {
                    try
                    {
                        await _store.DeleteSummariesAsync(
                            group.Key.AccountId, group.Key.FolderName,
                            group.Select(m => m.UniqueId));
                    }
                    catch (Exception ex)
                    {
                        LogService.Log($"Rule cleanup: failed to delete {group.Count()} summaries from {group.Key.FolderName}", ex);
                    }
                }
            }

            // Show messages immediately — don't wait for body preview fetches.
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                FolderSynced?.Invoke(incoming);
                if (matchedCount > 0)
                    RulesApplied?.Invoke(matchedCount);
                if (removedMessages.Count > 0)
                    MessagesRemoved?.Invoke(removedMessages);
            });
        }

        // ── Remote deletions ─────────────────────────────────────────────────────
        // Only meaningful when we already have local data for this folder.
        var localUids = await _store.GetAllUidsAsync(account.Id, folder.FullName);
        if (localUids.Count == 0) return incoming;

        var serverUids = await _imap.GetFolderUidsAsync(account.Id, folder.FullName, ct);
        var serverSet  = new HashSet<uint>(serverUids);
        var deletedUids = localUids.Where(u => !serverSet.Contains(u)).ToList();

        if (deletedUids.Count == 0) return incoming;

        LogService.Log($"Sync {account.AccountLabel}/{folder.FullName}: {deletedUids.Count} remote deletion(s)");
        await _store.DeleteSummariesAsync(account.Id, folder.FullName, deletedUids);

        var removed = deletedUids
            .Select(uid => new MailMessageSummary
            {
                UniqueId   = uid,
                AccountId  = account.Id,
                FolderName = folder.FullName,
            })
            .ToList();

        await Application.Current.Dispatcher.InvokeAsync(
            () => MessagesRemoved?.Invoke(removed));

        return incoming;
    }

    private async Task FetchAndApplyPreviewsAsync(
        AccountModel account, MailFolderModel folder,
        List<MailMessageSummary> incoming, CancellationToken ct)
    {
        try
        {
            // Only fetch bodies for messages the server didn't fill via IMAP PREVIEW.
            var uids = incoming
                .Where(s => string.IsNullOrEmpty(s.Preview))
                .OrderByDescending(s => s.Date)
                .Take(100)
                .Select(s => s.UniqueId)
                .ToList();
            if (uids.Count == 0) return;

            var previewLines = _config.Load().GetPreviewLines(account.Id);
            if (previewLines <= 0) return;
            var previews = await _imap.FetchPreviewsAsync(
                account.Id, folder.FullName, uids, previewLines, ct);

            // Match each summary in 'incoming' to its preview, building both the
            // UI-apply list and the persistence list in one pass.
            var updates = new List<(uint UniqueId, string Preview)>(previews.Count);
            var uiApply = new List<(MailMessageSummary Summary, string Preview)>(previews.Count);
            foreach (var s in incoming)
            {
                if (!previews.TryGetValue(s.UniqueId, out var p)) continue;
                uiApply.Add((s, p));
                updates.Add((s.UniqueId, p));
            }
            if (uiApply.Count == 0) return;

            // One dispatcher hop for the whole batch instead of N — N dispatcher
            // invocations during a fast sync flood the UI thread with continuations.
            await Application.Current.Dispatcher.InvokeAsync(() =>
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
}
