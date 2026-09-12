using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class CommandPaletteWindow : Window
{
    private readonly CommandPaletteViewModel _vm;

    /// <summary>
    /// How the active command is reported to a screen reader. Which one actually sounds right is
    /// decided by ear, not by reading, so all three stay working and the choice is one edit.
    /// </summary>
    internal enum ReportMode
    {
        /// <summary>
        /// UI Automation alone. The filter box declares it controls the list
        /// (<see cref="Controls.ControllingTextBox"/> → <c>ControllerFor</c>), and WPF itself
        /// raises the element-selected event when <c>SelectedIndex</c> changes. The screen reader
        /// speaks the row's name, its position in the set and its shortcut — nothing announced by
        /// hand, and no announcement setting can mute it.
        /// </summary>
        Automation,

        /// <summary>
        /// As <see cref="Automation"/>, plus an explicit element-selected event on the row, for
        /// the case where WPF's own is not reaching the screen reader. Kept separate rather than
        /// folded into the default because raising it on top of WPF's would speak the command
        /// twice — worse than silence, and far harder to diagnose by ear.
        /// </summary>
        AutomationExplicit,

        /// <summary>
        /// QuickMail says it, through <see cref="AccessibilityHelper.Announce"/>. Always
        /// available, but it is a custom announcement the user can switch off.
        /// </summary>
        Announce,
    }

    /// <summary>
    /// The mechanism in force. A field rather than a constant so the tests can exercise the
    /// announcement path without a second build — change the initializer to change the default.
    /// </summary>
    internal static ReportMode Reporting { get; set; } = ReportMode.Automation;

    // Shown without a nested message loop (see ShowModeless), and whether it has already been closed.
    private bool _modeless;
    private bool _dismissed;

    // Typing settles before the active command is reported, so the report does not talk over the
    // screen reader echoing what is being typed. Announcing on every keystroke is half of why
    // palette filtering was removed in 2026-05; arrow keys bypass the timer and report at once.
    //
    // NOTE this gates only what THIS window says — the Announce mode. It cannot gate the
    // selection events WPF raises by itself as the filter re-selects, which is what the two
    // Automation modes rely on; there the timing is WPF's and the screen reader's. If the default
    // mode turns out to speak on every keystroke, that is the reason, and Announce is the mode
    // with the timing under our control.
    private DispatcherTimer? _reportTimer;
    private static readonly TimeSpan ReportDebounce = TimeSpan.FromMilliseconds(300);

    // What was last reported, so an unchanged top match stays silent while the user keeps typing.
    private CommandDefinition? _lastReported;
    private bool _reportedEmpty;

    /// <summary>True when the palette closed because a command was chosen, rather than being dismissed.</summary>
    public bool CommandChosen { get; private set; }

    public CommandPaletteWindow(ICommandRegistry registry)
    {
        _vm = new CommandPaletteViewModel(registry);
        InitializeComponent();
        DataContext = _vm;
        FilterBox.Controls = CommandList;
        FilterBox.TextChanged += OnFilterTextChanged;
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
    }

    /// <summary>
    /// Shows the palette without a nested message loop, for a caller whose focus is inside a WebView2 (a message
    /// body). Opened modally from there, the palette crashed a screen reader, where opened from the message list it
    /// did not (#676, found by ear): the modal loop + WebView2 + assistive technology combination CLAUDE.md
    /// describes for GrabAddresses, with the same fix. The caller handles <see cref="Window.Closed"/> and reads
    /// <see cref="CommandChosen"/>.
    /// </summary>
    public void ShowModeless()
    {
        _modeless = true;
        Show();
    }

    // A modeless palette closes when the user moves to another window, as a modal one could not have been left
    // open behind them.
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_modeless) Dismiss(chosen: false);
    }

    private void Dismiss(bool chosen)
    {
        if (_dismissed) return;   // closing deactivates the window, which would dismiss it a second time
        _dismissed = true;
        _reportTimer?.Stop();
        CommandChosen = chosen;
        if (_modeless) Close();
        else DialogResult = chosen;
    }

    /// <summary>
    /// Stops the report timer however the window went away. <see cref="Dismiss"/> stops it too,
    /// but a palette can be closed without going through Dismiss at all — WPF closes owned
    /// windows when the owner shuts down — and a pending tick would then run against a closed
    /// window, since the timer holds it alive.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _dismissed = true;
        _reportTimer?.Stop();
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Center below the top edge of the owner window (similar to VS Code palette position).
        if (Owner != null)
        {
            Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
            Top  = Owner.Top + 60;
        }

        // Focus goes to the filter box and stays there for the life of the window. Nothing else in
        // the palette is focusable: the list is Focusable="False" and its rows are not tab stops.
        FilterBox.Focus();
        Keyboard.Focus(FilterBox);

        MoveSelection(0);
    }

    // ── Keyboard handling ────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Handled at window level, which is exactly what lets focus stay in the filter box: the
        // keys below are claimed before the TextBox sees them, while every editing key — Home,
        // End, Left, Right, Backspace, Delete, Ctrl+A — falls through to the caret untouched.
        if (e.Key == Key.Escape)
        {
            // Clear the filter first, dismiss only from an empty box. The template picker and the
            // folder picker both work this way; VS Code closes outright.
            if (FilterBox.Text.Length > 0)
            {
                FilterBox.Clear();
                MoveSelection(0);
            }
            else
            {
                Dismiss(chosen: false);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            RunSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            MoveSelection(+1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown)
        {
            MoveSelection(+10);
            e.Handled = true;
        }
        else if (e.Key == Key.PageUp)
        {
            MoveSelection(-10);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            // There is nowhere for Tab to go — the filter box is the only focusable thing in the
            // window — and letting WPF search for a next stop can land focus outside it.
            e.Handled = true;
        }
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        // The ViewModel has already re-ranked and re-selected by the time this runs; all that is
        // left is to report the new top match once the typing settles.
        QueueReport();
    }

    /// <summary>
    /// Selects the row under the mouse, without moving focus.
    ///
    /// <para>WPF's own <c>ListBoxItem</c> click handling selects a row only if it can focus it
    /// first, and these rows are deliberately not focusable — a click must not take focus off the
    /// filter box. So a plain click would otherwise leave the selection where the keyboard put it,
    /// and the double-click below would run a command the user never pointed at.</para>
    ///
    /// <para>PreviewMouseDown rather than PreviewMouseLeftButtonDown: the latter is a Direct
    /// routed event, raised only on the element under the pointer, so a handler on the list never
    /// sees it.</para>
    /// </summary>
    private void CommandList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(CommandList, source) is not ListBoxItem row) return;

        var index = CommandList.ItemContainerGenerator.IndexFromContainer(row);
        if (index < 0) return;

        CommandList.SelectedIndex = index;
        ReportNow();
    }

    private void CommandList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        RunSelected();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    // delta=0 re-selects the current index (used on load and after the filter clears).
    private void MoveSelection(int delta)
    {
        var count = CommandList.Items.Count;
        if (count == 0)
        {
            ReportNow();
            return;
        }

        var index = delta == 0
            ? Math.Max(CommandList.SelectedIndex, 0)
            : Math.Clamp(CommandList.SelectedIndex + delta, 0, count - 1);

        CommandList.SelectedIndex = index;
        CommandList.ScrollIntoView(CommandList.SelectedItem);

        // Arrowing is a deliberate move to one command, so it is reported at once rather than
        // waiting out the typing debounce.
        ReportNow();
    }

    private void RunSelected()
    {
        if (_vm.SelectedCommand != null)
        {
            var cmd = _vm.SelectedCommand;
            // The command runs after the palette has closed, so focus is back in the main window when it does.
            // Commands like view.showProperties call GetFocusedPaneIndex() to determine context; if the palette
            // window still has focus when Execute() runs, the pane index is wrong and the command silently does
            // nothing. It is queued BEFORE closing, so that it runs ahead of anything the owner queues when the
            // palette closes (Input priority runs in order): the owner's focus return has to follow the command.
            Owner?.Dispatcher.InvokeAsync(() => cmd.Execute(), System.Windows.Threading.DispatcherPriority.Input);
            Dismiss(chosen: true);
            return;
        }

        // Enter on an empty result set would otherwise look like the palette had stopped
        // responding. Said again even though the empty list was already announced once as it
        // emptied: this is the answer to a key the user just pressed, not a repeat of the state.
        _reportedEmpty = false;
        ReportNow();
    }

    // ── Reporting the active command ─────────────────────────────────────────────

    /// <summary>Reports after the user stops typing, so the report does not interrupt the typing echo.</summary>
    private void QueueReport()
    {
        _reportTimer ??= CreateReportTimer();
        _reportTimer.Stop();
        _reportTimer.Start();
    }

    private DispatcherTimer CreateReportTimer()
    {
        var timer = new DispatcherTimer { Interval = ReportDebounce };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ReportNow();
        };
        return timer;
    }

    private void ReportNow()
    {
        _reportTimer?.Stop();
        if (_dismissed) return;

        if (_vm.FilteredCommands.Count == 0)
        {
            // Nothing is selected, so there is no row for either mechanism to report. Both paths
            // need this one announcement, or an empty list is indistinguishable from a dead palette.
            if (_reportedEmpty) return;
            _reportedEmpty = true;
            _lastReported  = null;
            AccessibilityHelper.Announce(this, "No matching commands",
                category: AnnouncementCategory.Result);
            return;
        }

        var active = _vm.SelectedCommand;
        if (active is null) return;

        // Typing that narrows the list without changing what Enter would run says nothing new.
        if (ReferenceEquals(active, _lastReported)) return;
        _lastReported  = active;
        _reportedEmpty = false;

        switch (Reporting)
        {
            case ReportMode.Automation:
                // Setting SelectedIndex already raised the event, so there is nothing to do —
                // and by the same token nothing here decides WHEN it is spoken. The debounce and
                // the unchanged-match suppression above apply to the Announce branch only.
                break;
            case ReportMode.AutomationExplicit:
                RaiseElementSelected();
                break;
            case ReportMode.Announce:
                AccessibilityHelper.Announce(this, _vm.ActiveCommandSummary,
                    category: AnnouncementCategory.Result);
                break;
        }
    }

    /// <summary>
    /// Raises the element-selected event on the selected row, which is what a screen reader
    /// listens for on a field that declares it controls a list.
    /// </summary>
    private void RaiseElementSelected()
    {
        if (CommandList.SelectedIndex < 0) return;

        // The list virtualizes, so the row's container — and therefore its automation peer — may
        // not exist yet. ScrollIntoView realizes it, but only on a later layout pass, so look the
        // container up after one. The same deferral this window has always used to reach a row.
        CommandList.ScrollIntoView(CommandList.SelectedItem);
        Dispatcher.InvokeAsync(() =>
        {
            if (_dismissed) return;

            // Read the index here rather than capturing it: arrowing faster than the dispatcher
            // drains queues several of these, and a captured index would report a row that has
            // since been arrowed past — telling the screen reader a command is selected that is not.
            var index = CommandList.SelectedIndex;
            if (index < 0) return;
            if (CommandList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row) return;

            var peer = UIElementAutomationPeer.FromElement(row)
                       ?? UIElementAutomationPeer.CreatePeerForElement(row);
            peer?.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        }, DispatcherPriority.Background);
    }
}
