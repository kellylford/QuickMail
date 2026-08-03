using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// The Watched Conversations manager: review every watch, rename one, stop watching, or jump to a
/// conversation.
///
/// <para>Modeless (CLAUDE.md's modal-dialog rules): it holds an editable TextBox and opens over the
/// main window's live WebView2, which is the exact combination that deadlocks the UI thread under a
/// screen reader. That costs the automatic Escape/IsCancel behaviour, wired explicitly below.</para>
///
/// <para>No button carries an access-key underscore. The list has first-letter type-ahead, and a
/// bare mnemonic fires without Alt when focus is not in a text field — how "c" closed the folder
/// picker (#418). Alt+G/R/S/C are handled here instead.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001",
    Justification = "_loadCts is cancelled and disposed in OnClosing, which WPF always raises. " +
                    "WPF never calls Dispose on a Window, so implementing IDisposable here would " +
                    "be dead code that nothing invokes.")]
public partial class WatchedConversationsWindow : Window
{
    private readonly WatchedConversationsViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();
    private readonly CancellationTokenSource _loadCts = new();

    public WatchedConversationsWindow(WatchedConversationsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        RegisterLocalCommands();

        _vm.AnnouncementRequested += OnAnnouncement;
        CloseButton.Click += (_, _) => Close();
        WatchList.PreviewKeyDown += WatchList_PreviewKeyDown;
        EditLabelBox.PreviewKeyDown += EditLabelBox_PreviewKeyDown;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FocusPane(WatchList);

        // The one thing about this window that is not self-evident, and the one thing a user could
        // get wrong in a way that matters. Fires on each open rather than once ever: the window is
        // constructed fresh every time, and Hint is the category the user can turn off if they
        // have taken the point.
        AccessibilityHelper.Announce(this,
            "Renaming changes the label only, not which messages are collected.",
            interrupt: false, category: AnnouncementCategory.Hint);

        try
        {
            await _vm.LoadCountsAsync(_loadCts.Token);
        }
        catch (OperationCanceledException) { /* window closed while counting */ }
    }

    private void OnAnnouncement(string text, AnnouncementCategory category) =>
        AccessibilityHelper.Announce(this, text, interrupt: true, category: category);

    // Renaming is entered from a button or a command, so focus has to be moved into the box for
    // the user; and when it ends, focus has to come back to the row that was being renamed.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WatchedConversationsViewModel.IsRenaming)) return;

        if (_vm.IsRenaming)
        {
            Dispatcher.BeginInvoke(() =>
            {
                EditLabelBox.Focus();
                EditLabelBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            Dispatcher.BeginInvoke(() => FocusPane(WatchList),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition(
            "watchmgr.goto", "Mail", "Go to Conversation",
            () => _vm.GoToCommand.Execute(null),
            isAvailable: () => _vm.HasSelection));
        _localRegistry.Register(new CommandDefinition(
            "watchmgr.rename", "Mail", "Rename Watch",
            () => _vm.BeginRenameCommand.Execute(null),
            isAvailable: () => _vm.HasSelection));
        _localRegistry.Register(new CommandDefinition(
            "watchmgr.stop", "Mail", "Stop Watching",
            () => _vm.StopWatchingCommand.Execute(null),
            isAvailable: () => _vm.HasSelection));
        _localRegistry.Register(new CommandDefinition(
            "watchmgr.close", "View", "Close",
            Close));
    }

    // Enter opens the conversation; Delete stops watching. Neither is a letter, so neither
    // competes with the list's type-ahead.
    private void WatchList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm.GoToCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm.StopWatchingCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Inside the rename box, Enter saves and Escape abandons the rename — Escape must NOT reach
    // the window handler and close the window out from under an edit in progress.
    private void EditLabelBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.SaveRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // With Alt held the character arrives as a System key. Test for exactly Alt, not "Alt
        // among others": AltGr reports as Ctrl+Alt, and on layouts where AltGr+G/R/S/C produce
        // letters those keystrokes must reach type-ahead rather than pressing a button.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Not while a rename is in progress: Alt+S would stop watching a row the edit is no longer
        // about, and Alt+G would navigate away mid-edit. Save/Cancel are the only exits from here.
        if (Keyboard.Modifiers == ModifierKeys.Alt && !_vm.IsRenaming)
        {
            switch (key)
            {
                case Key.G: _vm.GoToCommand.Execute(null);         e.Handled = true; return;
                case Key.R: _vm.BeginRenameCommand.Execute(null);  e.Handled = true; return;
                case Key.S: _vm.StopWatchingCommand.Execute(null); e.Handled = true; return;
                case Key.C: Close();                               e.Handled = true; return;
            }
        }

        if (e.Key == Key.F6)
        {
            CycleFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        // Modeless windows get no IsCancel handling, so Escape is wired by hand.
        //
        // PreviewKeyDown TUNNELS root-to-leaf, so this override runs BEFORE the text box's own
        // handler — an earlier version assumed the opposite and closed the whole window out from
        // under an edit in progress. A rename therefore has to be checked for here, not left to
        // the inner handler.
        if (e.Key == Key.Escape)
        {
            if (_vm.IsRenaming)
            {
                _vm.CancelRenameCommand.Execute(null);
                e.Handled = true;
                return;
            }
            Close();
            e.Handled = true;
        }
    }

    private void CycleFocus(bool reverse)
    {
        // The rename panel is only a stop while it is shown; skipping it when collapsed keeps F6
        // from appearing to do nothing.
        UIElement[] panes = _vm.IsRenaming
            ? [WatchList, EditPanel, ButtonBar]
            : [WatchList, ButtonBar];

        int current = -1;
        for (int i = 0; i < panes.Length; i++)
            if (panes[i].IsKeyboardFocusWithin) { current = i; break; }

        int next = reverse
            ? (current <= 0 ? panes.Length - 1 : current - 1)
            : (current >= panes.Length - 1 ? 0 : current + 1);
        FocusPane(panes[next]);
    }

    private void FocusPane(UIElement pane)
    {
        if (ReferenceEquals(pane, WatchList))
        {
            // Focus the selected row rather than the list container, so the screen reader reads
            // the item instead of just the list name (#464).
            if (WatchList.SelectedItem == null && WatchList.Items.Count > 0)
                WatchList.SelectedIndex = 0;
            if (WatchList.ItemContainerGenerator.ContainerFromItem(WatchList.SelectedItem)
                    is ListBoxItem row)
                row.Focus();
            else
                WatchList.Focus();
            return;
        }

        pane.Focus();
        pane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OpenCommandPalette()
    {
        var prev = Keyboard.FocusedElement as IInputElement;
        var palette = new CommandPaletteWindow(_localRegistry) { Owner = this };
        palette.ShowDialog();
        if (prev != null) prev.Focus();
        else FocusPane(WatchList);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.AnnouncementRequested -= OnAnnouncement;
        _vm.PropertyChanged       -= OnViewModelPropertyChanged;
        WatchList.PreviewKeyDown  -= WatchList_PreviewKeyDown;
        EditLabelBox.PreviewKeyDown -= EditLabelBox_PreviewKeyDown;
        base.OnClosed(e);
    }
}
