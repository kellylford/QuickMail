using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Services;

/// <summary>
/// Decides which IMAP IDLE watchers to start and which to stop when the connected-account set
/// changes. Existing, still-running watchers are deliberately left alone.
///
/// This is the guard against the regression of issue #126 ("adding a new account disconnects the
/// others"): the old <c>StartWatchers</c> cancelled and rebuilt <em>every</em> account's held IDLE
/// connection whenever the set changed, so adding one account fired a simultaneous reconnect burst
/// that tripped servers' per-IP connection limits and knocked the other accounts offline. Reconcile
/// touches only the accounts that actually joined or left.
///
/// Pure and side-effect-free so it can be unit-tested without live connections (WatcherReconcilerTests).
/// </summary>
internal static class WatcherReconciler
{
    /// <param name="currentlyWatching">Account ids that already have a live watcher.</param>
    /// <param name="desired">Account ids that should have a watcher (the connected IMAP accounts).</param>
    /// <returns>
    /// <c>ToStart</c> — desired accounts with no live watcher yet;
    /// <c>ToStop</c> — watched accounts that are no longer desired (removed/disconnected).
    /// </returns>
    public static (IReadOnlyList<Guid> ToStart, IReadOnlyList<Guid> ToStop) Reconcile(
        IEnumerable<Guid> currentlyWatching, IEnumerable<Guid> desired)
    {
        var current = new HashSet<Guid>(currentlyWatching);
        var want    = new HashSet<Guid>(desired);

        var toStart = want.Where(id => !current.Contains(id)).ToList();
        var toStop  = current.Where(id => !want.Contains(id)).ToList();
        return (toStart, toStop);
    }
}
