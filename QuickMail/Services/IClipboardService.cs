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
}
