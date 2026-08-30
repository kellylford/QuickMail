namespace QuickMail.Models;

/// <summary>
/// Why the local copy behind a draft's row has gone (#637). Three quite different things end a
/// row's life, and the message list has to tell them apart: announcing all of them as an upload
/// told a user, offline, that a draft had reached a server it had not been anywhere near.
/// </summary>
public enum DraftRowDropReason
{
    /// <summary>The save's server leg took it, and the local copy was dropped behind it.</summary>
    Uploaded,

    /// <summary>The sender changed, so the row was re-keyed to the other account. Still local.</summary>
    MovedToAnotherAccount,

    /// <summary>The user declined to keep a message this window had started. Nothing to report.</summary>
    Discarded,
}
