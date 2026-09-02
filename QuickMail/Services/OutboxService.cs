using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// See <see cref="IOutboxService"/>. Rows live in the local store; this class owns the drain: one at
/// a time, oldest first, each item marked Sending while it is worked on so the Outbox folder shows
/// progress, and each failure classified through <see cref="ConnectionFailure"/> so a dead network
/// keeps a row pending with backoff while a server rejection parks it as Failed for the user.
/// </summary>
public sealed class OutboxService : IOutboxService, IDisposable
{
    private static readonly TimeSpan SendTimeout       = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DraftTimeout      = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SentAppendTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TrashTimeout      = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconnectDebounce = TimeSpan.FromSeconds(2);
    private const int MaxBackoffMinutes = 32;

    private readonly ILocalStoreService _store;
    private readonly IMailService _mail;
    private readonly ISendMailService _send;
    private readonly IAccountService _accounts;
    private readonly ICredentialService _credentials;
    private readonly IConnectivityService? _connectivity;
    private readonly bool _onlineMode;

    private readonly SemaphoreSlim _drain = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _reconnectDebounce;
    private bool _disposed;

    public OutboxService(
        ILocalStoreService store,
        IMailService mail,
        ISendMailService send,
        IAccountService accounts,
        ICredentialService credentials,
        IConnectivityService? connectivity,
        bool onlineMode)
    {
        _store        = store;
        _mail         = mail;
        _send         = send;
        _accounts     = accounts;
        _credentials  = credentials;
        _connectivity = connectivity;
        _onlineMode   = onlineMode;

        if (_connectivity != null)
        {
            _connectivity.OnlineChanged        += OnOnlineChanged;
            _connectivity.AccountOnlineChanged += OnAccountOnlineChanged;
        }
    }

    public bool IsAvailable => !_onlineMode;

    public event Action? Changed;
    public event Action<OutboxFlushResult>? FlushCompleted;

    // ── Enqueue ─────────────────────────────────────────────────────────────────

    public Task<string> EnqueueDraftAsync(ComposeModel compose, Guid accountId, string? existingId, CancellationToken ct = default)
        => EnqueueAsync(compose, accountId, existingId, OutboxKind.Draft, ct);

    public Task<string> EnqueueSendAsync(ComposeModel compose, Guid accountId, string? existingId, CancellationToken ct = default)
        => EnqueueAsync(compose, accountId, existingId, OutboxKind.Send, ct);

    private async Task<string> EnqueueAsync(ComposeModel compose, Guid accountId, string? existingId, OutboxKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compose);
        ThrowIfUnavailable();
        ct.ThrowIfCancellationRequested();

        OutboxItem? item = null;
        if (!string.IsNullOrEmpty(existingId))
            item = await _store.LoadOutboxItemAsync(existingId);

        // A replaced row keeps its id and creation time; everything else is re-derived from the
        // compose so the listing never shows a stale subject. State resets to Pending: a Failed row
        // the user reopened, edited and saved again deserves a fresh attempt.
        item ??= new OutboxItem { Id = OutboxItem.NewId(), CreatedUtc = DateTimeOffset.UtcNow };
        item.AccountId       = accountId;
        item.Kind            = kind;
        item.State           = OutboxState.Pending;
        item.Attempts        = 0;
        item.LastError       = null;
        item.NextAttemptUtc  = null;
        item.ReplaceDraftId  = string.IsNullOrEmpty(compose.DraftMessageId) ? null : compose.DraftMessageId;
        item.DraftFolderName = compose.DraftFolderName;
        item.Subject         = compose.Subject ?? string.Empty;
        item.To              = compose.To ?? string.Empty;
        item.Cc              = compose.Cc ?? string.Empty;
        item.Bcc             = compose.Bcc ?? string.Empty;

        await _store.UpsertOutboxItemAsync(item, compose);
        RaiseChanged();
        return item.Id;
    }

    // ── Read / remove ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<OutboxItem>> ListAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return [];
        return await _store.LoadOutboxItemsAsync();
    }

    public Task<OutboxItem?> GetAsync(string id, CancellationToken ct = default)
        => IsAvailable ? _store.LoadOutboxItemAsync(id) : Task.FromResult<OutboxItem?>(null);

    public Task<ComposeModel?> LoadComposeAsync(string id, CancellationToken ct = default)
        => IsAvailable ? _store.LoadOutboxComposeAsync(id) : Task.FromResult<ComposeModel?>(null);

    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        var existed = await _store.LoadOutboxItemAsync(id) != null;
        await _store.DeleteOutboxItemAsync(id);
        if (existed) RaiseChanged();
        return existed;
    }

    public Task<int> CountAsync(CancellationToken ct = default)
        => IsAvailable ? _store.CountOutboxItemsAsync() : Task.FromResult(0);

    // ── Drain ───────────────────────────────────────────────────────────────────

    public Task<OutboxFlushResult> FlushAsync(bool force = false, CancellationToken ct = default)
        => FlushCoreAsync(null, force, ct);

    public Task<OutboxFlushResult> FlushAccountAsync(Guid accountId, bool force = false, CancellationToken ct = default)
        => FlushCoreAsync(accountId, force, ct);

    private async Task<OutboxFlushResult> FlushCoreAsync(Guid? onlyAccount, bool force, CancellationToken ct)
    {
        if (!IsAvailable || _disposed) return OutboxFlushResult.Nothing;

        // A drain already running will pick up anything eligible; a second one would race it for
        // the same rows. Report that this call did nothing rather than queue behind it.
        if (!await _drain.WaitAsync(0, ct))
            return OutboxFlushResult.Nothing with { Skipped = 1 };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        var token = linked.Token;
        int sent = 0, drafts = 0, failed = 0, skipped = 0;
        try
        {
            var items = await _store.LoadOutboxItemsAsync();
            var now = DateTimeOffset.UtcNow;
            // Oldest first, so messages leave in the order they were written.
            foreach (var item in Enumerable.Reverse(items))
            {
                token.ThrowIfCancellationRequested();
                if (onlyAccount.HasValue && item.AccountId != onlyAccount.Value) continue;
                if (!IsEligible(item, now, force)) { skipped++; continue; }

                var outcome = await ProcessOneAsync(item, token);
                switch (outcome)
                {
                    case Outcome.Sent:          sent++;   break;
                    case Outcome.DraftUploaded: drafts++; break;
                    case Outcome.Failed:        failed++; break;
                    default:                    skipped++; break;
                }
            }
        }
        finally
        {
            _drain.Release();
        }

        var result = new OutboxFlushResult(sent, drafts, failed, skipped);
        RaiseChanged();
        if (result.Any)
        {
            try { FlushCompleted?.Invoke(result); }
            catch (Exception ex) { LogService.Log("Outbox: FlushCompleted handler", ex); }
        }
        return result;
    }

    private bool IsEligible(OutboxItem item, DateTimeOffset now, bool force)
    {
        if (force) return item.State != OutboxState.Sending;
        if (item.State != OutboxState.Pending) return false;
        if (item.NextAttemptUtc is { } next && next > now) return false;
        if (_connectivity != null && !_connectivity.IsAccountOnline(item.AccountId)) return false;
        return true;
    }

    private enum Outcome { Sent, DraftUploaded, Failed, Deferred, Vanished }

    private async Task<Outcome> ProcessOneAsync(OutboxItem item, CancellationToken token)
    {
        var compose = await _store.LoadOutboxComposeAsync(item.Id);
        if (compose == null) return Outcome.Vanished;

        await _store.UpdateOutboxStateAsync(item.Id, OutboxState.Sending, item.Attempts, item.LastError, null);
        RaiseChanged();

        try
        {
            if (item.Kind == OutboxKind.Draft)
            {
                await UploadDraftAsync(item, compose, token);
                await _store.DeleteOutboxItemAsync(item.Id);
                LogService.Log($"Outbox: draft {item.Id} uploaded");
                return Outcome.DraftUploaded;
            }

            await SendAsync(item, compose, token);
            await _store.DeleteOutboxItemAsync(item.Id);
            LogService.Log($"Outbox: message {item.Id} sent");
            return Outcome.Sent;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutdown or the caller gave up: leave the row exactly as it was.
            await _store.UpdateOutboxStateAsync(item.Id, OutboxState.Pending, item.Attempts, item.LastError, item.NextAttemptUtc);
            throw;
        }
        catch (PermanentOutboxFailure ex)
        {
            await _store.UpdateOutboxStateAsync(item.Id, OutboxState.Failed, item.Attempts + 1, ex.Message, null);
            LogService.Log($"Outbox: {item.Kind} {item.Id} failed permanently: {ex.Message}");
            return Outcome.Failed;
        }
        catch (Exception ex)
        {
            if (ConnectionFailure.IsConnectionFailure(ex, token))
            {
                var attempts = item.Attempts + 1;
                var next = DateTimeOffset.UtcNow + Backoff(attempts);
                await _store.UpdateOutboxStateAsync(item.Id, OutboxState.Pending, attempts, ex.Message, next);
                LogService.Log($"Outbox: {item.Kind} {item.Id} deferred (attempt {attempts}, next {next:u})", ex);
                return Outcome.Deferred;
            }

            await _store.UpdateOutboxStateAsync(item.Id, OutboxState.Failed, item.Attempts + 1, ex.Message, null);
            LogService.Log($"Outbox: {item.Kind} {item.Id} failed", ex);
            return Outcome.Failed;
        }
    }

    private async Task UploadDraftAsync(OutboxItem item, ComposeModel compose, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(DraftTimeout);

        var folder = item.DraftFolderName ?? await _mail.FindDraftsFolderNameAsync(item.AccountId, cts.Token);
        if (folder == null)
            throw new PermanentOutboxFailure("No Drafts folder found on this account.");

        compose.DraftMessageId  = item.ReplaceDraftId;
        compose.DraftFolderName = folder;
        await _mail.AppendDraftAsync(item.AccountId, compose, item.ReplaceDraftId, cts.Token);
    }

    private async Task SendAsync(OutboxItem item, ComposeModel compose, CancellationToken token)
    {
        var account = _accounts.LoadAccounts().FirstOrDefault(a => a.Id == item.AccountId)
            ?? throw new PermanentOutboxFailure("The account this message was written from no longer exists.");

        var password = _credentials.GetPassword(account.Id);
        if (string.IsNullOrEmpty(password) && account.AuthType == AuthType.Password)
            throw new PermanentOutboxFailure("No password stored for this account.");

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            cts.CancelAfter(SendTimeout);
            await _send.SendAsync(compose, account, password, cts.Token);
        }

        // The same two best-effort steps ComposeViewModel.SendAsync performs after a live send:
        // file a copy in Sent, and trash the server draft this message grew out of. Neither can
        // un-send the message, so neither is allowed to fail the row.
        try
        {
            using var sentCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            sentCts.CancelAfter(SentAppendTimeout);
            await _mail.AppendToSentAsync(account.Id, compose, sentCts.Token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            LogService.Log($"Outbox: {item.Id} sent, but appending to Sent failed", ex);
        }

        if (!string.IsNullOrEmpty(item.ReplaceDraftId) && !string.IsNullOrEmpty(item.DraftFolderName))
        {
            try
            {
                using var delCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                delCts.CancelAfter(TrashTimeout);
                await _mail.MoveToTrashAsync(account.Id, item.DraftFolderName, item.ReplaceDraftId, delCts.Token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                LogService.Log($"Outbox: {item.Id} sent, but trashing its server draft failed", ex);
            }
        }
    }

    internal static TimeSpan Backoff(int attempts)
        => TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(1, attempts)), MaxBackoffMinutes));

    // ── Connectivity triggers ───────────────────────────────────────────────────

    private void OnOnlineChanged(bool online)
    {
        if (online) ScheduleReconnectFlush();
    }

    private void OnAccountOnlineChanged(Guid accountId, bool online)
    {
        if (online) ScheduleReconnectFlush();
    }

    // Several accounts coming back within a second or two would otherwise start several drains; the
    // first wins the semaphore and the rest report Skipped, which is harmless but noisy in the log.
    private void ScheduleReconnectFlush()
    {
        if (_disposed || !IsAvailable) return;
        var previous = Interlocked.Exchange(ref _reconnectDebounce, null);
        previous?.Cancel();
        previous?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _reconnectDebounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ReconnectDebounce, cts.Token);
                await FlushAsync(force: false, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogService.Log("Outbox: flush after reconnect", ex); }
        });
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────

    private void ThrowIfUnavailable()
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The Outbox is not available in --online mode: there is no local store to keep the message in.");
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { LogService.Log("Outbox: Changed handler", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connectivity != null)
        {
            _connectivity.OnlineChanged        -= OnOnlineChanged;
            _connectivity.AccountOnlineChanged -= OnAccountOnlineChanged;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        _reconnectDebounce?.Dispose();
        _drain.Dispose();
    }

    /// <summary>The server (or the account setup) has settled the matter; retrying will not help.</summary>
    private sealed class PermanentOutboxFailure : Exception
    {
        public PermanentOutboxFailure() { }
        public PermanentOutboxFailure(string message) : base(message) { }
        public PermanentOutboxFailure(string message, Exception innerException) : base(message, innerException) { }
    }
}
