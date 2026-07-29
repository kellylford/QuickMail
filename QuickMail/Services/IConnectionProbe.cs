using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickMail.Services;

/// <summary>Outcome of an independent reachability check against a mail account.</summary>
/// <param name="Reachable">True if a brand-new connection completed a full round-trip.</param>
/// <param name="ElapsedMs">How long the whole check took.</param>
/// <param name="Detail">Server/inbox facts on success, or the full error chain on failure.</param>
public sealed record ProbeResult(bool Reachable, long ElapsedMs, string Detail)
{
    public override string ToString() =>
        Reachable
            ? $"reachable in {ElapsedMs} ms — {Detail}"
            : $"not reachable after {ElapsedMs} ms — {Detail}";
}

/// <summary>
/// Verifies an account's real reachability using a connection that shares nothing with the
/// application's pools, watchers, or cached state.
///
/// This exists to settle one question the current code cannot answer: when the account list shows
/// an account as disconnected, is the account actually unreachable, or is the displayed status
/// simply stale? Those two have completely different fixes and today look identical from outside.
/// </summary>
public interface IConnectionProbe
{
    /// <summary>
    /// Opens a fresh connection, authenticates, selects INBOX, NOOPs, and logs out. Never reuses a
    /// pooled client and never mutates pool or watcher state, so the result reflects the server and
    /// credentials only. Does not throw for connection failures — those are the answer, and are
    /// returned in the result.
    /// </summary>
    Task<ProbeResult> ProbeAccountAsync(Guid accountId, CancellationToken ct = default);
}
