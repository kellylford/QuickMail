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

    public CommandPaletteWindow(ICommandRegistry registry)
    {
        _vm = new CommandPaletteViewModel(registry);
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Center below the top edge of the owner window (similar to VS Code palette position).
        if (Owner != null)
        {
            Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
            Top  = Owner.Top + 60;
        }

        SearchBox.Focus();
    }

    // ── Keyboard handling ─────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            RunSelected();
            e.Handled = true;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Down/Up from the search box activate list navigation and move real keyboard
        // focus to the selected ListBoxItem.  Screen readers then announce the item
        // via standard focus events — no custom UIA properties needed.
        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Printable characters typed while the list has focus are forwarded to the
    /// search box so filtering continues without the user needing to click back.
    /// WPF's PreviewTextInput is locale-aware, so this works for all keyboard layouts.
    /// </summary>
    private void CommandList_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            _vm.SearchText += e.Text;
            SearchBox.Focus();
            SearchBox.CaretIndex = _vm.SearchText.Length;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Additional keys that must be forwarded while the list has focus:
    /// — Backspace  : remove last search character and return to the search box
    /// — Up at top  : return focus to the search box (mirrors Explorer / VS Code)
    /// Up/Down navigation between list items is handled natively by the ListBox.
    /// </summary>
    private void CommandList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            if (_vm.SearchText.Length > 0)
                _vm.SearchText = _vm.SearchText[..^1];
            SearchBox.Focus();
            SearchBox.CaretIndex = _vm.SearchText.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Up && CommandList.SelectedIndex == 0)
        {
            // At the top of the list → return focus to the search box
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void CommandList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        RunSelected();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private void MoveSelection(int delta)
    {
        var count = CommandList.Items.Count;
        if (count == 0) return;

        var index = Math.Clamp(CommandList.SelectedIndex + delta, 0, count - 1);
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
            _vm.ExecuteSelectedCommand.Execute(null);
            DialogResult = true;
        }
    }
}
