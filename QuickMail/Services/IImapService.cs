using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IImapService : IDisposable
{
    Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default);
    Task DisconnectAsync(Guid accountId, CancellationToken ct = default);
    Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default);
    Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default);

    /// <summary>
    /// Incremental fetch for background sync.
    /// When sinceUid == 0 (first sync), returns the last 500 messages.
    /// When sinceUid > 0, returns only messages with UID > sinceUid (IMAP UID range).
    /// </summary>
    Task<List<MailMessageSummary>> GetMessagesSinceAsync(
        Guid accountId, string folderName, uint sinceUid, CancellationToken ct = default);
    Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, uint uid, CancellationToken ct = default);
    Task MarkReadAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default);
    Task MoveToTrashAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default);
    Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default);
    Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default);
    Task<IList<uint>> GetFolderUidsAsync(Guid accountId, string folderName, CancellationToken ct = default);

    /// <summary>
    /// Fetches plain-text previews for the given UIDs in one folder open.
    /// Returns a mapping of UniqueId → preview string (empty entries omitted).
    /// </summary>
    Task<IReadOnlyDictionary<uint, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<uint> uids,
        int maxLines, CancellationToken ct = default);

    Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default);
}
