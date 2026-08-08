using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

/// <summary>
/// Shared test <see cref="IMailService"/> whose folders and messages are supplied per account, so a
/// test can stand up a <see cref="ViewModels.MainViewModel"/> with a known folder tree without a real
/// backend. Extracted from AllArchiveVirtualFolderTests (#452) so the shared-mailbox tests (#31) can
/// reuse it. Everything except folder/message lookup is an inert no-op.
/// </summary>
internal sealed class FolderedMailService : IMailService
{
    private readonly Dictionary<Guid, List<MailFolderModel>> _folders;
    private readonly Dictionary<(Guid, string), List<MailMessageSummary>> _messages;

    public FolderedMailService(
        Dictionary<Guid, List<MailFolderModel>> folders,
        Dictionary<(Guid, string), List<MailMessageSummary>> messages)
    {
        _folders  = folders;
        _messages = messages;
    }

    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) =>
        Task.FromResult(_folders.TryGetValue(accountId, out var f) ? new List<MailFolderModel>(f) : []);

    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) =>
        Task.FromResult(_messages.TryGetValue((accountId, folderName), out var m)
            ? new List<MailMessageSummary>(m) : []);

    public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
        => GetMessageSummariesAsync(accountId, folderName, int.MaxValue, ct);

    public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
    public bool IsConnected(Guid accountId) => true;
    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
    public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
    public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>([]);
    public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
    public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult((0, 0));
    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default) => Task.FromResult("0");
    public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => Task.CompletedTask;
    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => Task.CompletedTask;
    public void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) { }
    public void StopWatchers() { }
    public void Dispose() { }
}
