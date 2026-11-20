using System.Windows.Input;

namespace QuickMail.Helpers;

/// <summary>
/// The standard Windows cut, copy, paste and select-all gestures, which a text box or web page owns whenever
/// nothing else claims them (#978).
///
/// <para>
/// A window that dispatches keys through its registry must not swallow one of these just because a command is
/// bound to it: the message window's Copy Attachment sits on Ctrl+C and is available only while the attachment
/// list has focus, and swallowing the key everywhere else meant Ctrl+C copied nothing from the message body or
/// its header fields.
/// </para>
/// </summary>
internal static class TextEditingGestures
{
    /// <summary>True for Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+A, Ctrl+Insert, Shift+Insert and Shift+Delete.</summary>
    internal static bool IsTextEditingGesture(Key key, ModifierKeys modifiers) =>
        modifiers switch
        {
            ModifierKeys.Control => key is Key.C or Key.X or Key.V or Key.A or Key.Insert,
            ModifierKeys.Shift => key is Key.Insert or Key.Delete,
            _ => false,
        };

    /// <summary>
    /// Whether a window should mark a key handled when its registry has a command on it that is not available
    /// right now. Most such keys are swallowed so they do not fall through to the page (Alt+Left on the first
    /// message), but a text-editing gesture is left to whatever has focus.
    /// </summary>
    internal static bool SwallowWhenUnavailable(Key key, ModifierKeys modifiers) =>
        !IsTextEditingGesture(key, modifiers);
}
