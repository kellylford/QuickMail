using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// <see cref="IMailService"/> implementation that holds one backend per account and dispatches
/// every call to the right backend based on accountId. Consumers (MainViewModel, SyncService,
/// RuleService) see a single IMailService surface and are unaware that more than one backend exists.
///
/// In v0.7 (PR 3) the only backend is <see cref="ImapMailService"/>, so every account registers to
/// it and behavior is identical to today. PR 4 adds a Graph backend and routes Graph accounts to it.
/// </summary>
public class MailServiceRouter : IMailService
{
    private readonly ConcurrentDictionary<Guid, IMailService> _byAccount = new();
    private readonly List<IMailService> _allBackends; // ordered, for event aggregation + fan-out
    private readonly IMailService _defaultBackend;             // fallback for unregistered accounts

    public MailServiceRouter(IEnumerable<IMailService> backends)
    {
        _allBackends = backends.ToList();
        if (_allBackends.Count == 0)
            throw new ArgumentException("MailServiceRouter requires at least one backend.", nameof(backends));
        _defaultBackend = _allBackends[0];
        foreach (var b in _allBackends)
        {
            b.InboxNewMailDetected += OnInnerInboxNewMail;
            b.AccountReachabilityChanged += OnInnerAccountReachabilityChanged;
        }
    }

    /// <summary>Bind an account to a specific backend. Called once at account-load time and once per Add Account. Idempotent.</summary>
    public void RegisterAccount(Guid accountId, IMailService backend) => _byAccount[accountId] = backend;

    public void UnregisterAccount(Guid accountId) => _byAccount.TryRemove(accountId, out _);

    /// <summary>
    /// Resolves the backend for an account. Explicitly-registered accounts use their bound backend;
    /// anything else falls back to the first (default) backend. In v0.7 (PR 3) the only backend is
    /// IMAP, so runtime-added accounts route correctly via the fallback without plumbing the router
    /// through the VM layer. PR 4 (multiple backends) must call <see cref="RegisterAccount"/> for
    /// Graph accounts at add time, since the fallback assumes IMAP.
    /// </summary>
    private IMailService For(Guid accountId)
        => _byAccount.TryGetValue(accountId, out var b) ? b : _defaultBackend;

    private IMailService For(AccountModel account) => For(account.Id);

    // ── Event aggregation ────────────────────────────────────────────────────────
    public event Action<Guid>? InboxNewMailDetected;
    public event Action<Guid, bool>? AccountReachabilityChanged;

    private void OnInnerInboxNewMail(Guid accountId) => InboxNewMailDetected?.Invoke(accountId);
    private void OnInnerAccountReachabilityChanged(Guid accountId, bool isReachable) => AccountReachabilityChanged?.Invoke(accountId, isReachable);

    // ── Per-account delegation ─────────────────────────────────────────────────────
    public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
        => For(account).ConnectAsync(account, password, ct);

    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).DisconnectAsync(accountId, ct);

    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).GetFoldersAsync(accountId, ct);

    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
        => For(accountId).GetMessageSummariesAsync(accountId, folderName, maxMessages, ct);

    public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
        => For(accountId).GetMessagesSinceDateAsync(accountId, folderName, since, ct);

    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default)
        => For(accountId).GetMessagesSinceAsync(accountId, folderName, sinceMessageId, initialCount, ct);

    public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => For(accountId).GetMessageDetailAsync(accountId, folderName, messageId, ct);

    public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => For(accountId).PrefetchMessageDetailAsync(accountId, folderName, messageId, ct);

    public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => For(accountId).MarkReadAsync(accountId, folderName, messageId, ct);

    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => For(accountId).MarkReadBatchAsync(accountId, folderName, messageIds, ct);

    public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default)
        => For(accountId).SetMessageFlaggedAsync(accountId, folderName, messageId, flagged, ct);

    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => For(accountId).MoveToTrashAsync(accountId, folderName, messageId, ct);

    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => For(accountId).MoveToTrashBatchAsync(accountId, folderName, messageIds, ct);

    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => For(accountId).PermanentlyDeleteBatchAsync(accountId, folderName, messageIds, ct);

    public Task NoOpAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).NoOpAsync(accountId, ct);

    public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).CountTrashMessagesAsync(accountId, ct);

    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).EmptyTrashAsync(accountId, ct);

    public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => For(accountId).GetFolderMessageIdsAsync(accountId, folderName, ct);

    public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default)
        => For(accountId).FetchPreviewsAsync(accountId, folderName, messageIds, maxLines, ct);

    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => For(accountId).PollAsync(accountId, folderName, ct);

    public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).GetInboxStatusAsync(accountId, ct);

    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).FindDraftsFolderNameAsync(accountId, ct);

    public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
        => For(accountId).AppendDraftAsync(accountId, draft, replaceMessageId, ct);

    public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default)
        => For(accountId).AppendToSentAsync(accountId, sent, ct);

    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
        => For(accountId).DownloadAttachmentAsync(accountId, folderName, messageId, partSpecifier, ct);

    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => For(accountId).CopyMessagesAsync(accountId, folderName, messageIds, destinationFolder, ct);

    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => For(accountId).MoveMessagesAsync(accountId, folderName, messageIds, destinationFolder, ct);

    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default)
        => For(accountId).CreateFolderAsync(accountId, parentFolderName, name, ct);

    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => For(accountId).DeleteFolderAsync(accountId, folderName, ct);

    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default)
        => For(accountId).RenameFolderAsync(accountId, folderName, newName, newParentFolderName, ct);

    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default)
        => For(accountId).CopyFolderAsync(accountId, folderName, destinationParentName, ct);

    // ── Account-list-level operations: fan out to every backend ────────────────────
    // Each backend starts watchers only for the accounts it can actually connect; passing the full
    // list to all backends is correct because an account is only ever registered to one backend.
    public void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default)
    {
        // Give each backend only the accounts registered to it (unregistered accounts fall back to
        // the default backend), so a backend never spins up watcher tasks for accounts it doesn't own.
        var byBackend = new Dictionary<IMailService, List<AccountModel>>();
        foreach (var account in accounts)
        {
            var backend = _byAccount.TryGetValue(account.Id, out var b) ? b : _defaultBackend;
            if (!byBackend.TryGetValue(backend, out var list))
                byBackend[backend] = list = new List<AccountModel>();
            list.Add(account);
        }

        foreach (var b in _allBackends)
            b.StartIdleWatchers(byBackend.TryGetValue(b, out var list) ? list : new List<AccountModel>(), ct);
    }

    public void StopIdleWatchers()
    {
        foreach (var b in _allBackends)
            b.StopIdleWatchers();
    }

    public void Dispose()
    {
        foreach (var b in _allBackends)
        {
            b.InboxNewMailDetected -= OnInnerInboxNewMail;
            b.AccountReachabilityChanged -= OnInnerAccountReachabilityChanged;
            b.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
