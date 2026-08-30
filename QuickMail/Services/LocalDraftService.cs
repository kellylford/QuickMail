using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// <see cref="ILocalDraftService"/> over <see cref="ILocalStoreService"/> (#637).
/// <para>A pending draft is stored as the complete RFC 5322 message plus the summary and detail rows
/// that put it in the Drafts list. Keeping the whole MIME — not just the fields — is what lets a
/// draft with attachments reopen after a restart without asking the server for anything: the parts
/// are in the bytes. It is also what the upload pass needs to rebuild the compose state.</para>
/// </summary>
public sealed class LocalDraftService(ILocalStoreService store) : ILocalDraftService
{
    private readonly ILocalStoreService _store = store;

    /// <summary>
    /// Server draft this local copy supersedes, carried in the stored MIME rather than in a database
    /// column. It has to survive a restart — the upload has to know which server draft to replace, or
    /// it leaves the stale copy behind as a duplicate — and a header travels with the message that
    /// owns it, where a column would be a second thing to migrate and keep in step.
    /// </summary>
    internal const string ReplacesHeader = "X-QuickMail-Replaces-Draft";

    public async Task<PendingDraftSave> SaveAsync(
        AccountModel account, ComposeModel draft, string folderName,
        string? previousMessageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(draft);

        var replacingLocal = LocalMessageId.IsLocal(previousMessageId);
        var messageId      = replacingLocal ? previousMessageId! : LocalMessageId.New();

        // Which server draft this supersedes. Editing a local draft repeatedly must not lose the
        // server id the first save recorded, so carry it forward from what is already stored.
        var supersedes = replacingLocal
            ? await ReadSupersededIdAsync(account.Id, folderName, messageId)
            : previousMessageId;

        var msg = MimeMessageBuilder.Build(draft, account);
        if (!string.IsNullOrEmpty(supersedes))
            msg.Headers.Add(ReplacesHeader, supersedes);

        using var ms = new MemoryStream();
        await msg.WriteToAsync(ms, ct);
        var mime = ms.ToArray();

        var summary = BuildSummary(account, draft, messageId, folderName);
        var detail  = BuildDetail(summary, draft);

        // Bytes before the row that advertises them. These are three writes on three connections,
        // not one transaction, so a crash or a forced shutdown can land between any two — and the
        // order decides what that leaves behind. Summary first leaves a row the reader cannot open
        // and the upload pass discards, which is the draft silently disappearing. This way the
        // worst case is stored bytes no row points at: invisible, harmless, and reclaimed the next
        // time the same draft is saved (#637).
        await _store.UpsertDetailAsync(detail);
        await _store.StoreMimeBytesAsync(account.Id, folderName, messageId, mime);
        await _store.UpsertSummariesAsync([summary]);

        // The superseded server draft still exists on the server — the upload replaces it — but
        // showing both rows would offer a stale copy alongside the fresh one with nothing to tell
        // them apart. Drop the row; a sync before the upload lands brings it back, which is
        // recoverable, where silently preferring the stale copy would not be.
        //
        // Inside a catch, and that is the point: the message is already committed by the time this
        // runs, so throwing here would report a save that plainly did happen. The caller would
        // tell the user the draft was not saved while the row sat on disk waiting to be uploaded.
        // A leftover row is visible and recoverable; that is not (#637).
        try
        {
            if (!replacingLocal && !string.IsNullOrEmpty(supersedes))
                await _store.DeleteSummariesAsync(account.Id, folderName, [supersedes]);
        }
        catch (Exception ex)
        {
            LogService.Log("LocalDraftService.SaveAsync: the draft is saved; tidying the old row failed", ex);
        }

        return new PendingDraftSave(messageId, supersedes);
    }

    public async Task<string?> ResolveDraftsFolderNameAsync(Guid accountId)
    {
        var byAccount = await _store.LoadFoldersAsync();
        if (!byAccount.TryGetValue(accountId, out var folders)) return null;

        foreach (var folder in folders)
            if (folder.Kind == SpecialFolderKind.Drafts && !string.IsNullOrEmpty(folder.FullName))
                return folder.FullName;

        return null;
    }

    public async Task<ComposeModel?> LoadAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        if (!LocalMessageId.IsLocal(messageId)) return null;

        var mime = await _store.LoadMimeBytesAsync(accountId, folderName, messageId);
        if (mime == null) return null;

        using var ms = new MemoryStream(mime);
        var msg = await MimeMessage.LoadAsync(ms, ct);

        var compose = new ComposeModel
        {
            Kind            = ComposeKind.EditDraft,
            AccountId       = accountId,
            To              = ImapMailService.FormatAddressList(msg.To),
            Cc              = ImapMailService.FormatAddressList(msg.Cc),
            Bcc             = ImapMailService.FormatAddressList(msg.Bcc),
            Subject         = msg.Subject ?? string.Empty,
            Body            = msg.TextBody ?? string.Empty,
            Mode            = ImapMailService.ParseComposeMode(msg.Headers["X-QuickMail-Compose-Mode"]),
            DraftMessageId  = messageId,
            DraftFolderName = folderName,
            // From the summary row, not the stored bytes: the reason is written after the
            // MIME was stored, so the bytes have never heard of it (#637).
            DeliveryNotice  = await ReadDeliveryNoticeAsync(accountId, folderName, messageId),
        };

        if (compose.Mode == ComposeMode.Html)
            compose.HtmlBody = msg.HtmlBody;

        // Bytes, not part specifiers: an attachment on a pending draft has never been on a server,
        // so there is nothing to fetch it from later. It is loaded now or it is lost.
        foreach (var part in msg.Attachments)
        {
            using var partStream = new MemoryStream();
            // Both are genuinely nullable in MimeKit — a part declared with no content, and a
            // message/rfc822 wrapper with nothing parsed into it. Either yields an empty
            // attachment rather than an exception, which is the same answer the reader gives.
            if (part is MimePart { Content: not null } mimePart)
                await mimePart.Content.DecodeToAsync(partStream, ct);
            else if (part is MessagePart { Message: not null } messagePart)
                await messagePart.Message.WriteToAsync(partStream, ct);

            var bytes = partStream.ToArray();
            compose.Attachments.Add(new AttachmentModel
            {
                FileName    = part.ContentDisposition?.FileName ?? part.ContentType?.Name ?? "attachment",
                ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                FileSize    = bytes.Length,
                Content     = bytes,
            });
        }

        return compose;
    }

    public Task<string?> GetSupersededServerIdAsync(Guid accountId, string folderName, string messageId)
        => ReadSupersededIdAsync(accountId, folderName, messageId);

    /// <summary>Why the server would not take this draft, as a sentence, or empty (#637).</summary>
    private async Task<string> ReadDeliveryNoticeAsync(Guid accountId, string folderName, string messageId)
    {
        try
        {
            // One row, one column. Reading the WHOLE folder to pluck one field ran on every
            // draft open - and the upload pass calls LoadAsync for every pending draft, where
            // the notice is never looked at (#637).
            var reason = await _store.GetSendFailedReasonAsync(accountId, folderName, messageId);
            if (string.IsNullOrEmpty(reason)) return string.Empty;

            return new MailMessageSummary
            {
                MessageId = messageId, FolderName = folderName, AccountId = accountId,
                IsPendingUpload = true, SendFailedReason = reason,
            }.DeliveryNotice;
        }
        catch (Exception ex)
        {
            // A store that will not answer must not stop the draft opening; the user loses the
            // explanation, not the message.
            LogService.Log("LocalDraftService: could not read the delivery notice", ex);
            return string.Empty;
        }
    }

    public Task MarkSendFailedAsync(Guid accountId, string folderName, string messageId, string reason)
        => _store.MarkSendFailedAsync(accountId, folderName, messageId, reason);

    public Task DiscardAsync(Guid accountId, string folderName, string messageId)
        => _store.DeleteSummariesAsync(accountId, folderName, [messageId]);

    public async Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid accountId)
        => await _store.LoadPendingDraftsAsync(accountId);

    /// <summary>
    /// The server draft id recorded on a stored pending draft, or null. Read out of the stored MIME
    /// rather than from a parallel record of it.
    /// </summary>
    internal async Task<string?> ReadSupersededIdAsync(Guid accountId, string folderName, string messageId)
    {
        var mime = await _store.LoadMimeBytesAsync(accountId, folderName, messageId);
        if (mime == null) return null;

        using var ms = new MemoryStream(mime);
        var msg = await MimeMessage.LoadAsync(ms);
        var value = msg.Headers[ReplacesHeader];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// The Drafts-list row for a pending draft. Built from the compose state rather than re-read
    /// from the MIME just written: the two agree, and the compose state is what the user typed.
    /// </summary>
    private static MailMessageSummary BuildSummary(
        AccountModel account, ComposeModel draft, string messageId, string folderName) => new()
    {
        MessageId       = messageId,
        AccountId       = account.Id,
        FolderName      = folderName,
        From            = account.SenderDisplayName,
        To              = draft.To,
        Subject         = string.IsNullOrWhiteSpace(draft.Subject) ? "(no subject)" : draft.Subject,
        Date            = DateTimeOffset.Now,
        IsRead          = true,
        Preview         = BuildPreview(draft),
        HasAttachments  = draft.Attachments.Count > 0,
        IsPendingUpload = true,
    };

    private static MailMessageDetail BuildDetail(MailMessageSummary summary, ComposeModel draft) => new()
    {
        MessageId        = summary.MessageId,
        AccountId        = summary.AccountId,
        FolderName       = summary.FolderName,
        From             = summary.From,
        To               = draft.To,
        Cc               = draft.Cc,
        Subject          = summary.Subject,
        Date             = summary.Date,
        IsRead           = true,
        IsPendingUpload  = true,
        PlainTextBody    = draft.Body,
        HtmlBody         = draft.HtmlBody ?? string.Empty,
        DraftComposeMode = draft.Mode,
        Attachments      = [.. draft.Attachments],
    };

    private static string BuildPreview(ComposeModel draft)
    {
        var text = (draft.Body ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= 200 ? text : text[..200];
    }
}
