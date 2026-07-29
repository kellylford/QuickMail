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
public class MailServiceRouter : IMailService, IConnectionProbe
{
    private readonly ConcurrentDictionary<Guid, IMailService> _byAccount = new();

    /// <summary>
    /// Accounts bound by <see cref="ConnectAsync"/> rather than by <see cref="RegisterAccount"/>,
    /// so <see cref="DisconnectAsync"/> knows which bindings are its to release. Test Connection
    /// mints a fresh Guid for every probe (AccountEditorViewModel.BuildProbeAccount), and nothing
    /// in production ever called UnregisterAccount — so without this the table grew by one entry
    /// per press of the button, for the life of the process.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _boundByConnect = new();
    private readonly List<IMailService> _allBackends; // ordered, for event aggregation + fan-out
    private readonly IMailService _defaultBackend;             // fallback for unregistered accounts

    /// <summary>
    /// Picks a backend for an account that was never registered, from the account itself. Without
    /// it, an unregistered account falls back to <see cref="_defaultBackend"/> — which is IMAP — so
    /// Test Connection on a not-yet-created Graph account (its probe uses a throwaway Guid) would
    /// silently probe IMAP against a host the Graph path had just cleared, and report the resulting
    /// IMAP error as a Microsoft 365 failure.
    /// </summary>
    private readonly Func<AccountModel, IMailService>? _backendSelector;

    public MailServiceRouter(IEnumerable<IMailService> backends,
                             Func<AccountModel, IMailService>? backendSelector = null)
    {
        _allBackends = backends.ToList();
        if (_allBackends.Count == 0)
            throw new ArgumentException("MailServiceRouter requires at least one backend.", nameof(backends));
        _defaultBackend = _allBackends[0];
        _backendSelector = backendSelector;
    }

    /// <summary>Bind an account to a specific backend. Called once at account-load time and once per Add Account. Idempotent.</summary>
    public void RegisterAccount(Guid accountId, IMailService backend)
    {
        _byAccount[accountId] = backend;
        // A real account's binding outlives any single connection, so it is not the next
        // Disconnect's to release — even if a probe happened to bind this id first.
        _boundByConnect.TryRemove(accountId, out _);
    }

    public void UnregisterAccount(Guid accountId)
    {
        _byAccount.TryRemove(accountId, out _);
        _boundByConnect.TryRemove(accountId, out _);
    }

    /// <summary>
    /// How many accounts are currently bound to a backend. Diagnostics only — it exists so the
    /// probe-account leak this class used to have stays testable.
    /// </summary>
    public int BoundAccountCount => _byAccount.Count;

    /// <summary>
    /// Resolves the backend for an account. Explicitly-registered accounts use their bound backend;
    /// anything else falls back to the first (default) backend. In v0.7 (PR 3) the only backend is
    /// IMAP, so runtime-added accounts route correctly via the fallback without plumbing the router
    /// through the VM layer. PR 4 (multiple backends) must call <see cref="RegisterAccount"/> for
    /// Graph accounts at add time, since the fallback assumes IMAP.
    /// </summary>
    private IMailService For(Guid accountId)
        => _byAccount.TryGetValue(accountId, out var b) ? b : _defaultBackend;

    /// <inheritdoc />
    /// <remarks>
    /// Routes the probe to the account's own backend. Getting this wrong is not a harmless
    /// inaccuracy: the first live run of the diagnostics asked the IMAP backend about a Microsoft
    /// Graph account and reported a perfectly healthy account as being in the wrong state. A probe
    /// that reports false alarms is worse than no probe, because it burns the user's trust in the
    /// one tool meant to settle the question.
    /// </remarks>
    public Task<ProbeResult> ProbeAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        // Deliberately no _defaultBackend fallback: an unregistered account routed to IMAP by
        // default is exactly the mistake described above.
        if (!_byAccount.TryGetValue(accountId, out var backend))
        {
            return Task.FromResult(new ProbeResult(ProbeOutcome.NotSupported, 0,
                "account is not bound to a mail backend, so there is nothing to test"));
        }

        if (backend is IConnectionProbe probe)
            return probe.ProbeAccountAsync(accountId, ct);

        return Task.FromResult(new ProbeResult(ProbeOutcome.NotSupported, 0,
            $"the {backend.GetType().Name} backend does not support connection testing"));
    }

    /// <summary>
    /// Resolves the backend for an account we hold the whole model for. A registered account keeps
    /// its bound backend; otherwise the selector reads <see cref="AccountModel.BackendKind"/>, which
    /// is strictly better than assuming IMAP.
    /// </summary>
    private IMailService For(AccountModel account)
    {
        if (_byAccount.TryGetValue(account.Id, out var bound)) return bound;
        return _backendSelector?.Invoke(account) ?? _defaultBackend;
    }

    // ── Per-account delegation ─────────────────────────────────────────────────────
    public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
    {
        var backend = For(account);
        // Bind it, so the Disconnect that follows (which only has the Guid) reaches the same
        // backend this connect used. Note that it was NOT already registered — the binding is this
        // connection's, and the matching Disconnect gives it back.
        if (_byAccount.TryAdd(account.Id, backend))
            _boundByConnect.TryAdd(account.Id, 0);
        return backend.ConnectAsync(account, password, ct);
    }

    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default)
    {
        var backend = For(accountId);
        // Release a binding ConnectAsync created for an account nobody registered — a Test
        // Connection probe, whose Guid is thrown away the moment the probe ends. Registered
        // accounts keep theirs: they disconnect and reconnect for the whole run of the app.
        if (_boundByConnect.TryRemove(accountId, out _))
            _byAccount.TryRemove(accountId, out _);
        return backend.DisconnectAsync(accountId, ct);
    }

    public bool IsConnected(Guid accountId) => For(accountId).IsConnected(accountId);

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

    public void Dispose()
    {
        foreach (var b in _allBackends)
            b.Dispose();
        GC.SuppressFinalize(this);
    }
}
