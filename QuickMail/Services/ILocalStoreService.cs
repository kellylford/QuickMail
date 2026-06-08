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
}
