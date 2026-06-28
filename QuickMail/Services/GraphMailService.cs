using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services.Graph;

namespace QuickMail.Services;

/// <summary>
/// <see cref="IMailService"/> implementation backed by Microsoft Graph (v1.0). PR 4 implements the
/// read path (connect, folders, summaries, detail, mark-read, status); mutations, attachment
/// download, folder CRUD, and change notifications throw and are lit up in PRs 5–7.
/// </summary>
public class GraphMailService : IMailService
{
    private readonly GraphClient _client;
    private readonly IConfigService? _config;

    /// <summary>The shared Graph HTTP client, reused by <see cref="GraphChangeNotifier"/> for delta polling.</summary>
    internal GraphClient Client => _client;

    // accountId -> the connected AccountModel, so per-accountId calls can acquire the right token.
    private readonly ConcurrentDictionary<Guid, AccountModel> _accounts = new();

    // accountId -> { folder id -> special kind }, resolved at connect from Graph's well-known folder
    // names so special-folder detection is locale- and rename-proof (see Issue #61).
    private readonly ConcurrentDictionary<Guid, Dictionary<string, SpecialFolderKind>> _wellKnownFolders = new();

    private static readonly Dictionary<string, SpecialFolderKind> EmptyWellKnown = new();

    // Graph well-known folder names (stable across locales) -> our SpecialFolderKind.
    private static readonly (string Name, SpecialFolderKind Kind)[] WellKnownFolders =
    {
        ("inbox",        SpecialFolderKind.Inbox),
        ("sentitems",    SpecialFolderKind.Sent),
        ("drafts",       SpecialFolderKind.Drafts),
        ("deleteditems", SpecialFolderKind.Trash),
        ("junkemail",    SpecialFolderKind.Junk),
    };

    public GraphMailService(IOAuthService oauth, IConfigService? config = null, HttpClient? http = null)
    {
        _client = new GraphClient(oauth, http);
        _config = config;
    }

    // ── Connect ────────────────────────────────────────────────────────────────────
    public async Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
    {
        var me = await _client.GetAsync<GraphMe>(account, "/me?$select=id,userPrincipalName", ct);
        if (string.IsNullOrEmpty(me?.UserPrincipalName))
            throw new InvalidOperationException("Graph /me returned no userPrincipalName.");
        if (!string.Equals(account.Username, me.UserPrincipalName, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Log($"GraphMailService: token UPN {me.UserPrincipalName} differs from account.Username {account.Username}; updating.");
            account.Username = me.UserPrincipalName;
        }
        var wellKnown = await ResolveWellKnownFolderIdsAsync(account, ct);

        // Register the account only after every connect-time fetch succeeds, so a cancelled or
        // failed connect never leaves a half-registered account — one that passes the Account()
        // guard but has no well-known folder map, which would classify every folder as None.
        _wellKnownFolders[account.Id] = wellKnown;
        _accounts[account.Id] = account;
        LogService.Log($"GraphMailService: connected for {account.AccountLabel} ({account.Username}).");
    }

    /// <summary>
    /// Resolves the stable IDs of the well-known folders (Inbox, Sent, Drafts, Deleted, Junk) by
    /// their Graph well-known names, so detection survives localized display names and user renames.
    /// A folder that doesn't exist (e.g. no Junk) is simply skipped.
    /// </summary>
    private async Task<Dictionary<string, SpecialFolderKind>> ResolveWellKnownFolderIdsAsync(AccountModel account, CancellationToken ct)
    {
        var lookups = WellKnownFolders.Select(async wk =>
        {
            try
            {
                var f = await _client.GetAsync<GraphMailFolder>(account, $"/me/mailFolders/{wk.Name}?$select=id", ct);
                return (Id: f?.Id, wk.Kind);
            }
            catch (OperationCanceledException)
            {
                throw; // propagate cancellation — a cancelled connect must not look like success
            }
            catch (Exception ex)
            {
                // A 404 means the folder genuinely doesn't exist (e.g. no Junk). Other failures
                // (401/403/503) shouldn't silently degrade folder classification, so log at
                // warning level — not Debug — and skip just this one.
                LogService.Log($"GraphMailService: well-known folder '{wk.Name}' not resolved: {ex.Message}");
                return (Id: (string?)null, wk.Kind);
            }
        });

        var map = new Dictionary<string, SpecialFolderKind>();
        foreach (var (id, kind) in await Task.WhenAll(lookups))
            if (!string.IsNullOrEmpty(id)) map[id] = kind;
        return map;
    }

    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;

    private AccountModel Account(Guid accountId)
        => _accounts.TryGetValue(accountId, out var a)
            ? a
            : throw new InvalidOperationException($"Graph account {accountId} is not connected.");

    // ── Folders ──────────────────────────────────────────────────────────────────
    public async Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = Account(accountId);

        // Graph's /me/mailFolders returns ONLY top-level folders. Nested folders live under each
        // folder's /childFolders endpoint, so walk down into any folder that reports children —
        // otherwise sub-folders (e.g. Inbox/Projects/2026) never appear in the tree at all.
        const string select =
            "$select=id,displayName,parentFolderId,childFolderCount,totalItemCount,unreadItemCount";

        var all = await _client.GetAllPagesAsync<GraphMailFolder>(
            account, $"/me/mailFolders?$top=100&{select}", ct);

        var wellKnown = _wellKnownFolders.TryGetValue(accountId, out var map) ? map : EmptyWellKnown;
        // A folder deleted via Graph is moved under Deleted Items (a soft delete), so don't descend
        // into the Trash folder — surfacing those recoverable folders back in the tree makes a delete
        // look like it didn't take. We still show Deleted Items itself, just not its sub-folders.
        var trashId = wellKnown.FirstOrDefault(kv => kv.Value == SpecialFolderKind.Trash).Key;
        if (trashId == null)
            LogService.Log($"GraphMailService: Trash folder id unresolved for account {accountId}; " +
                           "cannot filter recoverable sub-folders out of the tree.");

        // Breadth-first descent: each iteration fetches the children of folders known to have them.
        var pending = new Queue<GraphMailFolder>(all.Where(f => f.ChildFolderCount > 0 && (trashId == null || f.Id != trashId)));
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            var children = await _client.GetAllPagesAsync<GraphMailFolder>(
                account, $"/me/mailFolders/{parent.Id}/childFolders?$top=100&{select}", ct);
            all.AddRange(children);
            foreach (var c in children.Where(c => c.ChildFolderCount > 0 && (trashId == null || c.Id != trashId)))
                pending.Enqueue(c);
        }

        return all.Select(f =>
        {
            // Prefer the well-known folder ID resolved at connect (locale- and rename-proof);
            // fall back to display-name matching for anything not resolved.
            var kind = wellKnown.TryGetValue(f.Id, out var k) ? k : MapWellKnownFolder(f.DisplayName);
            return new MailFolderModel
            {
                AccountId = accountId,
                FullName = f.Id,             // Graph uses opaque folder IDs as the "folder name"
                ParentId = f.ParentFolderId, // hierarchy is by parent reference, not a path separator
                DisplayName = f.DisplayName,
                UnreadCount = f.UnreadItemCount,
                MessageCount = f.TotalItemCount,
                Kind = kind,
                ExcludeFromAllMail = IsExcludedKind(kind),
            };
        }).ToList();
    }

    // ── Message summaries ────────────────────────────────────────────────────────
    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
        => FetchSummariesAsync(Account(accountId), folderName, maxMessages, since: null, ct);

    public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(
        Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
        => FetchSummariesAsync(Account(accountId), folderName, 999, since, ct);

    // Graph ignores sinceMessageId (its message IDs are non-numeric). Until delta polling lands in
    // PR 7, an incremental sync just re-fetches the recent N; the store/VM dedupe by MessageId.
    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(
        Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default)
        => FetchSummariesAsync(Account(accountId), folderName, initialCount, since: null, ct);

    private async Task<List<MailMessageSummary>> FetchSummariesAsync(
        AccountModel account, string folderName, int limit, DateTime? since, CancellationToken ct)
    {
        var top = limit > 0 ? Math.Min(limit, 999) : 500;
        var path = $"/me/mailFolders/{folderName}/messages?$top={top}&$orderby=receivedDateTime desc" +
                   "&$select=id,subject,from,toRecipients,receivedDateTime,isRead,bodyPreview,hasAttachments,flag";
        if (since.HasValue)
            path += $"&$filter=receivedDateTime ge {since.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";

        // Single page only — $top bounds the result like IMAP's initialCount (no nextLink following).
        var page = await _client.GetAsync<GraphCollection<GraphMessage>>(account, path, ct);
        return (page?.Value ?? new List<GraphMessage>())
            .Select(m => MapToSummary(m, account.Id, folderName)).ToList();
    }

    // ── Message detail ───────────────────────────────────────────────────────────
    public async Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        var path = $"/me/messages/{messageId}" +
                   "?$select=id,subject,body,from,toRecipients,ccRecipients,replyTo,internetMessageId,receivedDateTime,isRead,hasAttachments" +
                   "&$expand=attachments($select=id,name,contentType,size,isInline)";
        var m = await _client.GetAsync<GraphMessage>(Account(accountId), path, ct)
            ?? throw new InvalidOperationException($"Graph message {messageId} not found.");
        return MapToDetail(m, accountId, folderName);
    }

    // Graph GET does not set the Seen flag, so prefetch is identical to a normal detail fetch.
    public Task<MailMessageDetail> PrefetchMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => GetMessageDetailAsync(accountId, folderName, messageId, ct);

    public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => _client.PatchAsync(Account(accountId), $"/me/messages/{messageId}", new { isRead = true }, ct);

    public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default)
    {
        var status = flagged ? "flagged" : "notFlagged";
        return _client.PatchAsync(Account(accountId), $"/me/messages/{messageId}",
            new { flag = new { flagStatus = status } }, ct);
    }

    // ── Status / reconciliation ──────────────────────────────────────────────────
    public async Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        var f = await _client.GetAsync<GraphMailFolder>(
            Account(accountId), "/me/mailFolders/Inbox?$select=totalItemCount,unreadItemCount", ct);
        return (f?.TotalItemCount ?? 0, f?.UnreadItemCount ?? 0);
    }

    public async Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default)
    {
        var msgs = await _client.GetAllPagesAsync<GraphMessage>(
            Account(accountId), $"/me/mailFolders/{folderName}/messages?$select=id&$top=999", ct);
        return msgs.Select(m => m.Id).ToList();
    }

    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult<string?>("Drafts"); // Graph well-known folder name; valid as a folder ID

    // ── Safe no-ops (Graph has no persistent connection / IDLE; previews are native) ──
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
    public async Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default)
    {
        var f = await _client.GetAsync<GraphMailFolder>(
            Account(accountId), $"/me/mailFolders/{DeletedItemsFolderId}?$select=totalItemCount", ct);
        return f?.TotalItemCount ?? 0;
    }
    public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()); // bodyPreview is fetched inline

    // ── Mutations / attachments / folder CRUD ────────────────────────────────────
    // Graph well-known folder names double as folder ids in URLs.
    private const string DeletedItemsFolderId = "deleteditems";
    private const string RootFolderId         = "msgfolderroot";

    public async Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        var account = Account(accountId);
        foreach (var id in messageIds)
            await _client.PatchAsync(account, $"/me/messages/{id}", new { isRead = true }, ct);
    }

    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => MoveMessagesAsync(accountId, folderName, new[] { messageId }, DeletedItemsFolderId, ct);

    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => MoveMessagesAsync(accountId, folderName, messageIds, DeletedItemsFolderId, ct);

    public async Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        var account = Account(accountId);
        foreach (var id in messageIds)
            await _client.DeleteAsync(account, $"/me/messages/{id}", ct);
    }

    public async Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = Account(accountId);
        var msgs = await _client.GetAllPagesAsync<GraphMessage>(
            account, $"/me/mailFolders/{DeletedItemsFolderId}/messages?$select=id&$top=999", ct);
        foreach (var m in msgs)
            await _client.DeleteAsync(account, $"/me/messages/{m.Id}", ct);
        return msgs.Count;
    }

    public async Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
    {
        var account = Account(accountId);

        // POST the new draft to /me/messages (base64, text/plain) FIRST — Graph files it under Drafts
        // and returns the created message. Only then delete the old copy: Graph's DELETE is an
        // immediate, unrecoverable hard delete (unlike IMAP's deferred expunge), so a failure between
        // the two must leave the content recoverable (at worst a duplicate draft) rather than gone.
        var mime = MimeMessageBuilder.Build(draft, account, MimeMessageBuilder.AppUserAgent);
        var body = await MimeMessageBuilder.ToBase64BytesAsync(mime, ct);
        var created = await _client.PostRawReadAsync<GraphMessage>(account, "/me/messages", body, "text/plain", ct);
        var newId = created?.Id ?? string.Empty;

        if (!string.IsNullOrEmpty(replaceMessageId) && !string.IsNullOrEmpty(newId))
            await _client.DeleteAsync(account, $"/me/messages/{replaceMessageId}", ct);

        return newId;
    }

    // Graph's /sendMail auto-saves the sent message to the Sent folder, so there is nothing to append.
    public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
        // partSpecifier carries the Graph attachment id (set by MapToDetail).
        => _client.GetBytesAsync(Account(accountId), $"/me/messages/{messageId}/attachments/{partSpecifier}/$value", ct);

    public async Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
    {
        var account = Account(accountId);
        foreach (var id in messageIds)
            await _client.PostAsync(account, $"/me/messages/{id}/copy", new { destinationId = destinationFolder }, ct);
    }

    public async Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
    {
        var account = Account(accountId);
        foreach (var id in messageIds)
            await _client.PostAsync(account, $"/me/messages/{id}/move", new { destinationId = destinationFolder }, ct);
    }

    public async Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default)
    {
        var account = Account(accountId);
        // null parent = account root; otherwise create under the parent's childFolders collection.
        var path = string.IsNullOrEmpty(parentFolderName)
            ? "/me/mailFolders"
            : $"/me/mailFolders/{parentFolderName}/childFolders";
        await _client.PostAsync(account, path, new { displayName = name }, ct);
    }

    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default)
        // Graph moves a deleted folder to Deleted Items (recoverable), matching the IMAP safety behaviour.
        => _client.DeleteAsync(Account(accountId), $"/me/mailFolders/{folderName}", ct);

    public async Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default)
    {
        var account = Account(accountId);
        // Rename: set the display name. Only move when a new parent is actually requested — a
        // rename-only call (null newParentFolderName) must not POST a redundant move-to-root.
        await _client.PatchAsync(account, $"/me/mailFolders/{folderName}", new { displayName = newName }, ct);
        if (!string.IsNullOrEmpty(newParentFolderName))
            await _client.PostAsync(account, $"/me/mailFolders/{folderName}/move", new { destinationId = newParentFolderName }, ct);
    }

    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default)
    {
        // Graph copies the folder and all of its messages (and sub-folders) server-side in one call.
        var destination = string.IsNullOrEmpty(destinationParentName) ? RootFolderId : destinationParentName;
        return _client.PostAsync(Account(accountId), $"/me/mailFolders/{folderName}/copy", new { destinationId = destination }, ct);
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────────
    private static MailMessageSummary MapToSummary(GraphMessage m, Guid accountId, string folderName) => new()
    {
        MessageId = m.Id,
        AccountId = accountId,
        FolderName = folderName,
        From = m.From?.EmailAddress?.AsHeaderString() ?? string.Empty,
        To = JoinRecipients(m.ToRecipients),
        Subject = m.Subject ?? "(no subject)",
        Date = m.ReceivedDateTime,
        IsRead          = m.IsRead,
        Preview         = m.BodyPreview ?? string.Empty,
        HasAttachments  = m.HasAttachments,
        IsServerFlagged = m.Flag?.FlagStatus == "flagged",
    };

    private static MailMessageDetail MapToDetail(GraphMessage m, Guid accountId, string folderName)
    {
        var isHtml = string.Equals(m.Body?.ContentType, "html", StringComparison.OrdinalIgnoreCase);
        var attachments = (m.Attachments ?? new List<GraphAttachment>())
            .Where(a => !a.IsInline)
            .Select(a => new AttachmentModel
            {
                FileName = a.Name ?? "(attachment)",
                ContentType = a.ContentType ?? "application/octet-stream",
                FileSize = a.Size,
                PartSpecifier = a.Id, // Graph attachment id; used by DownloadAttachment in PR 5/6
            }).ToList();

        return new MailMessageDetail
        {
            MessageId = m.Id,
            AccountId = accountId,
            FolderName = folderName,
            From = m.From?.EmailAddress?.AsHeaderString() ?? string.Empty,
            To = JoinRecipients(m.ToRecipients),
            Cc = JoinRecipients(m.CcRecipients),
            ReplyTo = JoinRecipients(m.ReplyTo),
            Subject = m.Subject ?? "(no subject)",
            Date = m.ReceivedDateTime,
            IsRead = m.IsRead,
            InternetMessageId = m.InternetMessageId ?? string.Empty,
            PlainTextBody = isHtml ? string.Empty : (m.Body?.Content ?? string.Empty),
            HtmlBody = isHtml ? (m.Body?.Content ?? string.Empty) : string.Empty,
            Attachments = attachments,
        };
    }

    private static string JoinRecipients(List<GraphRecipient>? recipients)
        => string.Join(", ", (recipients ?? new List<GraphRecipient>())
            .Select(r => r.EmailAddress?.AsHeaderString() ?? string.Empty)
            .Where(s => s.Length > 0));

    private static SpecialFolderKind MapWellKnownFolder(string displayName) => displayName.Trim().ToLowerInvariant() switch
    {
        "inbox" => SpecialFolderKind.Inbox,
        "sent items" or "sent" => SpecialFolderKind.Sent,
        "drafts" => SpecialFolderKind.Drafts,
        "deleted items" or "trash" => SpecialFolderKind.Trash,
        "junk email" or "junk" => SpecialFolderKind.Junk,
        _ => SpecialFolderKind.None,
    };

    private static bool IsExcludedKind(SpecialFolderKind kind)
        => kind is SpecialFolderKind.Sent or SpecialFolderKind.Drafts
            or SpecialFolderKind.Trash or SpecialFolderKind.Junk;
}
