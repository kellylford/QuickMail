using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

#pragma warning disable CS0067 // events are part of IMailService; Graph raises them in PR 7
    public event Action<Guid, bool>? AccountReachabilityChanged;
    public event Action<Guid>? InboxNewMailDetected;
#pragma warning restore CS0067

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
        var folders = await _client.GetAllPagesAsync<GraphMailFolder>(
            Account(accountId),
            "/me/mailFolders?$top=100&$select=id,displayName,parentFolderId,totalItemCount,unreadItemCount", ct);

        var wellKnown = _wellKnownFolders.TryGetValue(accountId, out var map) ? map : EmptyWellKnown;

        return folders.Select(f =>
        {
            // Prefer the well-known folder ID resolved at connect (locale- and rename-proof);
            // fall back to display-name matching for anything not resolved.
            var kind = wellKnown.TryGetValue(f.Id, out var k) ? k : MapWellKnownFolder(f.DisplayName);
            return new MailFolderModel
            {
                AccountId = accountId,
                FullName = f.Id,             // Graph uses opaque folder IDs as the "folder name"
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
                   "&$select=id,subject,from,toRecipients,receivedDateTime,isRead,bodyPreview,hasAttachments";
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
    public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0); // real count in PR 5/6
    public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()); // bodyPreview is fetched inline

    // Graph delivers new-mail notifications via delta polling (PR 7), not IMAP IDLE. No-op here so a
    // mixed account list doesn't throw at startup.
    public void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) { }
    public void StopIdleWatchers() { }

    // ── Mutations / attachments / folder CRUD — PR 5/6 ───────────────────────────
    private static NotImplementedException NotYet(string member)
        => new($"GraphMailService.{member} lands in PR 5/6.");

    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => throw NotYet(nameof(MarkReadBatchAsync));
    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => throw NotYet(nameof(MoveToTrashAsync));
    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => throw NotYet(nameof(MoveToTrashBatchAsync));
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => throw NotYet(nameof(PermanentlyDeleteBatchAsync));
    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
        => throw NotYet(nameof(EmptyTrashAsync));
    public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
        => throw NotYet(nameof(AppendDraftAsync));
    // Graph's /sendMail auto-saves the sent message to the Sent folder, so there is nothing to append.
    public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
        => throw NotYet(nameof(DownloadAttachmentAsync));
    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => throw NotYet(nameof(CopyMessagesAsync));
    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => throw NotYet(nameof(MoveMessagesAsync));
    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default)
        => throw NotYet(nameof(CreateFolderAsync));
    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => throw NotYet(nameof(DeleteFolderAsync));
    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default)
        => throw NotYet(nameof(RenameFolderAsync));
    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default)
        => throw NotYet(nameof(CopyFolderAsync));

    public void Dispose() => _client.Dispose();

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
        IsRead = m.IsRead,
        Preview = m.BodyPreview ?? string.Empty,
        HasAttachments = m.HasAttachments,
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
