using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class CommandPaletteWindow : Window
{
    private readonly CommandPaletteViewModel _vm;

    // Shown without a nested message loop (see ShowModeless), and whether it has already been closed.
    private bool _modeless;
    private bool _dismissed;

    /// <summary>True when the palette closed because a command was chosen, rather than being dismissed.</summary>
    public bool CommandChosen { get; private set; }

    public CommandPaletteWindow(ICommandRegistry registry)
    {
        _vm = new CommandPaletteViewModel(registry);
        InitializeComponent();
        DataContext = _vm;
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
        CommandChosen = chosen;
        if (_modeless) Close();
        else DialogResult = chosen;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Center below the top edge of the owner window (similar to VS Code palette position).
        if (Owner != null)
        {
            Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
            Top  = Owner.Top + 60;
        }

        // Select and focus the first item so the screen reader announces it immediately.
        MoveSelection(0);
    }

    // ── Keyboard handling ────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss(chosen: false);
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
    }

    private void CommandList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        RunSelected();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    // delta=0 re-focuses the currently selected index (used on load).
    private void MoveSelection(int delta)
    {
        var count = CommandList.Items.Count;
        if (count == 0) return;

        var index = delta == 0
            ? Math.Max(CommandList.SelectedIndex, 0)
            : Math.Clamp(CommandList.SelectedIndex + delta, 0, count - 1);

        CommandList.SelectedIndex = index;
        CommandList.ScrollIntoView(CommandList.SelectedItem);

        // Defer focus until after the layout pass so the container is materialised.
        Dispatcher.InvokeAsync(() =>
        {
            if (CommandList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
                item.Focus();
        }, DispatcherPriority.Background);
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
        }
    }
}
