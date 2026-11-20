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
/// A new-mail notification that opens a message supersedes the work instead: that path sets its
/// own focus and selection, and a startup dialog would land over the message it opens. Postponing
/// the work to a later activation would be worse — focus would jump on some unrelated Alt+Tab or
/// dialog close, at a moment the user did not choose — so it is dropped for this session. The
/// one-time notices it would have shown come back on the next launch.
///
/// Pure state, no WPF, so every ordering is unit-tested; MainWindow only wires it up.
/// </summary>
public sealed class QuietStartGate
{
    private bool _activated;
    private bool _superseded;
    private Action? _pending;

    /// <summary>Load has finished. Returns <paramref name="work"/> if the user already brought the
    /// window up, otherwise null and keeps it for <see cref="Activated"/>.</summary>
    public Action? LoadCompleted(Action work)
    {
        if (_superseded) return null;
        _pending = work;
        return Take();
    }

    /// <summary>The window was activated. Returns the held work if load has finished; null if it
    /// has not (load will hand it back).</summary>
    public Action? Activated()
    {
        _activated = true;
        return Take();
    }

    /// <summary>A notification is opening a message: the held work, now or still to come, is
    /// dropped. Call before the restore that notification triggers, so its activation finds
    /// nothing to run. Harmless once the work has run.</summary>
    public void Supersede()
    {
        _activated = true;
        _superseded = true;
        _pending = null;
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
