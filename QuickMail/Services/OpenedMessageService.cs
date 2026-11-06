using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Messages that are not in any mailbox: a .eml file opened from Explorer, or an email attached to
/// another message (#939). Each is held as its original bytes under one reserved account id, which
/// the <see cref="MailServiceRouter"/> sends here. That is what lets everything a message window does
/// with a message (show its pictures, open, save and copy its attachments, forward them, save the
/// message, print it) work on these exactly as on mail in an account, with no special cases.
///
/// <para>Nothing here changes: there is no server. Reading works; marking, moving, deleting and the
/// like are refused with a sentence saying why, and the window does not offer them.</para>
/// </summary>
public sealed class OpenedMessageService : IMailService
{
    /// <summary>The account id every opened message carries. Not a real account: never in the account list.</summary>
    public static readonly Guid AccountId = new("0e1e0e1e-0e1e-4e1e-8e1e-0e1e0e1e0e1e");

    /// <summary>The folder name every opened message carries.</summary>
    public const string FolderName = "Opened";

    private const string NotInAMailbox = "This message is not in a mailbox, so it cannot be changed.";

    /// <summary>
    /// One opened message: its original bytes, and the message parsed from them once. A part's content is
    /// a stream, so two reads of one message at once (pictures load on the thread pool) take turns.
    /// </summary>
    private sealed class Held(byte[] original, MimeMessage message)
    {
        public byte[] Original { get; } = original;
        public MimeMessage Message { get; } = message;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Held> _originals = new();

    /// <summary>True for a message held here rather than in an account.</summary>
    public static bool IsOpenedMessage(MailMessageSummary? message) => message?.AccountId == AccountId;

    /// <summary>
    /// Takes in a message's original bytes and returns it ready to show. Throws
    /// <see cref="FormatException"/> when the bytes are not an email.
    /// </summary>
    public MailMessageDetail Open(byte[] original)
    {
        var message = Parse(original);
        var id = Guid.NewGuid().ToString("N");
        _originals[id] = new Held(original, message);
        return Pop3MailService.BuildDetail(AccountId, id, message, FolderName, isRead: true);
    }

    /// <summary>Lets go of a message once nothing shows it any more.</summary>
    public void Close(string messageId) => _originals.TryRemove(messageId, out _);

    /// <summary>How many messages are held. For tests.</summary>
    internal int Count => _originals.Count;

    private static MimeMessage Parse(byte[] original)
    {
        if (original.Length == 0) throw new FormatException("The file is empty.");
        MimeMessage message;
        try
        {
            using var stream = new MemoryStream(original, writable: false);
            message = MimeMessage.Load(stream);
        }
        catch (FormatException) { throw new FormatException("The file is not an email message."); }
        // The parser is lenient: bytes that are not an email come back as a message with no headers.
        if (message.Headers.Count == 0) throw new FormatException("The file is not an email message.");
        return message;
    }

    private Held HeldFor(string messageId) =>
        _originals.TryGetValue(messageId, out var held)
            ? held
            : throw new MessageOriginalUnavailableException("The message is no longer open.");

    private static async Task<T> WithMessageAsync<T>(Held held, Func<MimeMessage, Task<T>> read, CancellationToken ct)
    {
        await held.Gate.WaitAsync(ct);
        try { return await read(held.Message); }
        finally { held.Gate.Release(); }
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => WithMessageAsync(HeldFor(messageId),
            m => Task.FromResult(Pop3MailService.BuildDetail(AccountId, messageId, m, FolderName, isRead: true)), ct);

    public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => GetMessageDetailAsync(accountId, folderName, messageId, ct);

    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
        => WithMessageAsync(HeldFor(messageId), m => Pop3MailService.ExtractPartAsync(m, messageId, partSpecifier, ct), ct);

    public async Task CopyOriginalMessageToAsync(Guid accountId, string folderName, string messageId, Stream destination, CancellationToken ct = default)
        => await destination.WriteAsync(HeldFor(messageId).Original, ct);

    // ── Connection: always there, nothing to connect to ──────────────────────

    public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
    public bool IsConnected(Guid accountId) => true;
    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;

    // ── No folders, no lists ─────────────────────────────────────────────────

    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(new List<MailFolderModel>());
    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>(Array.Empty<(string, DateTimeOffset, bool)>());
    public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
    public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult((0, 0));
    public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    // ── Changing anything: refused ───────────────────────────────────────────

    private static Task Refused() => Task.FromException(new InvalidOperationException(NotInAMailbox));
    private static Task<T> Refused<T>() => Task.FromException<T>(new InvalidOperationException(NotInAMailbox));

    public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Refused();
    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Refused();
    public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default) => Refused();
    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Refused();
    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Refused();
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Refused();
    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Refused<int>();
    public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default) => Refused<string>();
    public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => Refused();
    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Refused();
    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Refused();
    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => Refused();
    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => Refused();
    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => Refused();
    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => Refused();

    public void Dispose() => _originals.Clear();
}
