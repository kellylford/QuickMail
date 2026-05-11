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
    private readonly IImapService _imap;
    private readonly ILocalStoreService _store;
    private readonly IConfigService _config;

    public SyncService(IImapService imap, ILocalStoreService store, IConfigService config)
    {
        _imap   = imap;
        _store  = store;
        _config = config;
    }

    public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
    public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;

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
                    LogService.Log($"Sync {account.DisplayName}/{folder.DisplayName}", ex);
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

    private async Task<List<MailMessageSummary>> SyncFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // ── New messages ─────────────────────────────────────────────────────────
        var maxUid   = await _store.GetMaxUidAsync(account.Id, folder.FullName);
        var incoming = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, maxUid, ct);

        if (incoming.Count > 0)
        {
            await _store.UpsertSummariesAsync(incoming);

            // Show messages immediately — don't wait for body preview fetches.
            await Application.Current.Dispatcher.InvokeAsync(
                () => FolderSynced?.Invoke(incoming));
        }

        // ── Remote deletions ─────────────────────────────────────────────────────
        // Only meaningful when we already have local data for this folder.
        var localUids = await _store.GetAllUidsAsync(account.Id, folder.FullName);
        if (localUids.Count == 0) return incoming;

        var serverUids = await _imap.GetFolderUidsAsync(account.Id, folder.FullName, ct);
        var serverSet  = new HashSet<uint>(serverUids);
        var deletedUids = localUids.Where(u => !serverSet.Contains(u)).ToList();

        if (deletedUids.Count == 0) return incoming;

        LogService.Log($"Sync {account.DisplayName}/{folder.FullName}: {deletedUids.Count} remote deletion(s)");
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

            foreach (var s in incoming)
            {
                if (!previews.TryGetValue(s.UniqueId, out var p)) continue;

                await Application.Current.Dispatcher.InvokeAsync(() => s.Preview = p);
                await _store.UpdatePreviewAsync(s.AccountId, s.FolderName, s.UniqueId, p);
            }
        }
        catch (OperationCanceledException) { /* sync cancelled — normal */ }
        catch (Exception ex)
        {
            LogService.Log($"FetchAndApplyPreviews {account.DisplayName}/{folder.FullName}", ex);
        }
    }
}
