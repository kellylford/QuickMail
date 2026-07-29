using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickMail.Services;

/// <summary>
/// What an independent reachability check concluded.
/// </summary>
public enum ProbeOutcome
{
    /// <summary>A fresh connection completed a full round-trip against the server.</summary>
    Reachable,

    /// <summary>The server could not be reached, or rejected the connection.</summary>
    Unreachable,

    /// <summary>
    /// This account's backend cannot be probed, so nothing is known either way.
    ///
    /// This state exists because collapsing it into <see cref="Unreachable"/> produced a false
    /// alarm the first time the diagnostics were used: an IMAP-only probe was asked about a
    /// Microsoft Graph account, answered "not registered with the IMAP service", and the window
    /// reported a healthy account as being in the wrong state. A probe that cannot answer must say
    /// so rather than guess.
    /// </summary>
    NotSupported,
}

/// <summary>Outcome of an independent reachability check against a mail account.</summary>
/// <param name="Outcome">Reachable, unreachable, or not answerable for this backend.</param>
/// <param name="ElapsedMs">How long the check took.</param>
/// <param name="Detail">Server facts on success, the error chain on failure, or the reason no check was possible.</param>
public sealed record ProbeResult(ProbeOutcome Outcome, long ElapsedMs, string Detail)
{
    /// <summary>True only when the server positively answered. Never true for <see cref="ProbeOutcome.NotSupported"/>.</summary>
    public bool Reachable => Outcome == ProbeOutcome.Reachable;

    /// <summary>True only when the account was positively determined to be unreachable.</summary>
    public bool Unreachable => Outcome == ProbeOutcome.Unreachable;

    /// <summary>True when nothing could be concluded — neither a pass nor a failure.</summary>
    public bool Inconclusive => Outcome == ProbeOutcome.NotSupported;

    public override string ToString() => Outcome switch
    {
        ProbeOutcome.Reachable    => $"reachable in {ElapsedMs} ms — {Detail}",
        ProbeOutcome.Unreachable  => $"not reachable after {ElapsedMs} ms — {Detail}",
        _                         => $"not testable — {Detail}",
    };
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
    /// Performs an independent round-trip against the account's server. Never reuses a pooled
    /// connection and never mutates pool or watcher state, so the result reflects the server and
    /// credentials only. Does not throw for connection failures — those are the answer, and are
    /// returned in the result. Returns <see cref="ProbeOutcome.NotSupported"/> rather than guessing
    /// when this backend has no way to check.
    /// </summary>
    Task<ProbeResult> ProbeAccountAsync(Guid accountId, CancellationToken ct = default);
}
