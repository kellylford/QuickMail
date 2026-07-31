using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickMail.Helpers;

/// <summary>
/// Attached behaviour that makes a radio-button group follow the platform convention: arrowing
/// between the buttons moves focus <em>and</em> checks the button focus lands on. WPF's
/// <c>DirectionalNavigation</c> only moves focus — the user has to press Space to actually choose
/// the option, which is not how radio groups behave anywhere else on Windows (issue #441).
///
/// <para>
/// Apply it to the container that holds one group:
/// <code>
/// &lt;StackPanel KeyboardNavigation.TabNavigation="Once"
///             KeyboardNavigation.DirectionalNavigation="Cycle"
///             helpers:RadioGroupNavigation.SelectionFollowsFocus="True"&gt;
/// </code>
/// </para>
///
/// <para>
/// Only arrow keys select. Tab still parks on whichever button is already checked without
/// changing it, so tabbing through a settings page never silently rewrites a setting.
/// </para>
///
/// <para>
/// Do <b>not</b> apply this to a group whose buttons carry a <c>Command</c> (e.g. the FlagManager
/// colour swatches). <c>ButtonBase</c> invokes the command from <c>OnClick()</c>, which Space,
/// Enter and the mouse raise but a programmatic <c>IsChecked = true</c> does not — so arrowing
/// there would show the swatch as chosen while the command that applies the colour never ran.
/// Quietly doing nothing is worse than not selecting.
/// </para>
/// </summary>
public static class RadioGroupNavigation
{
    /// <summary>
    /// True while this helper is moving the selection with an arrow key. The screen reader
    /// announces the newly focused button and its checked state on its own, so a Checked handler
    /// that announces the option name (SettingsDialog has one, because a bare StackPanel is not a
    /// UIA selection container) must stay quiet for this one — otherwise every arrow press speaks
    /// the option twice.
    /// </summary>
    public static bool IsMovingSelection { get; private set; }

    public static readonly DependencyProperty SelectionFollowsFocusProperty =
        DependencyProperty.RegisterAttached(
            "SelectionFollowsFocus", typeof(bool), typeof(RadioGroupNavigation),
            new PropertyMetadata(false, OnSelectionFollowsFocusChanged));

    public static void SetSelectionFollowsFocus(DependencyObject element, bool value)
        => element.SetValue(SelectionFollowsFocusProperty, value);

    public static bool GetSelectionFollowsFocus(DependencyObject element)
        => (bool)element.GetValue(SelectionFollowsFocusProperty);

    private static void OnSelectionFollowsFocusChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement container) return;

        container.PreviewKeyDown -= OnContainerPreviewKeyDown;
        if (e.NewValue is true)
            container.PreviewKeyDown += OnContainerPreviewKeyDown;
    }

    private static void OnContainerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The event's own device state, not the global Keyboard.Modifiers: the latter reports
        // whatever is physically held right now, which is not necessarily what produced this key.
        if (e.Handled || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;

        var forward = e.Key is Key.Down or Key.Right;
        if (!forward && e.Key is not (Key.Up or Key.Left)) return;

        // Only act on a real radio button inside this container; leave arrow keys alone when
        // focus is in a text box, combo box or list that happens to sit in the same panel.
        // For a keyboard event OriginalSource is the focused element; Keyboard.FocusedElement
        // is the fallback for synthesized events that carry a different source.
        var current = e.OriginalSource as RadioButton ?? Keyboard.FocusedElement as RadioButton;
        if (current is null) return;
        if (sender is not DependencyObject container) return;

        var group = new List<RadioButton>();
        CollectRadioButtons(container, current.GroupName, group);

        var index = group.IndexOf(current);
        if (index < 0 || group.Count < 2) return;

        var next = group[(index + (forward ? 1 : -1) + group.Count) % group.Count];

        // Check first, then focus, so the focus event that follows describes a button that is
        // already selected rather than one that is about to be. Suppression of the Checked
        // announcement is conditional on the button being able to take focus at all: if no focus
        // event is coming, nothing else would speak the change.
        IsMovingSelection = next.Focusable;
        try
        {
            next.IsChecked = true;
        }
        finally
        {
            IsMovingSelection = false;
        }
        next.Focus();

        e.Handled = true;
    }

    // Walks the logical tree, not the visual tree: logical children exist as soon as the XAML is
    // parsed, while visual children of a templated control only appear once it has been rendered.
    private static void CollectRadioButtons(DependencyObject parent, string groupName, List<RadioButton> found)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is RadioButton rb)
            {
                // Visibility rather than IsVisible: IsVisible is false for anything not yet
                // rendered, which would make the group empty in a window that has not been shown.
                if (rb.GroupName == groupName && rb.IsEnabled && rb.Visibility == Visibility.Visible)
                    found.Add(rb);
                continue;   // a radio button never nests another one
            }
            if (child is DependencyObject node)
                CollectRadioButtons(node, groupName, found);
        }
    }
}
