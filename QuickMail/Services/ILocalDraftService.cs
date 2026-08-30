using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Drafts held on this computer until a server will take them (#637).
/// <para>Before this existed, every draft save was a server round-trip: composing on a failing
/// connection produced "Save draft failed", auto-save could not help, and closing the window meant
/// choosing between losing the message and not closing the window. The store is now the first place
/// a draft goes and the server is the second, so a draft survives a lost connection, a quit, and a
/// crash.</para>
/// <para>A pending draft lives in the account's own Drafts folder in the local store, so it appears
/// in the Drafts list next to server drafts rather than in a place of its own — marked, via
/// <see cref="MailMessageSummary.IsPendingUpload"/>, as not yet on the server.</para>
/// </summary>
public interface ILocalDraftService
{
    /// <summary>
    /// Writes the compose state to the local store and returns the local message id to keep
    /// editing against. <paramref name="previousMessageId"/> is whatever the compose window was
    /// last saved as — a local id (replaced in place) or a server id (superseded: the local copy
    /// remembers which server draft it replaces, and its row is hidden until the upload lands).
    /// </summary>
    Task<PendingDraftSave> SaveAsync(
        AccountModel account, ComposeModel draft, string folderName,
        string? previousMessageId, CancellationToken ct = default);

    /// <summary>
    /// Reads a pending draft back for editing, or null if the id is not one this service holds.
    /// Attachment bytes come back with it — the draft's stored MIME is the whole message, so
    /// reopening it after a restart does not need the network.
    /// </summary>
    Task<ComposeModel?> LoadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default);

    /// <summary>
    /// The server draft a pending draft supersedes, or null when it is a new one. The upload pass
    /// needs it to replace the old server copy rather than leave a duplicate behind.
    /// </summary>
    Task<string?> GetSupersededServerIdAsync(Guid accountId, string folderName, string messageId);

    /// <summary>
    /// Records why this draft was not uploaded, which also stops it being retried (#637). Editing
    /// and saving the draft clears the reason and re-arms the upload. The reason is not always the
    /// server's: it also carries QuickMail's own, when the draft's saved copy cannot be read.
    /// </summary>
    Task MarkSendFailedAsync(Guid accountId, string folderName, string messageId, string reason);

    /// <summary>Drops a pending draft: uploaded, sent, or discarded by the user.</summary>
    Task DiscardAsync(Guid accountId, string folderName, string messageId);

    /// <summary>Pending drafts for one account, oldest first.</summary>
    Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid accountId);

    /// <summary>
    /// The account's Drafts folder name from the cached folder list, or null if the account has
    /// never synced its folders. Resolving it locally is what lets the first save of an offline
    /// session work at all: asking the server where Drafts is, is itself a network call.
    /// </summary>
    Task<string?> ResolveDraftsFolderNameAsync(Guid accountId);
}

/// <summary>
/// The result of a local draft save: the id the draft is now known by, and the server draft it
/// supersedes, if any. The caller needs both — the id to keep editing against, and the superseded
/// id to hand to the server so the upload replaces the old draft instead of duplicating it.
/// </summary>
/// <param name="MessageId">Local id of the stored draft.</param>
/// <param name="SupersededServerMessageId">Server draft this replaces, or null.</param>
public sealed record PendingDraftSave(string MessageId, string? SupersededServerMessageId);
