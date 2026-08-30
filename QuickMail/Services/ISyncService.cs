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
    /// Fired on the UI thread when the upload pass records that a draft will not go up (#637),
    /// with the rows as the store now has them.
    /// <para>Distinct from <see cref="MessagesRemoved"/>: the row STAYS. Without this the refusal
    /// only reached the store, so a user sitting in Drafts went on being told the draft was "not
    /// on server" — which the row defines as ON ITS WAY — about one nothing will ever retry.</para>
    /// </summary>
    event Action<IReadOnlyList<MailMessageSummary>>? DraftUploadsRefused;

    /// <summary>
    /// Drafts held only on this computer have just reached the server (the count). Their rows are
    /// dropped through <see cref="MessagesRemoved"/>; this exists so the user can be told the
    /// disappearance was an upload rather than the list reordering itself (#637).
    /// </summary>
    event Action<int>? DraftsUploaded;

    /// <summary>
    /// Fired on the UI thread when the periodic sweep finds that a cached message's read/unread state
    /// was changed by another client (#462). Carries the affected messages with their new
    /// <see cref="MailMessageSummary.IsRead"/>; the store has already been updated. The view updates the
    /// matching rows and refreshes folder counts. Distinct from <see cref="FolderSynced"/> so a read
    /// change never triggers a new-mail toast or touches flag state.
    /// </summary>
    event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;

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
    /// Full sync of one folder: fetches new messages (raising <see cref="FolderSynced"/>) and
    /// reconciles remote deletions (raising <see cref="MessagesRemoved"/>). Used by the periodic
    /// all-folder sweep to keep non-Inbox folders — which have no live watcher — current with mail a
    /// server-side rule filed there while the app runs (#366). Returns the new arrivals (empty when none).
    /// </summary>
    Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel account, MailFolderModel folder, CancellationToken ct);

    /// <summary>
    /// Reconciles one folder's local cache against the server, removing (and raising
    /// <see cref="MessagesRemoved"/> for) any message deleted or moved away by another client. Used
    /// to reconcile a folder on open and on the periodic tick, since the live/add-only sync paths do
    /// not. Backend-agnostic. Returns the number of ghost messages removed (0 when in sync or no local
    /// data yet).
    /// </summary>
    Task<int> ReconcileFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct);

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
