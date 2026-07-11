using System;
using System.Collections.Generic;

namespace QuickMail.ViewModels;

/// <summary>
/// Tracks which connected-account set the change-notifier watchers were last started for, so callers
/// only restart them when the set actually changes. <c>StartWatchers</c> is a full stop/restart, so
/// restarting on every account activation would thrash the poll loops. State advances only via
/// <see cref="MarkStarted"/> — which the caller invokes after watchers actually (re)start — so a
/// change that couldn't be acted on (e.g. no active background-sync token) is reported again next time.
/// Small and testable (WatcherStartGateTests).
/// </summary>
internal sealed class WatcherStartGate
{
    private HashSet<Guid> _started = new();

    /// <summary>True if <paramref name="connectedIds"/> differs from the set watchers were last started for.</summary>
    public bool HasChanged(IReadOnlyCollection<Guid> connectedIds) => !_started.SetEquals(connectedIds);

    /// <summary>Records that watchers are now running for exactly <paramref name="connectedIds"/>.</summary>
    public void MarkStarted(IReadOnlyCollection<Guid> connectedIds) => _started = new HashSet<Guid>(connectedIds);
}
