using System;
using System.Collections.Generic;
using System.ComponentModel;
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
/// Encapsulates the common event-handler logic shared by the three grouped-message
/// TreeViews (ConversationTree, SenderGroupTree, ToGroupTree).  Each tree wires
/// its own PreviewKeyDown because the group-type and delete-command differ, but
/// the remaining handlers (GotKeyboardFocus, SelectedItemChanged, PreviewTextInput,
/// PreviewMouseRightButtonDown, ContextMenuOpening, FocusFirstItem, LandOnAfterRebuild,
/// FocusMessage, LandOnMessageAfterRebuild) are identical in structure.
/// </summary>
public class GroupedMessageTreeController
{
    private readonly TreeView _tree;
    private readonly MainViewModel _vm;
    private readonly string _logName;
    private readonly string _collectionPropertyName;
    private readonly Func<int> _getCollectionCount;
    private readonly Func<object?, int> _getItemIndex;
    private readonly Func<int, object?> _getItemAt;
    private readonly Func<object, string> _getGroupKey;
    private readonly Func<string, object?> _findGroupByKey;
    private readonly Func<object, IReadOnlyList<MailMessageSummary>> _getGroupMessages;
    private readonly Func<IEnumerable<object>> _getVisibleItems;
    private readonly Func<TreeView, object?, List<object>, string?, bool> _tryHandleTypeAhead;

    public GroupedMessageTreeController(
        TreeView tree,
        MainViewModel vm,
        string logName,
        string collectionPropertyName,
        Func<int> getCollectionCount,
        Func<object?, int> getItemIndex,
        Func<int, object?> getItemAt,
        Func<object, string> getGroupKey,
        Func<string, object?> findGroupByKey,
        Func<object, IReadOnlyList<MailMessageSummary>> getGroupMessages,
        Func<IEnumerable<object>> getVisibleItems,
        Func<TreeView, object?, List<object>, string?, bool> tryHandleTypeAhead)
    {
        _tree = tree;
        _vm = vm;
        _logName = logName;
        _collectionPropertyName = collectionPropertyName;
        _getCollectionCount = getCollectionCount;
        _getItemIndex = getItemIndex;
        _getItemAt = getItemAt;
        _getGroupKey = getGroupKey;
        _findGroupByKey = findGroupByKey;
        _getGroupMessages = getGroupMessages;
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

    // ── ContextMenuOpening ────────────────────────────────────────────────────

    public void OnContextMenuOpening(object sender, ContextMenuEventArgs e, string groupContextMenuKey, string messageContextMenuKey)
    {
        switch (_tree.SelectedItem)
        {
            case null:
                e.Handled = true;
                break;
            default:
                // If the selected item is a group type (not MailMessageSummary), use the group menu;
                // otherwise use the message menu.
                if (_tree.SelectedItem is MailMessageSummary)
                    _tree.ContextMenu = (ContextMenu)((FrameworkElement)sender).FindResource(messageContextMenuKey);
                else
                    _tree.ContextMenu = (ContextMenu)((FrameworkElement)sender).FindResource(groupContextMenuKey);
                break;
        }
    }

    // ── FocusFirstItem ────────────────────────────────────────────────────────

    public void FocusFirstItem()
    {
        if (_tree.Items.Count == 0) { _tree.Focus(); return; }
        _tree.Dispatcher.InvokeAsync(_tree.Focus, DispatcherPriority.Input);
    }

    // ── LandOnAfterRebuild ────────────────────────────────────────────────────

    public void LandOnAfterRebuild(int targetIdx)
    {
        LogService.Debug($"[FOCUS] LandOn{_logName}: registered listener targetIdx={targetIdx}");
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != _collectionPropertyName) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            LogService.Debug($"[FOCUS] LandOn{_logName}: listener fired count={_getCollectionCount()} targetIdx={targetIdx}");
            _tree.Dispatcher.InvokeAsync(() =>
            {
                var count = _getCollectionCount();
                if (count == 0)
                {
                    LogService.Debug($"[FOCUS] LandOn{_logName}: dispatch skipped — collection empty");
                    return;
                }
                var idx = Math.Max(0, Math.Min(targetIdx, count - 1));
                var item = _getItemAt(idx);
                if (item != null && _tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                {
                    LogService.Debug($"[FOCUS] LandOn{_logName}: tvi.Focus() idx={idx}");
                    tvi.IsSelected = true;
                    tvi.Focus();
                }
                else
                {
                    LogService.Debug($"[FOCUS] LandOn{_logName}: container not realized idx={idx} — retry at Background");
                    _tree.Dispatcher.InvokeAsync(() =>
                    {
                        if (item != null && _tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi2)
                        {
                            LogService.Debug($"[FOCUS] LandOn{_logName}: retry tvi.Focus() idx={idx}");
                            tvi2.IsSelected = true;
                            tvi2.Focus();
                        }
                        else
                        {
                            LogService.Debug($"[FOCUS] LandOn{_logName}: retry also failed idx={idx} — giving up");
                        }
                    }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }

    // ── FocusMessage ──────────────────────────────────────────────────────────

    public void FocusMessage(object group, int msgIdx, bool isRetry = false)
    {
        var messages = _getGroupMessages(group);
        var target = messages[msgIdx];
        var groupTvi = _tree.ItemContainerGenerator.ContainerFromItem(group) as TreeViewItem;
        if (groupTvi == null)
        {
            if (!isRetry)
                _tree.Dispatcher.InvokeAsync(() => FocusMessage(group, msgIdx, true), DispatcherPriority.Background);
            return;
        }
        if (!groupTvi.IsExpanded) groupTvi.IsExpanded = true;
        var msgTvi = groupTvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
        if (msgTvi != null) { msgTvi.IsSelected = true; msgTvi.Focus(); return; }
        if (!isRetry)
            _tree.Dispatcher.InvokeAsync(() =>
            {
                var t2 = groupTvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
                if (t2 != null) { t2.IsSelected = true; t2.Focus(); }
                else { groupTvi.IsSelected = true; groupTvi.Focus(); }
            }, DispatcherPriority.Background);
        else { groupTvi.IsSelected = true; groupTvi.Focus(); }
    }

    // ── LandOnMessageAfterRebuild ─────────────────────────────────────────────

    public void LandOnMessageAfterRebuild(string groupKey, int msgIdx, int fallbackGroupIdx)
    {
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != _collectionPropertyName) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            _tree.Dispatcher.InvokeAsync(() =>
            {
                var group = _findGroupByKey(groupKey);
                if (group != null)
                {
                    var messages = _getGroupMessages(group);
                    if (messages.Count > 0)
                        FocusMessage(group, Math.Min(msgIdx, messages.Count - 1));
                }
                else
                {
                    var count = _getCollectionCount();
                    if (count == 0) return;
                    var idx = Math.Max(0, Math.Min(fallbackGroupIdx, count - 1));
                    var fallback = _getItemAt(idx);
                    if (fallback != null && _tree.ItemContainerGenerator.ContainerFromItem(fallback) is TreeViewItem tvi)
                    { tvi.IsSelected = true; tvi.Focus(); }
                    else
                        _tree.Dispatcher.InvokeAsync(() =>
                        {
                            if (fallback != null && _tree.ItemContainerGenerator.ContainerFromItem(fallback) is TreeViewItem tvi2)
                            { tvi2.IsSelected = true; tvi2.Focus(); }
                        }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }
}
