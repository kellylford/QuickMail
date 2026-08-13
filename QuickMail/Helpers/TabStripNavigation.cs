using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickMail.Helpers;

/// <summary>
/// Makes Left/Right/Home/End on a tab header move through the tabs in <em>declaration order</em>
/// rather than by where the headers happen to sit on screen (issue #528).
///
/// <para>
/// WPF navigates a tab strip with the same geometric directional navigation it uses everywhere
/// else: Right looks for the nearest focusable thing to the right, on roughly the same line. That
/// is fine while the headers form one row, and wrong the moment they do not. <c>TabPanel</c> wraps
/// the headers onto as many rows as it needs, and then moves the row holding the selected tab to
/// the bottom. Settings has six tabs and wraps to two rows of three, so Left and Right cycled
/// General → Advanced → Keyboard Shortcuts → General for ever; Startup, Windowing and Appearance
/// could not be reached with the arrow keys at all. Which tabs are stranded depends on the window
/// width, the font size and the text-scaling setting, so this is not something a wider dialog or
/// shorter headers can be relied on to fix.
/// </para>
///
/// <para>
/// Selection follows focus, which is how a tab control behaves everywhere on Windows: arrowing to
/// a tab shows it. The tab is selected <em>before</em> it is focused so the state the platform
/// reports for the newly focused header is already the new one. Nothing is announced here —
/// focusing a tab header and reporting its state is the platform's job.
/// </para>
///
/// <para>
/// Home and End were already handled by <c>TabControl</c> itself; they are handled here too so
/// that all four keys come from one place with one set of rules (skip disabled and collapsed
/// tabs), and so the behaviour is covered by tests rather than by an implementation detail of the
/// framework.
/// </para>
/// </summary>
public static class TabStripNavigation
{
    private static bool _installed;

    /// <summary>
    /// Registers the behaviour for every <see cref="TabControl"/> in the process. Called once from
    /// application startup: a class handler cannot be forgotten when a new window with tabs is
    /// added, which a per-control attached property can. Idempotent, so tests can call it too.
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        // Tunnelling, so the TabControl sees the key before anything inside it. The OriginalSource
        // check below is what keeps that safe: a key pressed in the tab's *content* — an arrow in
        // a text box, a list, a combo box — is not touched.
        EventManager.RegisterClassHandler(
            typeof(TabControl), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not TabControl tabControl) return;

        // The event's own device state, not the global Keyboard.Modifiers: the latter reports
        // whatever is physically held right now, which is not necessarily what produced this key.
        // Ctrl+Tab and Ctrl+Shift+Tab are TabControl's own and must be left alone.
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None) return;

        // Only when focus is on a tab header of this tab control. For a keyboard event
        // OriginalSource is the focused element; Keyboard.FocusedElement is the fallback for
        // synthesized events that carry a different source.
        var current = e.OriginalSource as TabItem ?? Keyboard.FocusedElement as TabItem;
        if (current is null || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(current), tabControl))
            return;

        var tabs = SelectableTabs(tabControl);
        var index = tabs.IndexOf(current);
        if (index < 0 || tabs.Count < 2) return;

        var target = e.Key switch
        {
            Key.Right => tabs[(index + 1) % tabs.Count],
            Key.Left  => tabs[(index - 1 + tabs.Count) % tabs.Count],
            Key.Home  => tabs[0],
            Key.End   => tabs[^1],
            _         => null,
        };
        if (target is null) return;

        // Handled either way: Home on the first tab and Left on it both mean "the strip dealt with
        // this", and letting the key fall through to geometric navigation would move focus out of
        // the strip entirely.
        e.Handled = true;
        if (ReferenceEquals(target, current)) return;

        // SetCurrentValue rather than an assignment: it does not overwrite a binding on IsSelected.
        target.SetCurrentValue(TabItem.IsSelectedProperty, true);
        target.Focus();
    }

    private static List<TabItem> SelectableTabs(TabControl tabControl)
    {
        var tabs = new List<TabItem>();
        for (var i = 0; i < tabControl.Items.Count; i++)
        {
            // ContainerFromIndex covers tabs generated from an ItemsSource; tabs declared in XAML
            // are their own containers, so fall back to the item before the containers exist.
            var tab = tabControl.ItemContainerGenerator.ContainerFromIndex(i) as TabItem
                      ?? tabControl.Items[i] as TabItem;

            // Visibility rather than IsVisible: IsVisible is false for anything not yet rendered,
            // which would make the strip empty in a window that has not been shown.
            if (tab is { IsEnabled: true, Visibility: Visibility.Visible })
                tabs.Add(tab);
        }
        return tabs;
    }
}
