using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "_testCts is cancelled in OnClosing and disposed in OnClosed; WPF never calls Dispose on a Window, so implementing IDisposable would be dead code.")]
public partial class ConnectionDiagnosticsWindow : Window
{
    private readonly ConnectionDiagnosticsViewModel _vm;
    private readonly IUiDispatcher? _ui;
    private IInputElement? _restoreFocusTo;
    private bool _accountHintAnnounced;

    // Per the New Window Checklist: async work is owned by a field-scoped CTS, cancelled in
    // OnClosing. Without it, closing the window mid-test left the probe running and holding the
    // process-wide one-at-a-time semaphore for up to 45 seconds, blocking the next test.
    private CancellationTokenSource? _testCts;

    // A palette scoped to THIS window. Passing the global registry offered only main-window
    // commands, so none of this window's own actions were reachable from it — the checklist
    // requires actions without a hotkey to be discoverable here.
    private readonly CommandRegistry _localRegistry = new();

    public ConnectionDiagnosticsWindow(
        ConnectionTruthProbe? probe,
        Func<IReadOnlyList<AccountModel>> accountsSource,
        IUiDispatcher? uiDispatcher = null)
    {
        InitializeComponent();

        _ui       = uiDispatcher;
        _vm       = new ConnectionDiagnosticsViewModel(probe, accountsSource);
        DataContext = _vm;

        _vm.TestTokenProvider = () =>
        {
            _testCts?.Dispose();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            return _testCts.Token;
        };
        _vm.CopyRequested     += OnCopyRequested;
        _vm.SaveRequested     += OnSaveRequested;
        _vm.AnnounceRequested += OnAnnounceRequested;

        ConnectionJournal.EventRecorded += OnJournalEvent;

        RegisterLocalCommands();

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
        var affectsStatus = evt.Kind is ConnectionEventKind.Status or ConnectionEventKind.Probe;

        void Apply()
        {
            _vm.AppendEvent(evt);
            if (affectsStatus) _vm.RefreshStatusOnly();
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

    private void RegisterLocalCommands()
    {
        void Add(string id, string title, Action execute) =>
            _localRegistry.Register(new CommandDefinition(
                id: id, category: "Diagnostics", title: title, execute: execute));

        Add("diagnostics.test",    "Test This Account", () => { if (_vm.TestAccountCommand.CanExecute(null)) _vm.TestAccountCommand.Execute(null); });
        Add("diagnostics.copy",    "Copy Report",       () => _vm.CopyReportCommand.Execute(null));
        Add("diagnostics.save",    "Save Report",       () => _vm.SaveReportCommand.Execute(null));
        Add("diagnostics.refresh", "Refresh",           () => _vm.RefreshNowCommand.Execute(null));
        Add("diagnostics.close",   "Close Window",      Close);
    }

    private void OpenCommandPalette()
    {
        new CommandPaletteWindow(_localRegistry) { Owner = this }.ShowDialog();
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

        // Status, not Hint: this is where focus went, not advice. A user who turns hints off still
        // needs to know which pane they landed in, or F6 becomes silent movement.
        AccessibilityHelper.Announce(this, name, category: AnnouncementCategory.Status);
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────────

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Cancel in OnClosing, not OnClosed: an in-flight probe holds a process-wide semaphore, and
        // releasing it promptly is what lets the next test run.
        try { _testCts?.Cancel(); } catch { }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Pair every += with a -= : the journal's static event would otherwise keep this window
        // (and its whole visual tree) alive for the rest of the session.
        ConnectionJournal.EventRecorded -= OnJournalEvent;
        _vm.CopyRequested     -= OnCopyRequested;
        _vm.SaveRequested     -= OnSaveRequested;
        _vm.AnnounceRequested -= OnAnnounceRequested;

        _testCts?.Dispose();
        _testCts = null;

        base.OnClosed(e);

        try { _restoreFocusTo?.Focus(); } catch { /* the target may be gone */ }
    }
}
