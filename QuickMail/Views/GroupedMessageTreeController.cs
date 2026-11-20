using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// The event-handler logic the three grouped-message TreeViews (ConversationTree,
/// SenderGroupTree, ToGroupTree) share verbatim. Each tree still wires its own PreviewKeyDown,
/// because the group type and the delete command differ.
/// </summary>
/// <remarks>
/// This deliberately covers only the handlers that are genuinely identical. It once also carried
/// FocusMessage, LandOnAfterRebuild, LandOnMessageAfterRebuild and OnContextMenuOpening, none of
/// which anything ever called — MainWindow kept its own copies throughout. The first three were
/// worse than merely unused: they used the shape that assumes a container can be waited for, which
/// is false for a group outside the virtualized viewport, and were left behind when MainWindow's
/// copies moved to <see cref="Helpers.TreeViewItemRealizer"/> (#617). Anything that needs to focus
/// a message inside a group belongs there, not here.
/// </remarks>
public class GroupedMessageTreeController
{
    private readonly TreeView _tree;
    private readonly MainViewModel _vm;
    private readonly string _logName;
    private readonly Func<IEnumerable<object>> _getVisibleItems;
    private readonly Func<TreeView, object?, List<object>, string?, bool> _tryHandleTypeAhead;

    public GroupedMessageTreeController(
        TreeView tree,
        MainViewModel vm,
        string logName,
        Func<IEnumerable<object>> getVisibleItems,
        Func<TreeView, object?, List<object>, string?, bool> tryHandleTypeAhead)
    {
        _tree = tree;
        _vm = vm;
        _logName = logName;
        _getVisibleItems = getVisibleItems;
        _tryHandleTypeAhead = tryHandleTypeAhead;
    }

    // ── GotKeyboardFocus ──────────────────────────────────────────────────────

    public void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        LogService.Debug($"[FOCUS] {_logName} GotKeyboardFocus selectedItem={_tree.SelectedItem?.GetType().Name ?? "null"} count={_tree.Items.Count} from={e.OldFocus?.GetType().Name ?? "null"}");
        if (_tree.SelectedItem == null && _tree.Items.Count > 0)
        {
            if (_tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            {
                LogService.Debug($"[FOCUS]   {_logName} GotKeyboardFocus: no selection — selecting first item");
                first.IsSelected = true;
                first.Focus();
            }
        }
    }

    // ── SelectedItemChanged ───────────────────────────────────────────────────

    public void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        LogService.Debug($"[FOCUS] {_logName} SelectedItemChanged old={e.OldValue?.GetType().Name ?? "null"} new={e.NewValue?.GetType().Name ?? "null"}");
        if (e.NewValue is MailMessageSummary msg)
            _vm.SelectedMessage = msg;
    }

    // ── PreviewTextInput ──────────────────────────────────────────────────────

    public void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var visibleItems = _getVisibleItems().ToList();
        if (_tryHandleTypeAhead(_tree, _tree.SelectedItem, visibleItems, e.Text))
            e.Handled = true;
    }

    // ── PreviewMouseRightButtonDown ───────────────────────────────────────────

    public void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        if (source is TreeViewItem tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
    }

    // ── FocusFirstItem ────────────────────────────────────────────────────────

    public void FocusFirstItem()
    {
        if (_tree.Items.Count == 0) { _tree.Focus(); return; }
        _tree.Dispatcher.InvokeAsync(_tree.Focus, DispatcherPriority.Input);
    }
}
