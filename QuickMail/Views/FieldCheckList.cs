using System.Collections.Generic;
using System.Collections.Specialized;
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

    /// <summary>Row the list returns to when focus comes back to it.</summary>
    private int _lastFocusedIndex;

    static FieldCheckList()
    {
        // The row check boxes are the tab stop; the list itself is never one. Control defaults
        // IsTabStop to true, and inheriting that default gave this window a second tab stop it
        // was never meant to have (#464): Tab landed on the list container, which is not something
        // the user can act on, and only then on a row. TabNavigation="Once" already collapsed the
        // rows themselves to one stop — measured on both sides of this change — so removing the
        // container from the tab order is the whole fix.
        //
        // IsTabStop, not Focusable: the list still has to accept Focus() calls, because that is how
        // Label implements an access key on its Target — turning Focusable off would silently make
        // Alt+F do nothing. Focus handed to the list is redirected to a row below.
        IsTabStopProperty.OverrideMetadata(
            typeof(FieldCheckList), new FrameworkPropertyMetadata(false));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FieldCheckListPeer(this);

    /// <summary>
    /// Focus belongs on a row, never on the list itself. Anything that hands focus to the list —
    /// the label's Alt+F, a caller doing <c>FieldList.Focus()</c> — is asking for the list to be
    /// usable, and a container the user cannot act on is not that. This is the only way focus
    /// reaches the container: Tab does not, because the list is not a tab stop.
    /// </summary>
    /// <remarks>
    /// <para>This corrects focus after the fact rather than cancelling it in
    /// <c>PreviewGotKeyboardFocus</c>, which was tried first and is the tidier shape on paper: it
    /// would mean the list never becomes the focused element at all. Cancelling requires focusing
    /// the row re-entrantly, from inside WPF's own focus-change dispatch, and that inner
    /// <c>Focus()</c> does not reliably succeed — it failed consistently in the full test run and
    /// passed when the same test ran alone. When it fails the pending change is not cancelled and
    /// focus simply stays on the list, which is the fault being fixed. Correcting afterwards always
    /// ends on a row.</para>
    /// <para>The cost is that the platform briefly reports the list as focused before the row, so
    /// Alt+F may be spoken as the list and then the field. Tab, the path #464 was reported
    /// against, does not go through here at all — the list is not a tab stop.</para>
    /// </remarks>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (ReferenceEquals(e.NewFocus, this))
        {
            // Back to the row the user was last on, the way returning to a list should behave.
            if (FocusRow(_lastFocusedIndex) || FocusRow(0)) e.Handled = true;
            return;
        }

        // Otherwise this is bubbling up from a row's check box: remember which, so focus comes
        // back to it.
        var idx = FocusedIndex();
        if (idx >= 0) _lastFocusedIndex = idx;
    }

    /// <summary>
    /// The remembered row belongs to the layout that was showing. Changing the Row type swaps the
    /// whole field set, and the view model resets its own selection to the first field — so a
    /// stale index here would send Alt+F to a different row than F6, in a list where the two must
    /// agree because Move Up/Down act on the selection.
    /// </summary>
    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        if (e.Action == NotifyCollectionChangedAction.Reset) _lastFocusedIndex = 0;
    }

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
