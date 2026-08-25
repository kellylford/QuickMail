using System.Windows.Input;

namespace QuickMail.Helpers;

/// <summary>
/// Decides which keyboard context-menu gesture a control has to open its own <c>ContextMenu</c>
/// for, and which one it must leave to Windows (issue #631).
///
/// <para>
/// The two gestures do <em>not</em> reach an application the same way. <c>DefWindowProc</c> turns
/// Shift+F10 into <c>WM_CONTEXTMENU</c> while processing the <em>key down</em>, but it turns the
/// Applications key into <c>WM_CONTEXTMENU</c> only on the <em>key up</em>. A handler that opens
/// the menu itself from <c>PreviewKeyDown</c> and marks the event handled therefore behaves
/// completely differently for the two:
/// </para>
///
/// <list type="bullet">
///   <item><description>Shift+F10 — marking the key down handled keeps the message away from
///   <c>DefWindowProc</c>, so no <c>WM_CONTEXTMENU</c> is ever generated and the menu opened here
///   is the only one. This works, and is why the attachment lists open their menu directly: it
///   stops <c>MainWindow.OnWindowContextMenuOpening</c> from answering with the message menu
///   instead (commit da7714c).</description></item>
///   <item><description>Applications key — the key down carries no <c>WM_CONTEXTMENU</c> to
///   suppress, so handling it changes nothing about what follows. The key up still reaches
///   <c>DefWindowProc</c>, which raises <c>WM_CONTEXTMENU</c> at the window that now has focus:
///   the menu's own popup. A second context-menu request arriving at the open popup tears it back
///   down, which is the reported symptom — the menu is announced and then immediately gone.
///   </description></item>
/// </list>
///
/// <para>
/// So the Applications key must be left alone. Its <c>WM_CONTEXTMENU</c> routes
/// <c>ContextMenuOpening</c> from the focused item up to the list, and WPF opens the list's own
/// <c>ContextMenu</c> — the ordinary platform path, the same one every other list in the app
/// relies on. <c>OnWindowContextMenuOpening</c> already bails out for the attachment lists, so
/// nothing intercepts it on the way.
/// </para>
/// </summary>
public static class ContextMenuKeys
{
    /// <summary>
    /// True when the control must open its <c>ContextMenu</c> itself from <c>PreviewKeyDown</c>
    /// and mark the key handled. False for every other key — including the Applications key,
    /// which Windows delivers as <c>WM_CONTEXTMENU</c> on key up and which must not be
    /// intercepted here.
    /// </summary>
    public static bool OpensMenuOnKeyDown(Key key, ModifierKeys modifiers)
        => key == Key.F10 && modifiers == ModifierKeys.Shift;
}
