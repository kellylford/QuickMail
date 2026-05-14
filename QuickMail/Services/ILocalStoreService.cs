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
    Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null);
    Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<uint> uniqueIds);
    Task UpdateIsReadAsync(Guid accountId, string folderName, uint uniqueId, bool isRead);
    Task UpdatePreviewAsync(Guid accountId, string folderName, uint uniqueId, string preview);
    Task<bool> HasSummariesMissingRecipientsAsync();

    Task UpsertDetailAsync(MailMessageDetail detail);
    Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, uint uniqueId);

    /// <summary>Returns the highest UniqueId stored for this folder, or 0 if none.</summary>
    Task<uint> GetMaxUidAsync(Guid accountId, string folderName);

    /// <summary>Returns all UniqueIds stored locally for this folder.</summary>
    Task<HashSet<uint>> GetAllUidsAsync(Guid accountId, string folderName);
}
