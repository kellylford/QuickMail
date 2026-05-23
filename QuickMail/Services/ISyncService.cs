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

    Task SyncAllAccountsAsync(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        CancellationToken ct);

    /// <summary>
    /// Syncs a single folder for one account. Used by the IDLE watcher for targeted
    /// inbox sync without a full account-wide sweep.
    /// </summary>
    Task SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct);
}
