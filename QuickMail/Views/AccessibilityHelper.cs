using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Raises UIA Notification events so screen readers (NVDA, JAWS, Narrator) hear
/// programmatic announcements in this native WPF app.
///
/// WPF's AutomationProperties.LiveSetting fires UIA LiveRegionChanged, which
/// screen readers support only inside web browsers (HTML aria-live). For desktop
/// apps, RaiseNotificationEvent (UIA 1.1, Windows 10 1703+) is the correct API.
/// </summary>
internal static class AccessibilityHelper
{
    private const string ActivityId = "QuickMailAnnouncement";

    /// <summary>
    /// Announces <paramref name="text"/> to the active screen reader.
    /// </summary>
    /// <param name="element">Any realized UIElement in the window (typically the window itself).</param>
    /// <param name="text">The string for the screen reader to speak.</param>
    /// <param name="interrupt">
    /// <see langword="true"/> to interrupt the current utterance (ImportantMostRecent);
    /// <see langword="false"/> to queue and replace any pending same-ID announcement (MostRecent).
    /// </param>
    public static void Announce(UIElement element, string text, bool interrupt = false)
    {
        if (string.IsNullOrEmpty(text)) return;

        var fromElement = UIElementAutomationPeer.FromElement(element);
        var peer        = fromElement ?? UIElementAutomationPeer.CreatePeerForElement(element);

        LogService.Debug($"[UIA] Announce: element={element?.GetType().Name} fromElement={fromElement != null} peer={peer?.GetType().Name ?? "null"} text='{text}'");

        if (peer == null)
        {
            LogService.Debug("[UIA] Announce: peer is null — notification skipped");
            return;
        }

        var processing = interrupt
            ? AutomationNotificationProcessing.ImportantMostRecent
            : AutomationNotificationProcessing.MostRecent;

        try
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                processing,
                text,
                ActivityId);
            LogService.Debug("[UIA] Announce: RaiseNotificationEvent completed");
        }
        catch (Exception ex)
        {
            LogService.Log("[UIA] Announce RaiseNotificationEvent", ex);
        }
    }
}
