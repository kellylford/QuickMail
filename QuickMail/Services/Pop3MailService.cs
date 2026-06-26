using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Pop3;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// <see cref="IMailService"/> implementation for POP3/SMTP accounts.
///
/// Key behavioural differences from <see cref="ImapMailService"/>:
/// - Full messages are downloaded at sync time; no on-demand body fetch.
/// - UIDL strings serve as message identifiers.
/// - All state mutations (read/unread, flags, move, trash) are local-only; the server has no
///   per-message state beyond "present" or "absent".
/// - The server-side folder tree does not exist. Four local synthetic folders are exposed:
///   Inbox, Sent, Drafts, Trash.
/// - When <see cref="AccountModel.Pop3LeaveMailOnServer"/> is false, messages are deleted from
///   the server after confirmed local storage. Server deletion is verified by UIDL — if the
///   UIDL is no longer present (the user retrieved it from another client), the DELE is skipped
///   so the wrong message is never deleted.
/// - Folder CRUD operations throw <see cref="NotSupportedException"/>.
/// - The <c>--online</c> flag bypasses POP3 accounts entirely (see App.xaml.cs).
/// </summary>
public class Pop3MailService : IMailService
{
    internal const string InboxFolder  = "Inbox";
    internal const string SentFolder   = "Sent";
    internal const string DraftsFolder = "Drafts";
    internal const string TrashFolder  = "Trash";

    private readonly ILocalStoreService _store;
    private readonly bool _onlineMode;

    // Per-account state
    private readonly ConcurrentDictionary<Guid, AccountModel> _accounts  = new();
    private readonly ConcurrentDictionary<Guid, string>       _passwords = new();
    // POP3 servers typically allow only one concurrent session per mailbox.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks    = new();

    private bool _disposed;

    public Pop3MailService(ILocalStoreService store, bool onlineMode = false)
    {
        _store      = store;
        _onlineMode = onlineMode;
    }

    // ── Connect / Disconnect ──────────────────────────────────────────────────

    public async Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _accounts[account.Id] = account;
        if (password is not null)
            _passwords[account.Id] = password;
        _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));

        // Probe the server to validate the credentials.
        using var client = await OpenConnectionAsync(account.Id, ct);
        await client.DisconnectAsync(quit: false, ct);
    }

    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default)
    {
        _accounts.TryRemove(accountId, out _);
        _passwords.TryRemove(accountId, out _);
        if (_locks.TryRemove(accountId, out var sem))
            sem.Dispose();
        return Task.CompletedTask;
    }

    // ── Folders ───────────────────────────────────────────────────────────────

    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        var folders = new List<MailFolderModel>
        {
            Folder(accountId, InboxFolder,  "Inbox",  SpecialFolderKind.Inbox,  excludeFromAll: false),
            Folder(accountId, SentFolder,   "Sent",   SpecialFolderKind.Sent,   excludeFromAll: true),
            Folder(accountId, DraftsFolder, "Drafts", SpecialFolderKind.Drafts, excludeFromAll: true),
            Folder(accountId, TrashFolder,  "Trash",  SpecialFolderKind.Trash,  excludeFromAll: true),
        };
        return Task.FromResult(folders);

        static MailFolderModel Folder(Guid aid, string full, string display, SpecialFolderKind kind, bool excludeFromAll) =>
            new() { AccountId = aid, FullName = full, DisplayName = display, Kind = kind, ExcludeFromAllMail = excludeFromAll };
    }

    // ── Message fetch ─────────────────────────────────────────────────────────

    public async Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
        => await _store.LoadFolderSummariesAsync(accountId, folderName, maxMessages);

    public async Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(
        Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
    {
        var all = await _store.LoadFolderSummariesAsync(accountId, folderName);
        return all.Where(m => m.Date.UtcDateTime >= since).ToList();
    }

    /// <summary>
    /// Downloads messages from the POP3 server that have not yet been stored locally.
    /// Uses UIDL deduplication — the <paramref name="sinceMessageId"/> argument is ignored;
    /// the local store's existing message IDs determine what is "new".
    /// Only acts on the Inbox folder; synthetic local folders (Sent, Drafts, Trash) are ignored.
    /// </summary>
    public async Task<List<MailMessageSummary>> GetMessagesSinceAsync(
        Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default)
    {
        if (!string.Equals(folderName, InboxFolder, StringComparison.OrdinalIgnoreCase))
            return [];

        if (_onlineMode)
        {
            LogService.Log($"POP3 sync skipped for account {accountId}: --online mode is active (local store unavailable).");
            return [];
        }

        if (!_accounts.TryGetValue(accountId, out var account))
            return [];

        var seenUidls = await _store.GetAllMessageIdsAsync(accountId, InboxFolder);
        var downloaded = new List<MailMessageSummary>();

        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            using var client = await OpenConnectionAsync(accountId, ct);

            if (client.Count == 0)
            {
                await client.DisconnectAsync(quit: false, ct);
                return downloaded;
            }

            // Index in this list is the 0-based message index for GetMessageAsync / DeleteMessageAsync.
            var serverUidls = await client.GetMessageUidsAsync(ct);
            bool anyDeleted = false;

            for (int i = 0; i < serverUidls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var uidl = serverUidls[i];
                if (seenUidls.Contains(uidl))
                    continue;

                MimeMessage msg;
                try
                {
                    msg = await client.GetMessageAsync(i, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogService.Log($"POP3 [{account.AccountLabel}] failed to download UIDL {uidl}", ex);
                    continue;
                }

                var summary = BuildSummary(accountId, uidl, msg);
                var detail  = BuildDetail(accountId, uidl, msg);

                await _store.UpsertSummariesAsync([summary]);
                await _store.UpsertDetailAsync(detail);

                // Store raw MIME bytes only for messages with attachments so we can serve them
                // offline later without reconnecting. Storing all MIME bytes would bloat the DB.
                if (detail.Attachments.Count > 0)
                {
                    using var ms = new MemoryStream();
                    await msg.WriteToAsync(ms, ct);
                    await _store.StoreMimeBytesAsync(accountId, InboxFolder, uidl, ms.ToArray());
                }

                downloaded.Add(summary);

                if (!account.Pop3LeaveMailOnServer)
                {
                    // Mark for server deletion. DELEs are only committed when we QUIT below.
                    await client.DeleteMessageAsync(i, ct);
                    anyDeleted = true;
                }
            }

            // QUIT commits any pending DELEs; abort otherwise (no-op in either case if nothing changed).
            await client.DisconnectAsync(quit: anyDeleted, ct);
            return downloaded;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        var detail = await _store.LoadDetailAsync(accountId, folderName, messageId);
        if (detail is null)
            throw new InvalidOperationException(
                $"POP3 message {messageId} not found in local store. It may not have been downloaded yet.");
        return detail;
    }

    public Task<MailMessageDetail> PrefetchMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => GetMessageDetailAsync(accountId, folderName, messageId, ct);

    // ── State mutations (local-only) ──────────────────────────────────────────

    public async Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => await _store.UpdateIsReadAsync(accountId, folderName, messageId, isRead: true);

    public async Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => await _store.UpdateIsReadBatchAsync(messageIds.Select(id => (accountId, folderName, id)), isRead: true);

    public async Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default)
        => await _store.UpdateFlagIdAsync(accountId, folderName, messageId,
            flagged ? FlagDefinition.BuiltInFlagId.ToString() : null);

    // ── Trash (local two-step delete) ────────────────────────────────────────

    public async Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => await MoveLocalAsync(accountId, folderName, TrashFolder, [messageId]);

    public async Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => await MoveLocalAsync(accountId, folderName, TrashFolder, messageIds);

    public async Task PermanentlyDeleteBatchAsync(
        Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        await _store.DeleteSummariesAsync(accountId, folderName, messageIds);

        if (!_accounts.TryGetValue(accountId, out var account) || account.Pop3LeaveMailOnServer)
            return;

        // Only messages that originally came from the server (Inbox or Trash moved-from-Inbox) may
        // still exist on the POP3 server. Skip synthetic-only folders.
        if (!string.Equals(folderName, TrashFolder, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(folderName, InboxFolder, StringComparison.OrdinalIgnoreCase))
            return;

        var idsToDelete = new HashSet<string>(messageIds, StringComparer.Ordinal);
        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            using var client = await OpenConnectionAsync(accountId, ct);
            var serverUidls = await client.GetMessageUidsAsync(ct);

            bool anyDeleted = false;
            for (int i = 0; i < serverUidls.Count; i++)
            {
                // Safety check (Q2): only issue DELE if the UIDL is still present on the server.
                // A message already retrieved by another POP3 client will be absent from
                // serverUidls — skipping it prevents deleting the wrong message by index.
                if (idsToDelete.Contains(serverUidls[i]))
                {
                    await client.DeleteMessageAsync(i, ct);
                    anyDeleted = true;
                }
            }

            await client.DisconnectAsync(quit: anyDeleted, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
    {
        var trashIds = await _store.GetAllMessageIdsAsync(accountId, TrashFolder);
        if (trashIds.Count == 0) return 0;
        await PermanentlyDeleteBatchAsync(accountId, TrashFolder, trashIds.ToList(), ct);
        return trashIds.Count;
    }

    public async Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default)
        => (await _store.GetAllMessageIdsAsync(accountId, TrashFolder)).Count;

    // ── Sent / Drafts (local-only) ────────────────────────────────────────────

    public async Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default)
    {
        _accounts.TryGetValue(accountId, out var account);
        var msg    = MimeMessageBuilder.Build(sent, account ?? new AccountModel { Id = accountId }, null);
        var uidl   = "sent-" + Guid.NewGuid().ToString("N");
        await _store.UpsertSummariesAsync([BuildSummary(accountId, uidl, msg, SentFolder, isRead: true)]);
        await _store.UpsertDetailAsync(BuildDetail(accountId, uidl, msg, SentFolder));
    }

    public async Task<string> AppendDraftAsync(
        Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
    {
        if (replaceMessageId is not null)
            await _store.DeleteSummariesAsync(accountId, DraftsFolder, [replaceMessageId]);

        _accounts.TryGetValue(accountId, out var account);
        var msg  = MimeMessageBuilder.Build(draft, account ?? new AccountModel { Id = accountId }, null);
        var uidl = "draft-" + Guid.NewGuid().ToString("N");
        await _store.UpsertSummariesAsync([BuildSummary(accountId, uidl, msg, DraftsFolder, isRead: true)]);
        await _store.UpsertDetailAsync(BuildDetail(accountId, uidl, msg, DraftsFolder));
        return uidl;
    }

    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult<string?>(DraftsFolder);

    // ── Attachment download (from locally stored MIME bytes) ──────────────────

    public async Task<byte[]> DownloadAttachmentAsync(
        Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
    {
        var mimeBytes = await _store.LoadMimeBytesAsync(accountId, folderName, messageId);
        if (mimeBytes is null)
            throw new InvalidOperationException(
                $"No MIME bytes stored for POP3 message {messageId}. " +
                "The message may have been downloaded before attachment caching was introduced, " +
                "or the message has no attachments.");

        using var ms  = new MemoryStream(mimeBytes);
        var mimeMsg   = await MimeMessage.LoadAsync(ms, ct);
        var parts     = mimeMsg.Attachments.ToList();

        if (!int.TryParse(partSpecifier, out var idx) || idx < 0 || idx >= parts.Count)
            throw new InvalidOperationException(
                $"Invalid attachment index '{partSpecifier}' for POP3 message {messageId} ({parts.Count} attachments present).");

        if (parts[idx] is not MimePart mimePart || mimePart.Content is null)
            throw new InvalidOperationException(
                $"Attachment {idx} of POP3 message {messageId} is not a decodable MIME part.");

        using var buf = new MemoryStream();
        await mimePart.Content.DecodeToAsync(buf, ct);
        return buf.ToArray();
    }

    // ── Poll / NoOp / Inbox status ────────────────────────────────────────────

    public async Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default)
    {
        var newMessages = await GetMessagesSinceAsync(accountId, InboxFolder, "0", 0, ct);
        return newMessages.Count;
    }

    public Task NoOpAsync(Guid accountId, CancellationToken ct = default)
        => Task.CompletedTask; // No persistent connection to keep alive between syncs.

    public async Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        var summaries = await _store.LoadFolderSummariesAsync(accountId, InboxFolder);
        return (summaries.Count, summaries.Count(m => !m.IsRead));
    }

    // ── Folder message IDs / previews ─────────────────────────────────────────

    public async Task<IList<string>> GetFolderMessageIdsAsync(
        Guid accountId, string folderName, CancellationToken ct = default)
        => (await _store.GetAllMessageIdsAsync(accountId, folderName)).ToList();

    public async Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<string> messageIds,
        int maxLines, CancellationToken ct = default)
    {
        // All POP3 messages are fully downloaded at sync time; previews are already in the store.
        var summaries = await _store.LoadFolderSummariesAsync(accountId, folderName);
        return summaries
            .Where(s => messageIds.Contains(s.MessageId) && !string.IsNullOrEmpty(s.Preview))
            .ToDictionary(s => s.MessageId, s => s.Preview);
    }

    // ── Copy / Move (local-only) ──────────────────────────────────────────────

    public Task CopyMessagesAsync(
        Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => MoveLocalAsync(accountId, folderName, destinationFolder, messageIds, copy: true);

    public Task MoveMessagesAsync(
        Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => MoveLocalAsync(accountId, folderName, destinationFolder, messageIds, copy: false);

    // ── Folder CRUD — not supported ───────────────────────────────────────────

    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default)
        => throw new NotSupportedException("Folder operations are not supported for POP3 accounts.");

    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => throw new NotSupportedException("Folder operations are not supported for POP3 accounts.");

    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default)
        => throw new NotSupportedException("Folder operations are not supported for POP3 accounts.");

    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default)
        => throw new NotSupportedException("Folder operations are not supported for POP3 accounts.");

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Opens an authenticated <see cref="Pop3Client"/>. Caller is responsible for disposing it.</summary>
    private async Task<Pop3Client> OpenConnectionAsync(Guid accountId, CancellationToken ct)
    {
        if (!_accounts.TryGetValue(accountId, out var account))
            throw new InvalidOperationException($"Account {accountId} is not registered with Pop3MailService.");

        if (!_passwords.TryGetValue(accountId, out var password) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException($"No password available for POP3 account {account.Username}.");

        var client = new Pop3Client();
        try
        {
            if (account.Pop3AcceptInvalidCert)
            {
#pragma warning disable CA5359 // accept-invalid-cert is an explicit user setting
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            }

            var ssl = account.Pop3UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(account.Pop3Host, account.Pop3Port, ssl, ct);
            await client.AuthenticateAsync(account.Username, password, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies or moves messages between local POP3 synthetic folders.
    /// MIME bytes are carried along so attachment downloads continue to work after a move.
    /// </summary>
    private async Task MoveLocalAsync(
        Guid accountId, string fromFolder, string toFolder,
        IList<string> messageIds, bool copy = false)
    {
        var summaries = await _store.LoadFolderSummariesAsync(accountId, fromFolder);
        var byId = summaries.ToDictionary(s => s.MessageId);

        foreach (var id in messageIds)
        {
            if (!byId.TryGetValue(id, out var src)) continue;
            var detail = await _store.LoadDetailAsync(accountId, fromFolder, id);

            var destSummary = new MailMessageSummary
            {
                MessageId      = id,
                AccountId      = accountId,
                FolderName     = toFolder,
                From           = src.From,
                To             = src.To,
                Subject        = src.Subject,
                Date           = src.Date,
                IsRead         = src.IsRead,
                Preview        = src.Preview,
                HasAttachments = src.HasAttachments,
            };
            await _store.UpsertSummariesAsync([destSummary]);

            if (detail is not null)
            {
                var destDetail = new MailMessageDetail
                {
                    MessageId     = id,
                    AccountId     = accountId,
                    FolderName    = toFolder,
                    From          = detail.From,
                    To            = detail.To,
                    Cc            = detail.Cc,
                    ReplyTo       = detail.ReplyTo,
                    Subject       = detail.Subject,
                    Date          = detail.Date,
                    PlainTextBody = detail.PlainTextBody,
                    HtmlBody      = detail.HtmlBody,
                    Attachments   = detail.Attachments,
                    CalendarIcs   = detail.CalendarIcs,
                };
                await _store.UpsertDetailAsync(destDetail);

                var mimeBytes = await _store.LoadMimeBytesAsync(accountId, fromFolder, id);
                if (mimeBytes is not null)
                    await _store.StoreMimeBytesAsync(accountId, toFolder, id, mimeBytes);
            }

            if (!copy)
                await _store.DeleteSummariesAsync(accountId, fromFolder, [id]);
        }
    }

    private static MailMessageSummary BuildSummary(
        Guid accountId, string uidl, MimeMessage msg,
        string folderName = InboxFolder, bool isRead = false)
    {
        return new MailMessageSummary
        {
            MessageId      = uidl,
            AccountId      = accountId,
            FolderName     = folderName,
            From           = msg.From.Mailboxes.FirstOrDefault()?.ToString() ?? string.Empty,
            To             = string.Join(", ", msg.To.Mailboxes.Select(m => m.ToString())),
            Subject        = msg.Subject ?? string.Empty,
            Date           = msg.Date == default ? DateTimeOffset.UtcNow : msg.Date,
            IsRead         = isRead,
            Preview        = BuildPreview(msg),
            HasAttachments = msg.Attachments.Any(),
        };
    }

    private static MailMessageDetail BuildDetail(
        Guid accountId, string uidl, MimeMessage msg, string folderName = InboxFolder)
    {
        var attachments = new List<AttachmentModel>();
        int idx = 0;
        foreach (var part in msg.Attachments)
        {
            attachments.Add(new AttachmentModel
            {
                FileName      = part.ContentDisposition?.FileName
                                ?? part.ContentType?.Name
                                ?? $"attachment{idx}",
                ContentType   = part.ContentType?.MimeType ?? "application/octet-stream",
                // File size is computed at actual download time (DownloadAttachmentAsync).
                FileSize      = 0,
                // Index-based specifier: used by DownloadAttachmentAsync to locate the part
                // when re-parsing the stored MIME bytes.
                PartSpecifier = idx.ToString(),
            });
            idx++;
        }

        return new MailMessageDetail
        {
            MessageId         = uidl,
            AccountId         = accountId,
            FolderName        = folderName,
            From              = msg.From.Mailboxes.FirstOrDefault()?.ToString() ?? string.Empty,
            To                = string.Join(", ", msg.To.Mailboxes.Select(m => m.ToString())),
            Cc                = string.Join(", ", msg.Cc.Mailboxes.Select(m => m.ToString())),
            ReplyTo           = string.Join(", ", msg.ReplyTo.Mailboxes.Select(m => m.ToString())),
            Subject           = msg.Subject ?? string.Empty,
            Date              = msg.Date == default ? DateTimeOffset.UtcNow : msg.Date,
            InternetMessageId = msg.MessageId ?? string.Empty,
            PlainTextBody     = msg.TextBody ?? string.Empty,
            HtmlBody          = msg.HtmlBody ?? string.Empty,
            Attachments       = attachments,
        };
    }

    private static string BuildPreview(MimeMessage msg)
    {
        var text = msg.TextBody;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Take(3);
        var joined = string.Join(" ", lines);
        return joined.Length > 200 ? joined[..200] : joined;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Pop3MailService));
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var sem in _locks.Values)
            sem.Dispose();
        _locks.Clear();
        GC.SuppressFinalize(this);
    }
}
