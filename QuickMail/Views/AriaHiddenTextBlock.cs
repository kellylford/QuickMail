using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace QuickMail.Views;

/// <summary>
/// A TextBlock whose UIA automation peer is suppressed, making it invisible to screen readers.
/// Use when the containing element's AutomationProperties.Name already carries the full
/// accessible name for the whole item, so child text is not announced separately.
/// </summary>
public sealed class AriaHiddenTextBlock : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
