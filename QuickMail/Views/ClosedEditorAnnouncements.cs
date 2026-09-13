using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Announces what a rule editor says after its window has closed (#701).
/// <para>
/// The editor window announces its own view model's messages while it is open, and the Rules Manager must not
/// repeat them: it did, and every refusal was heard twice. But saving a server-side rule (a work or school
/// Microsoft 365 account) can still be waiting on the server when the editor is closed, and the editor stops listening as it closes, so a failure
/// then was announced by nothing. From the moment an editor closes, its messages are the Rules Manager's to
/// say — still once.
/// </para>
/// </summary>
/// <remarks>Deliberately not <see cref="IDisposable"/>: it lives in a Window, which WPF never disposes. The window
/// calls <see cref="Stop"/> from its OnClosed instead.</remarks>
internal sealed class ClosedEditorAnnouncements
{
    private readonly Action<string, AnnouncementCategory> _announce;
    private readonly List<ServerRuleEditorViewModel> _closed = [];
    private bool _stopped;

    public ClosedEditorAnnouncements(Action<string, AnnouncementCategory> announce) => _announce = announce;

    /// <summary><paramref name="editor"/>'s window has closed: announce what it says from now on.</summary>
    public void EditorClosed(ServerRuleEditorViewModel editor)
    {
        // The Rules Manager owns its editors, so closing it closes them too, and an editor's Closed can arrive after
        // the Rules Manager's own. There is no window left to say anything by then.
        if (_stopped) return;
        editor.AnnouncementRequested += Announce;
        _closed.Add(editor);
    }

    /// <summary>The Rules Manager is closing: stop announcing for every editor.</summary>
    public void Stop()
    {
        _stopped = true;
        foreach (var editor in _closed) editor.AnnouncementRequested -= Announce;
        _closed.Clear();
    }

    private void Announce(string text, AnnouncementCategory category) => _announce(text, category);
}
