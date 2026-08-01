using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickMail.Views;

/// <summary>
/// A list whose rows are check boxes and nothing else.
///
/// <para>Both stock items controls insert a wrapper element per row that carries its own name,
/// taken from the item's <c>ToString()</c>: a <c>ListBox</c> contributes a ListItem, and a plain
/// <c>ItemsControl</c> contributes a DataItem. Either way the row's name is in the tree twice, once
/// on the wrapper and once on the check box inside it, and it is spoken twice — "Date, Date check
/// box, unchecked".</para>
///
/// <para>This control keeps the list role (so a row still has position and count) but reports the
/// row check boxes as its direct children, so each row is exactly one named element.
/// <c>RowFieldsWindowCheckBoxTests</c> asserts that shape against the live automation tree.</para>
/// </summary>
public sealed class FieldCheckList : ItemsControl
{
    // Home/End and first-letter navigation come free inside a ListBox; this control is not one,
    // so they are implemented here. Type-ahead reuses QuickMail's own accumulator (#415) rather
    // than WPF TextSearch, which only works on a Selector.
    private readonly TypeAheadPrefixTracker _typeAhead = new();

    protected override AutomationPeer OnCreateAutomationPeer() => new FieldCheckListPeer(this);

    // ── keyboard ──────────────────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // Only unmodified Home/End: Alt+Up/Alt+Down reorder, and Ctrl/Shift combinations are
        // left for whatever else may want them.
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        var target = e.Key switch
        {
            Key.Home => 0,
            Key.End  => Items.Count - 1,
            _        => -1,
        };
        if (target >= 0 && FocusRow(target)) e.Handled = true;
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (e.Handled) return;
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;

        // Space arrives here as " ", which the tracker rejects as whitespace — so Space keeps
        // falling through to the focused check box and toggles it.
        if (TryTypeAhead(e.Text)) e.Handled = true;
    }

    /// <summary>
    /// Moves focus to the next row whose text starts with the accumulated prefix, wrapping.
    /// Internal so tests can drive it directly rather than through synthesized input (#415).
    /// </summary>
    internal bool TryTypeAhead(string? text)
    {
        if (!_typeAhead.TryAppend(text, this, out var prefix)) return false;

        var items = Items.Cast<object?>().ToList();
        // Item text is ToString(), matching how WPF's own TextSearch falls back.
        var idx = TypeAheadMatcher.FindNext(items, i => i?.ToString(), FocusedIndex(), prefix);
        return idx >= 0 && FocusRow(idx);
    }

    /// <summary>Index of the row containing keyboard focus, or -1 when focus is elsewhere.</summary>
    internal int FocusedIndex()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject c &&
                FindCheckBox(c) is { IsKeyboardFocusWithin: true })
                return i;
        }
        return -1;
    }

    /// <summary>Focuses a row's check box and scrolls it into view.</summary>
    internal bool FocusRow(int index)
    {
        if (index < 0 || index >= Items.Count) return false;
        UpdateLayout();
        if (ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container) return false;
        if (FindCheckBox(container) is not { } box) return false;

        box.BringIntoView();
        return box.Focus();
    }

    /// <summary>The row check boxes, in display order.</summary>
    internal IEnumerable<CheckBox> RowCheckBoxes()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                FindCheckBox(container) is { } box)
                yield return box;
        }
    }

    internal static CheckBox? FindCheckBox(DependencyObject root)
    {
        if (root is CheckBox cb) return cb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindCheckBox(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    private sealed class FieldCheckListPeer : FrameworkElementAutomationPeer
    {
        public FieldCheckListPeer(FieldCheckList owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(FieldCheckList);

        /// <summary>
        /// The check boxes themselves, skipping the per-item wrapper peers that would otherwise
        /// repeat each row's name.
        /// </summary>
        protected override List<AutomationPeer> GetChildrenCore()
        {
            var owner = (FieldCheckList)Owner;
            var children = new List<AutomationPeer>();
            foreach (var box in owner.RowCheckBoxes())
            {
                // Reuse the element's existing peer when WPF has already made one, so identity
                // stays stable across calls and focus/state events keep matching up.
                var peer = FromElement(box) ?? CreatePeerForElement(box);
                if (peer != null) children.Add(peer);
            }
            return children;
        }
    }
}
