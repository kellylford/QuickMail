using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>Messages put in Trash, as (account, folder, id).</summary>
    public List<(Guid AccountId, string Folder, string Id)> Trashed { get; } = new();

    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        Trashed.Add((accountId, folderName, messageId));
        return Task.CompletedTask;
    }

    /// <summary>Accounts that cannot take messages from another account, as Microsoft 365 cannot yet.</summary>
    public HashSet<Guid> CannotAppend { get; } = new();

    /// <summary>Message-Ids of originals to refuse when appended, to make a transfer stop partway.</summary>
    public HashSet<string> FailAppendOf { get; } = new();

    /// <summary>Every message added from another account, with the state it was given.</summary>
    public List<(Guid AccountId, string Folder, string Subject, bool IsRead, bool IsFlagged, DateTimeOffset Received)> Appended { get; } = new();

    public bool CanAppendMessages(Guid accountId) => !CannotAppend.Contains(accountId);

    public async Task AppendMessageAsync(Guid accountId, string folderName, Stream original, bool isRead, bool isFlagged,
                                         DateTimeOffset received, CancellationToken ct = default)
    {
        var message = await MimeKit.MimeMessage.LoadAsync(original, ct);
        if (message.MessageId is { } id && FailAppendOf.Contains(id))
            throw new InvalidOperationException("the other server said no");
        Appended.Add((accountId, folderName, message.Subject, isRead, isFlagged, received));
    }
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
    /// <summary>When set, attachment downloads fail as an unreachable server would.</summary>
    public bool FailDownloads { get; set; }

    /// <summary>Part specifiers downloaded, in order.</summary>
    public List<string> Downloads { get; } = new();

    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
    {
        if (FailDownloads) return Task.FromException<byte[]>(new InvalidOperationException("server said no"));
        Downloads.Add(partSpecifier);
        return Task.FromResult(System.Text.Encoding.UTF8.GetBytes($"content of {partSpecifier}"));
    }
    /// <summary>Every move and copy asked for, in order, for tests that check what reached the server.</summary>
    public List<(string Op, Guid AccountId, string From, IReadOnlyList<string> Ids, string To)> Transfers { get; } = new();

    /// <summary>When set, moves and copies throw, as a server that refuses them would.</summary>
    public bool FailTransfers { get; set; }

    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => Transfer("copy", accountId, folderName, messageIds, destinationFolder);
    /// <summary>Message ids whose original the "server" cannot provide, as POP3 can't once a message has left it.</summary>
    public HashSet<string> OriginalsUnavailable { get; } = new();

    /// <summary>How many originals have been downloaded.</summary>
    public int OriginalsFetched { get; private set; }

    public async Task CopyOriginalMessageToAsync(Guid accountId, string folderName, string messageId, Stream destination, CancellationToken ct = default)
    {
        if (OriginalsUnavailable.Contains(messageId))
            throw new MessageOriginalUnavailableException("This account cannot provide the original message.");
        OriginalsFetched++;
        var bytes = System.Text.Encoding.ASCII.GetBytes($"From: someone@example.com\r\nSubject: Subject {messageId}\r\nMessage-Id: <{messageId}@example.com>\r\n\r\nBody of {messageId}\r\n");
        await destination.WriteAsync(bytes, ct);
    }

    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => Transfer("move", accountId, folderName, messageIds, destinationFolder);

    private Task Transfer(string op, Guid accountId, string from, IList<string> ids, string to)
    {
        if (FailTransfers) return Task.FromException(new InvalidOperationException("server said no"));
        Transfers.Add((op, accountId, from, ids.ToList(), to));
        return Task.CompletedTask;
    }
    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => Task.CompletedTask;
    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => Task.CompletedTask;
    public void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) { }
    public void StopWatchers() { }
    public void Dispose() { }
}

