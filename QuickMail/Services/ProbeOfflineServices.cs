using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Offline no-op backends wired at the DI root in --ui-probe mode (#180
/// Decision D): the probe must be structurally incapable of touching a real
/// mailbox. Reads return empty; connection state is always disconnected; the
/// few operations that would leave the machine (send, sign-in) throw so a
/// probe that somehow reaches them fails loudly instead of silently no-oping.
/// </summary>
public sealed class ProbeOfflineMailService : IMailService
{
    public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
    public bool IsConnected(Guid accountId) => false;
    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(new List<MailFolderModel>());
    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
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
    public Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>(Array.Empty<(string, DateTimeOffset, bool)>());
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
    public void Dispose() { }
}

public sealed class ProbeOfflineSendMailService : ISendMailService
{
    private static InvalidOperationException Refused() =>
        new InvalidOperationException("Network is disabled in --ui-probe mode; sending is forbidden.");

    public Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default) => Task.FromException(Refused());
    public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password, string organizerEmail, CancellationToken ct = default) => Task.FromException(Refused());
    public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default) => Task.FromException(Refused());
}

public sealed class ProbeOfflineOAuthService : IOAuthService
{
    private static InvalidOperationException Refused() =>
        new InvalidOperationException("Network is disabled in --ui-probe mode; sign-in is forbidden.");

    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default) => Task.FromException<string>(Refused());
    public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => Task.FromException<string>(Refused());
    public Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => Task.FromException<string>(Refused());
    public Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default) => Task.FromException(Refused());
    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default) => Task.FromException<OAuthResult>(Refused());
    public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel account, CancellationToken ct = default) => Task.FromException<OAuthResult>(Refused());
    public Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default) => Task.FromException(Refused());
    public Task RequestCalendarConsentAsync(AccountModel account, CancellationToken ct = default) => Task.FromException(Refused());
    public Task SignOutAsync(AccountModel account) => Task.FromException(Refused());
}
