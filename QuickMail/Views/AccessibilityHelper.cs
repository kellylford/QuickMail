using System.Reflection;
using System.Windows.Forms;

namespace QuickMail.Views;

/// <summary>
/// Raises UIA Notification events so screen readers (NVDA, JAWS, Narrator) hear
/// programmatic announcements from this WinForms app.
///
/// Uses reflection to call AccessibleObject.RaiseAutomationNotification so this
/// compiles regardless of which exact enum namespace the build exposes.
/// </summary>
internal static class AccessibilityHelper
{
    private static readonly MethodInfo? _raiseNotification =
        typeof(AccessibleObject).GetMethod("RaiseAutomationNotification");

    private static readonly Type? _kindType =
        _raiseNotification?.GetParameters()[0].ParameterType;

    private static readonly Type? _processingType =
        _raiseNotification?.GetParameters()[1].ParameterType;

    /// <summary>
    /// Announces <paramref name="text"/> to the active screen reader.
    /// </summary>
    /// <param name="control">Any live control in the window.</param>
    /// <param name="text">The string for the screen reader to speak.</param>
    /// <param name="interrupt">
    /// <see langword="true"/> to interrupt current utterance;
    /// <see langword="false"/> to queue, replacing any pending announcement.
    /// </param>
    public static void Announce(Control control, string text, bool interrupt = false)
    {
        if (string.IsNullOrEmpty(text) || _raiseNotification == null) return;
        try
        {
            // AutomationNotificationKind.Other = 4
            // AutomationNotificationProcessing.MostRecent = 2, ImportantMostRecent = 5
            var kind       = Enum.ToObject(_kindType!,       4);
            var processing = Enum.ToObject(_processingType!, interrupt ? 5 : 2);
            _raiseNotification.Invoke(control.AccessibilityObject, [kind, processing, text]);
        }
        catch { /* accessibility is best-effort */ }
    }
}
