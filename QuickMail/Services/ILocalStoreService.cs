using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface ILocalStoreService
{
    void Initialize();

    Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries);
    Task<List<MailMessageSummary>> LoadAllSummariesAsync();
    Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId);
    Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null);

    /// <summary>
    /// The folder's summaries dated <paramref name="since"/> or later, filtered in SQL rather than in
    /// memory. The POP3 backend's window fetch is a store read (its mail lives nowhere else), and a
    /// mailbox that is never pruned by a server grows without bound — so loading every row to discard
    /// most of them is a cost that grows with the account's whole history, every sweep.
    /// </summary>
    Task<List<MailMessageSummary>> LoadFolderSummariesSinceAsync(Guid accountId, string folderName, DateTimeOffset since);
    Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds);
    Task DeleteAccountDataAsync(Guid accountId);

    /// <summary>Clears cached mail (summaries, bodies, delta cursors) for the given accounts only —
    /// the one-time Graph immutable-id rebuild (#366). Calendar events are not touched.</summary>
    Task ClearCachedMailAsync(IEnumerable<Guid> accountIds);

    /// <summary>
    /// Deletes calendar events for accounts not in <paramref name="knownAccountIds"/> (orphans from a
    /// removed-and-re-added account or a cache rebuild). Local events (<see cref="Guid.Empty"/>) are kept.
    /// </summary>
    Task PurgeCalendarEventsForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds);

    // ── Folders (#516) ───────────────────────────────────────────────────────────
    // The folder list is persisted so the startup folder can be resolved — and the folder tree
    // drawn — before any account connects. Without this, folder metadata exists only in
    // MainViewModel._cachedFolders, populated by GetFoldersAsync after connect, so nothing at
    // launch knows which folders exist or which of them are Inboxes.

    /// <summary>
    /// Replaces the stored folder list for one account, in a single transaction. Replace rather than
    /// upsert: a folder deleted or renamed on the server must disappear locally. Header rows and rows
    /// with an empty <c>FullName</c> are skipped — they are display artifacts, not server folders.
    /// Other accounts are untouched, so a partial connect refreshes only what it reached.
    /// </summary>
    Task SaveFoldersAsync(Guid accountId, IReadOnlyList<MailFolderModel> folders);

    /// <summary>
    /// Every stored folder, grouped by account and in the order it was last saved. Shaped to drop
    /// straight into <c>MainViewModel._cachedFolders</c>.
    /// </summary>
    Task<Dictionary<Guid, List<MailFolderModel>>> LoadFoldersAsync();

    /// <summary>
    /// Deletes folders for accounts not in <paramref name="knownAccountIds"/> — orphans left by an
    /// account removed while the app was closed, or removed and re-added under a new id.
    /// </summary>
    Task PurgeFoldersForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds);
    Task UpdateIsReadAsync(Guid accountId, string folderName, string messageId, bool isRead);
    Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, bool isRead);
    Task UpdatePreviewAsync(Guid accountId, string folderName, string messageId, string preview);

    /// <summary>
    /// Batch-update preview text for many messages in one transaction. Used by SyncService
    /// after fetching previews so a folder of N messages doesn't issue N round-trips.
    /// </summary>
    Task UpdatePreviewsBatchAsync(Guid accountId, string folderName, IEnumerable<(string MessageId, string Preview)> updates);
    Task<bool> HasSummariesMissingRecipientsAsync();

    Task UpsertDetailAsync(MailMessageDetail detail);
    Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId);

    // ── POP3 raw message bytes (#128) ────────────────────────────────────────────

    /// <summary>
    /// Stores the raw RFC 5322 bytes of a POP3 message so its attachments can be extracted later
    /// without a second download — POP3 has no per-part fetch, so the bytes are the only way back to
    /// the parts. Pass null to clear. Called only by <c>Pop3MailService</c>, and only after
    /// <see cref="UpsertDetailAsync"/>; IMAP and Graph messages leave this null.
    /// </summary>
    Task StoreMimeBytesAsync(Guid accountId, string folderName, string messageId, byte[]? mimeBytes);

    /// <summary>
    /// Returns the bytes previously stored by <see cref="StoreMimeBytesAsync"/>, or null if none
    /// were stored (IMAP/Graph messages, and POP3 messages with no attachments).
    /// </summary>
    Task<byte[]?> LoadMimeBytesAsync(Guid accountId, string folderName, string messageId);

    // ── POP3 collected-UIDL ledger (#128) ────────────────────────────────────────
    // The persistent memory of which server UIDLs this profile has ever collected or destroyed.
    // Message rows cannot carry this: a row moves out of the Inbox (delete, rule, manual filing) or
    // leaves the store entirely (Empty Trash), and with leave-on-server on the UIDL is still listed
    // by the server — without the ledger every one of those messages is re-downloaded into the
    // Inbox as new mail on the next sweep, forever.

    /// <summary>Every UIDL ever recorded for this account by <see cref="AddPop3CollectedUidlsAsync"/>.</summary>
    Task<HashSet<string>> LoadPop3CollectedUidlsAsync(Guid accountId);

    /// <summary>Records UIDLs as collected (or deliberately destroyed). Idempotent.</summary>
    Task AddPop3CollectedUidlsAsync(Guid accountId, IEnumerable<string> uidls);

    /// <summary>
    /// Forgets UIDLs the server no longer lists. Pruning only — a UIDL absent from the server can
    /// never be offered again, so remembering it buys nothing.
    /// </summary>
    Task RemovePop3CollectedUidlsAsync(Guid accountId, IEnumerable<string> uidls);

    /// <summary>
    /// Returns the highest message key stored for this folder, or "0" if none. For the IMAP
    /// backend this is the numeric high-water UID, computed as MAX(CAST(unique_id AS INTEGER))
    /// and rendered as a decimal string; the Graph backend does not use it (it tracks a delta token).
    /// </summary>
    Task<string> GetMaxMessageKeyAsync(Guid accountId, string folderName);

    /// <summary>Returns all message ids stored locally for this folder.</summary>
    Task<HashSet<string>> GetAllMessageIdsAsync(Guid accountId, string folderName);

    /// <summary>
    /// Returns id → is_read for every message stored locally in this folder. The periodic sweep uses it
    /// to reconcile read/unread state changed by another client (#462): it diffs this against the
    /// server's read state from the id listing and updates only the rows that differ, no message fetch.
    /// The key set is also the folder's cached-id set (drives the addition/deletion diff), so this one
    /// query replaces a separate <see cref="GetAllMessageIdsAsync"/> on the sweep path.
    /// </summary>
    Task<Dictionary<string, bool>> LoadFolderReadStatesAsync(Guid accountId, string folderName);

    /// <summary>
    /// Id, date and read state for every message in the folder — the three columns an id listing is
    /// made of, and nothing else. The POP3 backend answers <c>GetFolderMessageIdDatesAsync</c> from
    /// the cache, which otherwise means materialising every fifteen-column summary in the folder
    /// (bodies excepted) on every sweep, to project three fields out of each.
    /// </summary>
    Task<IReadOnlyList<(string Id, DateTimeOffset Date, bool IsRead)>> LoadFolderMessageStatesAsync(
        Guid accountId, string folderName);

    /// <summary>Which of <paramref name="messageIds"/> already exist in the folder (bounded lookup).</summary>
    Task<HashSet<string>> GetExistingMessageIdsAsync(Guid accountId, string folderName, IEnumerable<string> messageIds);

    /// <summary>
    /// Counts all message summaries stored for the given account.
    /// Returns 0 if the account has no messages or does not exist.
    /// </summary>
    Task<int> CountSummariesAsync(Guid accountId);

    /// <summary>
    /// Cached message-summary count per folder for one account, keyed by folder_name, in a single
    /// grouped scan (#462 sweep instrumentation). One query per account beats one per folder — it keeps
    /// the measurement's own cost off the timed region and off the per-folder hot path.
    /// </summary>
    Task<Dictionary<string, int>> CountSummariesByFolderAsync(Guid accountId);

    /// <summary>
    /// Total and unread message counts per folder for one account, in a single <c>GROUP BY</c>. What
    /// the POP3 folder tree and Inbox status are asking for: they need four folders' counts and
    /// nothing else, and reading them off <see cref="LoadFolderReadStatesAsync"/> means building a
    /// dictionary of every id in each folder to then count it.
    /// </summary>
    Task<Dictionary<string, (int Total, int Unread)>> CountMessagesByFolderAsync(Guid accountId);

    // ── Local re-filing (#128) ───────────────────────────────────────────────────
    // POP3's folders are QuickMail's own, so moving a message between them is a store operation with
    // no server side at all.

    /// <summary>
    /// Re-files stored messages from one folder to another, moving the summary row, the detail row
    /// and the raw MIME bytes intact — no column is read into managed memory and written back, so a
    /// field the model gains later cannot be dropped in transit. When
    /// <paramref name="copy"/> is true the rows are duplicated instead, leaving the originals.
    /// Ids already present in <paramref name="toFolder"/> are replaced. Returns how many messages
    /// were found in <paramref name="fromFolder"/> and re-filed.
    /// </summary>
    Task<int> RefileMessagesAsync(
        Guid accountId, string fromFolder, string toFolder, IEnumerable<string> messageIds, bool copy);

    /// <summary>
    /// Returns the oldest message date stored for the given account, or null if no messages exist.
    /// Used to display the cache window in Account Properties.
    /// </summary>
    Task<DateTimeOffset?> GetOldestMessageDateAsync(Guid accountId);

    /// <summary>
    /// Sets or clears the named-flag assignment on a single message.
    /// Pass null for flagId to clear the flag.
    /// </summary>
    Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId);

    /// <summary>
    /// Batch-sets or clears the named-flag assignment on multiple messages in a single transaction.
    /// Pass null for flagId to clear all flags in the batch.
    /// </summary>
    Task UpdateFlagIdBatchAsync(
        IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items,
        string? flagId);

    // ── Calendar events ──────────────────────────────────────────────────────────

    /// <summary>Upserts a calendar event by (Uid, AccountId).</summary>
    Task UpsertCalendarEventAsync(CalendarEvent evt);

    /// <summary>Loads all calendar events, ordered by start time ascending (nulls last).</summary>
    Task<List<CalendarEvent>> LoadCalendarEventsAsync();

    /// <summary>
    /// Returns every account's calendars, as the sync recorded them from the provider's own calendar
    /// list — so a calendar with no events in it is still here, and each says whether it can be
    /// written to. Feeds the per-calendar nodes in the folder tree and the editor's Calendar picker.
    ///
    /// <para>
    /// An account the sync has not enumerated yet falls back to the distinct calendars its synced
    /// rows are tagged with, all assumed writable — which is the whole list this used to return, and
    /// keeps the folder tree populated on a profile upgraded between releases.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<CalendarSourceInfo>> LoadCalendarSourcesAsync();

    /// <summary>Records one account's calendar list, as the provider reports it (replaces what was there).</summary>
    Task ReplaceCalendarSourcesAsync(Guid accountId, IReadOnlyList<CalendarSourceInfo> sources);

    /// <summary>Forgets an account's calendar list (calendar sync switched off, or the account removed).</summary>
    Task DeleteCalendarSourcesAsync(Guid accountId);

    /// <summary>Updates only the response status for an event.</summary>
    Task UpdateCalendarResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status);

    /// <summary>Deletes a calendar event by Uid + AccountId.</summary>
    Task DeleteCalendarEventAsync(string uid, Guid accountId);

    /// <summary>
    /// Replaces all Graph-synced calendar rows (<c>is_graph = 1</c>) for the account with the
    /// supplied fresh set, in one transaction. Rows are stored with <c>is_graph = 1</c> regardless
    /// of each event's flag. Harvested-invite and locally-authored rows are untouched. Used by
    /// <see cref="GraphCalendarSyncService"/>'s replace-slice sync (read-down v1, no delta tokens).
    /// </summary>
    Task ReplaceGraphCalendarEventsAsync(Guid accountId, IReadOnlyList<CalendarEvent> events);

    /// <summary>
    /// Returns all non-empty calendar_ics rows from MessageDetail, for harvesting.
    /// Each item is (AccountId, FolderName, MessageId, IcsText).
    /// </summary>
    Task<List<(Guid AccountId, string FolderName, string MessageId, string IcsText)>> LoadAllCalendarIcsAsync();

    /// <summary>
    /// Clears source_message_id and source_folder on any CalendarEvent whose source
    /// message no longer exists in MessageDetail. Called after each harvest so that
    /// events whose invite emails were purged from the local cache don't produce
    /// "message not found" errors when the user tries to open the invitation.
    /// </summary>
    Task ClearOrphanedCalendarSourceLinksAsync();

    /// <summary>
    /// Returns the stored Microsoft Graph delta cursor (a full <c>@odata.deltaLink</c> URL) for an
    /// account+folder, or null if none has been persisted yet (first poll). See dev spec §6.12.
    /// </summary>
    Task<string?> GetDeltaTokenAsync(Guid accountId, string folderId);

    /// <summary>Persists the Graph delta cursor for an account+folder, replacing any existing value.</summary>
    Task SetDeltaTokenAsync(Guid accountId, string folderId, string deltaToken);

    // ── Outbox (#637) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes or replaces one queued item: the row, the compose model as JSON (attachments stripped),
    /// and every loaded attachment as its own blob row, in one transaction. Sets
    /// <see cref="OutboxItem.HasAttachments"/> and <see cref="OutboxItem.UpdatedUtc"/> on the way.
    /// </summary>
    Task UpsertOutboxItemAsync(OutboxItem item, ComposeModel compose);

    /// <summary>Every queued item across all accounts, newest first. Reads no JSON and no blobs.</summary>
    Task<List<OutboxItem>> LoadOutboxItemsAsync();

    Task<OutboxItem?> LoadOutboxItemAsync(string id);

    /// <summary>The stored compose model with attachment bytes rehydrated and <see cref="ComposeModel.OutboxId"/> set; null when the row is gone.</summary>
    Task<ComposeModel?> LoadOutboxComposeAsync(string id);

    Task UpdateOutboxStateAsync(string id, OutboxState state, int attempts, string? lastError, DateTimeOffset? nextAttemptUtc);

    /// <summary>Removes the row and its attachment blobs. A missing id is a no-op.</summary>
    Task DeleteOutboxItemAsync(string id);

    Task<int> CountOutboxItemsAsync();
}
