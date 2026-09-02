using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>What one drain of the Outbox did.</summary>
public sealed record OutboxFlushResult(int Sent, int DraftsUploaded, int Failed, int Skipped)
{
    public static readonly OutboxFlushResult Nothing = new(0, 0, 0, 0);

    /// <summary>True when at least one item reached a final outcome this drain.</summary>
    public bool Any => Sent + DraftsUploaded + Failed > 0;
}

/// <summary>
/// The local queue of mail written while the server could not be reached (issue #637): drafts
/// waiting to upload and messages waiting to send. The compose window enqueues; a drain runs when
/// connectivity returns, on the fallback sync tick, and on the Send Outbox Now command.
/// </summary>
public interface IOutboxService
{
    /// <summary>False in <c>--online</c> mode, where there is no local store to queue into. Enqueue throws then.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Stores (or, with <paramref name="existingId"/>, replaces) a draft to upload later. Returns the
    /// row id; pass it back on the next save so repeated saves keep one row.
    /// </summary>
    Task<string> EnqueueDraftAsync(ComposeModel compose, Guid accountId, string? existingId, CancellationToken ct = default);

    /// <summary>
    /// Stores (or replaces) a message to send later. <paramref name="existingId"/> may name a draft
    /// row — the row becomes a send, so a queued draft never outlives the message it became.
    /// </summary>
    Task<string> EnqueueSendAsync(ComposeModel compose, Guid accountId, string? existingId, CancellationToken ct = default);

    /// <summary>Every queued item across all accounts, newest first.</summary>
    Task<IReadOnlyList<OutboxItem>> ListAsync(CancellationToken ct = default);

    Task<OutboxItem?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>The stored compose model, attachments included, with <see cref="ComposeModel.OutboxId"/> set. Null when the row is gone.</summary>
    Task<ComposeModel?> LoadComposeAsync(string id, CancellationToken ct = default);

    /// <summary>Removes a row. Returns false when it was already gone.</summary>
    Task<bool> RemoveAsync(string id, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Drains every eligible item: pending rows whose backoff has elapsed, on accounts not known to
    /// be offline. <paramref name="force"/> ignores the backoff, the connectivity check and retries
    /// failed rows too — the user asked. A drain already in progress makes this one return at once
    /// with <see cref="OutboxFlushResult.Skipped"/> set.
    /// </summary>
    Task<OutboxFlushResult> FlushAsync(bool force = false, CancellationToken ct = default);

    /// <summary>As <see cref="FlushAsync"/>, for one account.</summary>
    Task<OutboxFlushResult> FlushAccountAsync(Guid accountId, bool force = false, CancellationToken ct = default);

    /// <summary>Raised (on whatever thread did the work) whenever rows are added, removed, or change state.</summary>
    event Action? Changed;

    /// <summary>Raised once per drain that reached at least one final outcome — never per item.</summary>
    event Action<OutboxFlushResult>? FlushCompleted;
}
