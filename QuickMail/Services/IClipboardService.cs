using System.Collections.Generic;

namespace QuickMail.Services;

/// <summary>
/// Clipboard access, behind an interface so ViewModels do not reach into
/// <c>System.Windows.Clipboard</c> directly (see the MVVM rules in CLAUDE.md) and so tests do not
/// depend on the real Windows clipboard.
///
/// That test dependency was not theoretical: the clipboard is a machine-wide shared resource, and
/// a VM test that used it failed — and took the whole test host down with it — whenever any other
/// process happened to hold the clipboard, including QuickMail itself running on the same machine.
/// </summary>
public interface IClipboardService
{
    /// <summary>Places <paramref name="text"/> on the clipboard. Returns false if it could not be set.</summary>
    bool SetText(string text);

    /// <summary>Returns the clipboard's text, or an empty string if it holds none or cannot be read.</summary>
    string GetText();

    /// <summary>
    /// Places files on the clipboard as Explorer's Copy does, so they paste into Explorer, the desktop
    /// or a message being written, tagged with <paramref name="marker"/> so QuickMail can tell later that
    /// the clipboard still holds what it put there (#929). Returns false if it could not be set.
    /// </summary>
    bool SetFiles(IReadOnlyList<string> paths, string marker) => false;

    /// <summary>
    /// Places only <paramref name="marker"/> on the clipboard: messages were copied, but there are no
    /// files to offer other programs (yet, or at all). Returns false if it could not be set.
    /// </summary>
    bool SetMarker(string marker) => false;

    /// <summary>
    /// The marker QuickMail last put on the clipboard, or null when the clipboard now holds something
    /// else (the user copied text or files since) or cannot be read.
    /// </summary>
    string? GetMarker() => null;
}
