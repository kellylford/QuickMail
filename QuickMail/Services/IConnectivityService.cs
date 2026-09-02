using System;
using System.Threading;

namespace QuickMail.Services;

/// <summary>What the app currently believes about one account's reachability.</summary>
public enum AccountConnectivity
{
    /// <summary>No operation has reported either way yet. Treated as online, optimistically.</summary>
    Unknown,
    Online,
    Offline,
}

/// <summary>
/// The app's one answer to "are we online?" (issue #637). Two inputs feed it: the machine-level
/// network signal from Windows, and the outcome of real operations against each account. Neither
/// alone is enough — a captive portal looks like a working network until a mail server fails to
/// answer, and a single account's outage must not mark the whole app offline while another account
/// is fine.
/// </summary>
/// <remarks>
/// The service is <b>told</b> by the code that talks to servers; it does not subscribe to the mail
/// backends itself. Every event fires on a ThreadPool or timer thread; subscribers that touch UI
/// state must marshal.
/// </remarks>
public interface IConnectivityService
{
    /// <summary>Windows reports a usable network interface. Raw, undebounced.</summary>
    bool IsNetworkAvailable { get; }

    /// <summary>
    /// Network available and not every known account offline. Going offline is debounced so a
    /// momentary blip does not announce itself; coming back online is immediate.
    /// </summary>
    bool IsOnline { get; }

    /// <summary>True unless this account has been reported unreachable and not since reachable, or the network is down.</summary>
    bool IsAccountOnline(Guid accountId);

    AccountConnectivity AccountState(Guid accountId);

    /// <summary>An operation against the account succeeded. Idempotent; a change is journaled with its source.</summary>
    void NoteAccountReachable(Guid accountId, string source);

    /// <summary>An operation against the account failed to reach the server. Idempotent.</summary>
    void NoteAccountUnreachable(Guid accountId, string source);

    /// <summary>
    /// Classifies <paramref name="ex"/>: a connection failure marks the account unreachable; anything
    /// else, including null (success), marks it reachable — a server that answered "no" is a server
    /// that answered.
    /// </summary>
    void NoteOperationOutcome(Guid accountId, Exception? ex, string source, CancellationToken callerToken = default);

    /// <summary>The account was removed; forget its state.</summary>
    void Forget(Guid accountId);

    /// <summary>Fires on each change of <see cref="IsOnline"/>: false after the debounce, true at once.</summary>
    event Action<bool>? OnlineChanged;

    /// <summary>Fires once per flip of an account between online and offline.</summary>
    event Action<Guid, bool>? AccountOnlineChanged;

    /// <summary>The raw machine-level signal, undebounced — what a reconnect should react to.</summary>
    event Action<bool>? NetworkAvailabilityChanged;
}
