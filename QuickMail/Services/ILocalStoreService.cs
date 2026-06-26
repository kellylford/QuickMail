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

    // ── POP3 raw MIME storage ────────────────────────────────────────────────────

    /// <summary>
    /// Stores the raw MIME bytes for a POP3 message so attachments can be extracted
    /// locally without reconnecting to the server. Pass null to clear (e.g. on delete).
    /// Only called by Pop3MailService; IMAP/Graph messages leave this column NULL.
    /// </summary>
    Task StoreMimeBytesAsync(Guid accountId, string folderName, string messageId, byte[]? mimeBytes);

    /// <summary>
    /// Returns the raw MIME bytes previously stored by <see cref="StoreMimeBytesAsync"/>,
    /// or null if none were stored (IMAP/Graph messages, or store was cleared).
    /// </summary>
    Task<byte[]?> LoadMimeBytesAsync(Guid accountId, string folderName, string messageId);

    // ── Calendar events ──────────────────────────────────────────────────────────

    /// <summary>Upserts a calendar event by (Uid, AccountId).</summary>
    Task UpsertCalendarEventAsync(CalendarEvent evt);

    /// <summary>Loads all calendar events, ordered by start time ascending (nulls last).</summary>
    Task<List<CalendarEvent>> LoadCalendarEventsAsync();

    /// <summary>Updates only the response status for an event.</summary>
    Task UpdateCalendarResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status);

    /// <summary>Deletes a calendar event by Uid + AccountId.</summary>
    Task DeleteCalendarEventAsync(string uid, Guid accountId);

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
}
