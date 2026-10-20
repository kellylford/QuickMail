using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickMail.Helpers;

/// <summary>
/// Puts keyboard focus straight onto a context menu's first item when the menu is opened from the
/// keyboard, instead of onto the menu itself (issue #693).
///
/// <para>
/// As a <c>ContextMenu</c>'s popup opens, WPF enters menu mode and the menu calls <c>Focus()</c>
/// on <em>itself</em> (<c>ContextMenu.OnIsMenuModeChanged</c>). No item is selected: WPF's own
/// <c>ContextMenu.OnKeyDown</c> waits for a Down or Up arrow before it focuses one. So focus first
/// stops on the menu as a whole, and only a second step reaches an item, whether the user takes it
/// by pressing Down or assistive technology takes it for them. Recorded in the running app, that
/// was a focus event on the menu and, about 220 ms later, a second on its first item: a screen
/// reader started speaking one and was cut off by the other, and a braille display showed the menu
/// flicker as if it had opened twice. Measured in the test VM with no screen reader running,
/// focus stays on the menu itself until a key is pressed.
/// </para>
///
/// <para>
/// The fix is made at the one point the menu asks for focus, not after it has it. A class handler
/// on <see cref="Keyboard.PreviewGotKeyboardFocusEvent"/> sees the menu about to take focus from
/// outside, focuses the item instead, and marks the request handled, which WPF treats as focus
/// delegated rather than refused. Focus then changes once, from the list straight to the item, and
/// the platform reports that one change. Moving focus in an <c>Opened</c> handler cannot do this:
/// by the time <c>Opened</c> is raised the menu has already taken focus, so that only shortens the
/// gap between the two steps.
/// </para>
///
/// <para>
/// Only for a menu opened from the keyboard, which is where Windows itself selects the first item.
/// A menu opened with the mouse keeps WPF's behaviour, with no item highlighted. A menu can name a
/// different starting item — the one in effect, for a menu of choices — with
/// <see cref="SetInitialItem"/>; otherwise it is the first item that is enabled and visible,
/// the same item Down would have reached.
/// </para>
///
/// <para>
/// Nothing is announced here. Focusing a menu item and reporting it is the platform's job.
/// </para>
/// </summary>
public static class ContextMenuInitialFocus
{
    private static bool _installed;

    /// <summary>
    /// Whether the menu now opening was opened from the keyboard. Shipped behaviour asks the input
    /// manager which device delivered the most recent input. A test cannot set that without real
    /// keystrokes, so tests pin it; nothing in the app assigns it.
    /// </summary>
    internal static Func<bool> OpenedFromKeyboard { get; set; } =
        () => InputManager.Current.MostRecentInputDevice is KeyboardDevice;

    /// <summary>
    /// Chooses the item a menu opens on, when it should not be the first one. Returning null, or an
    /// item that cannot take focus, falls back to the first item.
    /// </summary>
    public static readonly DependencyProperty InitialItemProperty = DependencyProperty.RegisterAttached(
        "InitialItem", typeof(Func<ContextMenu, MenuItem?>), typeof(ContextMenuInitialFocus),
        new PropertyMetadata(null));

    public static void SetInitialItem(ContextMenu menu, Func<ContextMenu, MenuItem?>? chooser)
        => menu.SetValue(InitialItemProperty, chooser);

    public static Func<ContextMenu, MenuItem?>? GetInitialItem(ContextMenu menu)
        => (Func<ContextMenu, MenuItem?>?)menu.GetValue(InitialItemProperty);

    /// <summary>
    /// Registers the behaviour for every <see cref="ContextMenu"/> in the process. Called once from
    /// application startup: a class handler cannot be forgotten when a new menu is added, which a
    /// per-menu handler can. Idempotent, so tests can call it too.
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        EventManager.RegisterClassHandler(
            typeof(ContextMenu), Keyboard.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnPreviewGotKeyboardFocus));
    }

    private static void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.Handled || sender is not ContextMenu menu) return;

        // Only the menu itself taking focus. Focus moving to one of its items tunnels through the
        // menu too, and is exactly what should happen.
        if (!ReferenceEquals(e.NewFocus, menu) || !menu.IsOpen) return;

        // Only focus arriving from outside the menu, which is the menu opening. Focus coming back
        // to the menu from something inside it is WPF's own navigation and is left alone.
        if (e.OldFocus is DependencyObject old && IsInside(menu, old)) return;

        if (!OpenedFromKeyboard()) return;

        var item = InitialItemFor(menu);
        if (item is null) return;

        // Delegate: focus the item from inside the menu's own focus request, then cancel that
        // request. If the item could not take keyboard focus after all, leave the request alone so
        // the menu still gets focus exactly as before.
        item.Focus();
        if (item.IsKeyboardFocused) e.Handled = true;
    }

    /// <summary>
    /// The item a keyboard-opened menu starts on: the one <see cref="SetInitialItem"/> chooses if it
    /// can take focus, otherwise the first item that is enabled, visible and focusable. Null when
    /// there is none, in which case the menu keeps focus itself.
    /// </summary>
    internal static MenuItem? InitialItemFor(ContextMenu menu)
    {
        if (menu.Items.Count == 0) return null;

        // Defensive: the popup measures its content, generating every container, before it reports
        // itself open. If the containers are ever not there yet, one layout pass makes them, before
        // either a menu's own chooser or the first-item search looks for one.
        if (menu.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            menu.UpdateLayout();

        if (GetInitialItem(menu)?.Invoke(menu) is { } chosen && CanTakeFocus(chosen))
            return chosen;

        return FirstFocusableItem(menu);
    }

    /// <summary>
    /// A chooser for <see cref="SetInitialItem"/> on a menu of choices: the item that is checked,
    /// which is the choice in effect. Null when none is, so the menu opens on its first item.
    /// </summary>
    public static MenuItem? CheckedItem(ContextMenu menu)
    {
        foreach (var item in menu.Items)
        {
            if (menu.ItemContainerGenerator.ContainerFromItem(item) is MenuItem { IsChecked: true } container)
                return container;
        }
        return null;
    }

    private static MenuItem? FirstFocusableItem(ContextMenu menu)
    {
        foreach (var item in menu.Items)
        {
            if (menu.ItemContainerGenerator.ContainerFromItem(item) is MenuItem container
                && CanTakeFocus(container))
                return container;
        }
        return null;
    }

    private static bool CanTakeFocus(MenuItem item)
        => item.IsEnabled && item.IsVisible && item.Focusable;

    // Visual parents first; logical parents bridge the popup boundary of a submenu. The step limit
    // only guards against a malformed tree; real menus are a few dozen levels deep.
    private static bool IsInside(DependencyObject ancestor, DependencyObject node)
    {
        var d = node;
        for (var steps = 0; d is not null && steps < 1000; steps++)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null)
                ?? LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
