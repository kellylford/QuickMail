using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IMailService : IDisposable
{
    Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default);
    Task DisconnectAsync(Guid accountId, CancellationToken ct = default);
    Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default);
    Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default);

    /// <summary>
    /// Fetches messages delivered on or after <paramref name="since"/> using IMAP SEARCH SINCE.
    /// Results are sorted newest-first.
    /// </summary>
    Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(
        Guid accountId, string folderName, DateTime since, CancellationToken ct = default);

    /// <summary>
    /// Incremental fetch for background sync.
    /// When <paramref name="sinceMessageId"/> is "0" (first sync), returns the last
    /// <paramref name="initialCount"/> messages. Otherwise the IMAP backend parses it as a UID and
    /// returns only messages with UID &gt; that value. The Graph backend ignores this argument and
    /// uses the folder's stored delta token instead (see the GraphChangeNotifier spec).
    /// </summary>
    Task<List<MailMessageSummary>> GetMessagesSinceAsync(
        Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default);
    Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="GetMessageDetailAsync"/> but uses a background IMAP lease
    /// and does NOT set the Seen flag on the server. Intended for prefetching nearby
    /// messages into the local cache so subsequent opens are instant.
    /// </summary>
    Task<MailMessageDetail> PrefetchMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default);
    Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default);
    Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default);

    /// <summary>Sets or clears the server \Flagged flag on a single message.</summary>
    Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default);
    Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default);
    Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default);
    /// <summary>Permanently deletes messages already in Trash by setting \Deleted and expunging.</summary>
    Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default);
    /// <summary>Sends IMAP NOOP to keep the connection alive. Silently discards the client if the connection is stale.</summary>
    Task NoOpAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Raised when an account's reachability changes (connection lost due to IDLE watcher failure,
    /// or connection restored after successful IDLE reconnect). The bool indicates current reachability.
    /// Fired on the ThreadPool; handlers should marshal to UI thread if needed.
    /// </summary>
    event Action<Guid, bool>? AccountReachabilityChanged;

    /// <summary>
    /// Counts messages in the Trash folder (if it exists) without opening other folders.
    /// Returns 0 if Trash does not exist or is empty.
    /// </summary>
    Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Raised on the ThreadPool when IMAP IDLE detects new mail in the INBOX of the given account.
    /// The handler should marshal to the UI thread if needed.
    /// </summary>
    event Action<Guid>? InboxNewMailDetected;

    /// <summary>
    /// Starts one IDLE watcher connection per account in <paramref name="accounts"/>.
    /// Each watcher watches INBOX and fires <see cref="InboxNewMailDetected"/> when new mail arrives.
    /// Call after all accounts are connected. Safe to call multiple times — existing watchers for
    /// the same account are stopped and replaced.
    /// </summary>
    void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default);

    /// <summary>Stops all IDLE watcher connections.</summary>
    void StopIdleWatchers();
    Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default);
    Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default);

    /// <summary>
    /// Fetches plain-text previews for the given message ids in one folder open.
    /// Returns a mapping of message id → preview string (empty entries omitted).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<string> messageIds,
        int maxLines, CancellationToken ct = default);

    Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default);

    /// <summary>
    /// Returns (Total, Unread) for the account's INBOX using an IMAP STATUS command.
    /// Cheaper than <see cref="GetFoldersAsync"/> — does not open any folder.
    /// </summary>
    Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Returns the full name of the Drafts folder, or null if none.</summary>
    Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Saves a draft to the server Drafts folder.
    /// If <paramref name="replaceMessageId"/> is provided the old draft is deleted first.
    /// Returns the message id of the newly appended message.
    /// </summary>
    Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default);

    /// <summary>
    /// Appends a sent message to the server Sent folder so it appears in the Sent view.
    /// Providers that auto-copy sent mail (e.g. Gmail) will silently duplicate; we rely on
    /// the server to deduplicate or accept the extra copy — most handle this gracefully.
    /// </summary>
    Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default);

    /// <summary>Downloads and decodes a single attachment by its IMAP body-part specifier.</summary>
    Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default);

    // ── Copy / Move messages ─────────────────────────────────────────────────
    Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default);
    Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default);

    // ── Folder CRUD ──────────────────────────────────────────────────────────
    Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default);
    Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default);
    /// <summary>Moves a folder by renaming it under a new parent. Pass null for <paramref name="newParentFolderName"/> to move to the account root.</summary>
    Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default);
    /// <summary>Recursively copies a folder and all its messages into a new parent folder.</summary>
    Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default);
}
