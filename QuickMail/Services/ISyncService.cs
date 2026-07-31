using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface ISyncService
{
    /// <summary>
    /// Fired on the UI thread after each folder is synced, with only the
    /// messages that were not already in the local store.
    /// </summary>
    event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;

    /// <summary>
    /// Fired on the UI thread when messages that exist locally are no longer
    /// present on the server (deleted by another client).
    /// </summary>
    event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;

    /// <summary>
    /// Fired on the UI thread after rules have been applied to incoming messages.
    /// The int is the number of messages matched by rules.
    /// </summary>
    event Action<int>? RulesApplied;

    /// <summary>
    /// Fired on the UI thread as folders complete during SyncAllAccountsAsync.
    /// Parameters are (completedFolders, totalFolders) for progress reporting.
    /// </summary>
    event Action<int, int>? SyncProgressChanged;

    Task SyncAllAccountsAsync(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        CancellationToken ct);

    /// <summary>
    /// Syncs a single folder for one account. Used by the IDLE watcher for targeted
    /// inbox sync without a full account-wide sweep. Returns the messages fetched this pass
    /// (empty when none) so the caller can decide which are genuinely new for notification.
    /// </summary>
    Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct);

    /// <summary>
    /// Online-mode variant: fetches the most recent messages directly from IMAP without
    /// touching the local store, then fires <see cref="FolderSynced"/> so the UI updates.
    /// Returns the messages fetched this pass (empty when none).
    /// </summary>
    Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct);

    /// <summary>
    /// Marks the given accounts as freshly cache-wiped by the one-time immutable-id rebuild (#366), so
    /// the first persisted sync of each of their folders caches its messages but does NOT run client
    /// rules on them (pre-existing mail already processed on first arrival, not new arrivals). Rules
    /// resume on genuinely-new mail from the next sync. Call once at startup, after the rebuild clears
    /// the cache and before any sync runs.
    /// </summary>
    void SeedRebuildBaseline(IEnumerable<Guid> accountIds);

    /// <summary>
    /// Returns the UTC time of the last completed sync for the given account,
    /// or null if the account has never been synced in this session.
    /// </summary>
    DateTimeOffset? LastSyncedUtc(Guid accountId);
}
