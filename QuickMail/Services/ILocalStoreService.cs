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
    Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds);
    Task DeleteAccountDataAsync(Guid accountId);
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

    /// <summary>
    /// Returns the highest message key stored for this folder, or "0" if none. For the IMAP
    /// backend this is the numeric high-water UID, computed as MAX(CAST(unique_id AS INTEGER))
    /// and rendered as a decimal string; the Graph backend does not use it (it tracks a delta token).
    /// </summary>
    Task<string> GetMaxMessageKeyAsync(Guid accountId, string folderName);

    /// <summary>Returns all message ids stored locally for this folder.</summary>
    Task<HashSet<string>> GetAllMessageIdsAsync(Guid accountId, string folderName);

    /// <summary>
    /// Counts all message summaries stored for the given account.
    /// Returns 0 if the account has no messages or does not exist.
    /// </summary>
    Task<int> CountSummariesAsync(Guid accountId);

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
    /// Returns the distinct server calendars (one row per account + calendar) across all synced rows
    /// (<c>is_graph = 1</c> with a non-empty <c>calendar_id</c>), for building the per-calendar
    /// grandchild nodes under each account in the folder tree.
    /// </summary>
    Task<IReadOnlyList<(Guid AccountId, string CalendarId, string CalendarName)>> LoadCalendarSourcesAsync();

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
}
