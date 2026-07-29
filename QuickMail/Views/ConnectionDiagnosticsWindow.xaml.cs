using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Shows what the app believes about each account's connection, what an independent probe found,
/// and the live connection journal — plus a one-press export so a report can be sent back.
///
/// Modeless by deliberate choice: the main window hosts a live WebView2 reading pane, and a modal
/// dialog opened over it can hard-deadlock the UI thread with a screen reader active (see the modal
/// dialog rules in CLAUDE.md). Escape and Close are therefore wired explicitly.
/// </summary>
public partial class ConnectionDiagnosticsWindow : Window
{
    private readonly ConnectionDiagnosticsViewModel _vm;
    private readonly ICommandRegistry? _registry;
    private readonly IUiDispatcher? _ui;
    private IInputElement? _restoreFocusTo;
    private bool _accountHintAnnounced;

    public ConnectionDiagnosticsWindow(
        ConnectionTruthProbe? probe,
        Func<IReadOnlyList<AccountModel>> accountsSource,
        ICommandRegistry? registry = null,
        IUiDispatcher? uiDispatcher = null)
    {
        InitializeComponent();

        _registry = registry;
        _ui       = uiDispatcher;
        _vm       = new ConnectionDiagnosticsViewModel(probe, accountsSource);
        DataContext = _vm;

        _vm.CopyRequested     += OnCopyRequested;
        _vm.SaveRequested     += OnSaveRequested;
        _vm.AnnounceRequested += OnAnnounceRequested;

        ConnectionJournal.EventRecorded += OnJournalEvent;

        CloseButton.Click     += (_, _) => Close();
        AccountList.GotFocus  += OnAccountListGotFocus;
        PreviewKeyDown        += OnPreviewKeyDown;

        Loaded += (_, _) =>
        {
            AccountList.Focus();
            if (AccountList.SelectedItem != null)
                AccountList.ScrollIntoView(AccountList.SelectedItem);
        };
    }

    /// <summary>
    /// Where focus should return when this window closes. WPF's default return-to-owner is not
    /// reliable for virtualised list items, so the caller captures the element explicitly.
    /// </summary>
    public void SetFocusRestoreTarget(IInputElement? element) => _restoreFocusTo = element;

    // ── Journal → UI ─────────────────────────────────────────────────────────────

    private void OnJournalEvent(ConnectionEvent evt)
    {
        // Fires on whatever thread produced the event (IDLE watchers and probes run on the thread
        // pool), so marshal before touching the bound collection.
        void Apply()
        {
            _vm.AppendEvent(evt);
            _vm.RefreshStatusOnly();
        }

        if (_ui != null) _ui.Post(Apply);
        else Dispatcher.BeginInvoke(Apply);
    }

    // ── VM requests the View can satisfy ─────────────────────────────────────────

    private void OnCopyRequested(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex)
        {
            // Clipboard access can fail transiently when another process holds it. Say so rather
            // than letting the announcement claim a copy that did not happen.
            AccessibilityHelper.Announce(
                this, $"Could not copy the report. {ex.Message}", category: AnnouncementCategory.Result);
        }
    }

    private void OnSaveRequested(string text)
    {
        var dialog = new SaveFileDialog
        {
            Title      = "Save connection report",
            FileName   = $"quickmail-connection-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter     = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, text);
            AccessibilityHelper.Announce(
                this, $"Report saved to {Path.GetFileName(dialog.FileName)}.",
                category: AnnouncementCategory.Result);
        }
        catch (Exception ex)
        {
            AccessibilityHelper.Announce(
                this, $"Could not save the report. {ex.Message}", category: AnnouncementCategory.Result);
        }
    }

    private void OnAnnounceRequested(string text, AnnouncementCategory category) =>
        AccessibilityHelper.Announce(this, text, category: category);

    // ── Keyboard ─────────────────────────────────────────────────────────────────

    private void OnAccountListGotFocus(object sender, RoutedEventArgs e)
    {
        // Deliver the usage tip as a hint on first focus rather than baking it into the list's
        // AutomationProperties.Name, so it respects the user's hint preference and is not repeated
        // on every visit to the list.
        if (_accountHintAnnounced) return;
        _accountHintAnnounced = true;
        AccessibilityHelper.Announce(
            this, "Press Enter to test whether the selected account is really reachable.",
            category: AnnouncementCategory.Hint);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift;

        if (e.Key == Key.Escape)
        {
            // Don't steal Escape from an open ComboBox dropdown — that is the documented
            // trade-off of going modeless (CLAUDE.md).
            if (FilterCombo.IsDropDownOpen) return;
            Close();
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == Key.P)
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            CyclePanes(forward: !shift);
            e.Handled = true;
            return;
        }

        // Enter on the account list runs the test, matching the announced hint.
        if (e.Key == Key.Enter && AccountList.IsKeyboardFocusWithin)
        {
            if (_vm.TestAccountCommand.CanExecute(null))
                _vm.TestAccountCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpenCommandPalette()
    {
        if (_registry == null) return;
        new CommandPaletteWindow(_registry) { Owner = this }.ShowDialog();
    }

    // The F6 ring: accounts → events → buttons. Announced on arrival so the move is audible.
    private void CyclePanes(bool forward)
    {
        var panes = new (FrameworkElement Target, string Name)[]
        {
            (AccountList, "Accounts"),
            (EventList,   "Connection events"),
            (ButtonRow,   "Buttons"),
        };

        var current = 0;
        for (var i = 0; i < panes.Length; i++)
        {
            if (panes[i].Target.IsKeyboardFocusWithin) { current = i; break; }
        }

        var next = forward
            ? (current + 1) % panes.Length
            : (current - 1 + panes.Length) % panes.Length;

        var (target, name) = panes[next];
        if (target == ButtonRow)
            TestButton.Focus();
        else
            target.Focus();

        AccessibilityHelper.Announce(this, name, category: AnnouncementCategory.Hint);
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        // Pair every += with a -= : the journal's static event would otherwise keep this window
        // (and its whole visual tree) alive for the rest of the session.
        ConnectionJournal.EventRecorded -= OnJournalEvent;
        _vm.CopyRequested     -= OnCopyRequested;
        _vm.SaveRequested     -= OnSaveRequested;
        _vm.AnnounceRequested -= OnAnnounceRequested;

        base.OnClosed(e);

        try { _restoreFocusTo?.Focus(); } catch { /* the target may be gone */ }
    }
}
