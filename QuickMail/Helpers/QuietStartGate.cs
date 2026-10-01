using System;

namespace QuickMail.Helpers;

/// <summary>
/// Decides when a quiet start at Windows sign-in (#770) runs the work it held back — the main
/// window's focus and its one-time startup dialogs, or with no accounts the Account Manager.
///
/// Two things must both have happened: load has finished (the work needs the loaded view) and the
/// user has brought the window up. Either can come first — on a machine busy signing in, the user
/// may select the taskbar button while load is still running — so the gate records each and hands
/// back the work when the second arrives, exactly once.
///
/// An activation caused by a new-mail notification that opens a message does not count: that path
/// sets its own focus and selection, and a startup dialog would land over the message it opens.
/// The next activation runs the work instead.
///
/// Pure state, no WPF, so every ordering is unit-tested; MainWindow only wires it up.
/// </summary>
public sealed class QuietStartGate
{
    private bool _activated;
    private Action? _pending;

    /// <summary>Load has finished. Returns <paramref name="work"/> if the user already brought the
    /// window up, otherwise null and keeps it for <see cref="Activated"/>.</summary>
    public Action? LoadCompleted(Action work)
    {
        _pending = work;
        return Take();
    }

    /// <summary>The window was activated. Returns the held work if load has finished; null if it
    /// has not (load will hand it back) or if this activation opens a message from a notification.</summary>
    public Action? Activated(bool opensMessageFromNotification)
    {
        if (opensMessageFromNotification) return null;
        _activated = true;
        return Take();
    }

    /// <summary>True once an activation has counted; later activations need no attention.</summary>
    public bool HasBeenActivated => _activated;

    private Action? Take()
    {
        if (!_activated || _pending is not { } work) return null;
        _pending = null;
        return work;
    }
}
