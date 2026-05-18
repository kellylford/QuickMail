using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel   _vm;
    private readonly IContactService    _contactService;
    private TextBox? _activeAddressBox;
    private CancellationTokenSource? _autocompleteCts;

    public ComposeWindow(ComposeViewModel vm, IContactService contactService)
    {
        _vm = vm;
        _contactService = contactService;
        InitializeComponent();
        DataContext = vm;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusText) && !string.IsNullOrEmpty(vm.StatusText))
                AccessibilityHelper.Announce(this, vm.StatusText);
        };

        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.TextChanged       += AddressBox_TextChanged;
            box.PreviewKeyDown    += AddressBox_PreviewKeyDown;
            box.LostKeyboardFocus += AddressBox_LostKeyboardFocus;
        }

        Loaded  += (_, _) => ToBox.Focus();
        Closing += OnWindowClosing;
    }

    // ── Autocomplete ─────────────────────────────────────────────────────────

    private async void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _activeAddressBox = (TextBox)sender;
        var searchToken = GetCurrentToken(_activeAddressBox.Text, _activeAddressBox.CaretIndex);
        if (searchToken.Length < 1) { AutoCompletePopup.IsOpen = false; return; }

        // Cancel any previous search and create a new cancellation token
        _autocompleteCts?.Cancel();
        _autocompleteCts = new CancellationTokenSource();

        try
        {
            var results = await _contactService.SearchContactsAsync(searchToken, _autocompleteCts.Token);
            if (results.Count == 0) { AutoCompletePopup.IsOpen = false; return; }

            SuggestionList.ItemsSource = results;
            AutoCompletePopup.PlacementTarget = _activeAddressBox;
            AutoCompletePopup.Placement       = PlacementMode.Bottom;
            AutoCompletePopup.IsOpen          = true;

            AccessibilityHelper.Announce(this,
                results.Count == 1 ? "1 suggestion" : $"{results.Count} suggestions");
        }
        catch (OperationCanceledException)
        {
            // Search was cancelled by a more recent keystroke, ignore
        }
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!AutoCompletePopup.IsOpen) return;
        if (e.Key == Key.Down)
        {
            SuggestionList.Focus();
            SuggestionList.SelectedIndex = 0;
            (SuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AutoCompletePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!SuggestionList.IsKeyboardFocusWithin)
                AutoCompletePopup.IsOpen = false;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    internal void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            if (SuggestionList.SelectedItem is ContactModel c) AcceptSuggestion(c);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AutoCompletePopup.IsOpen = false;
            _activeAddressBox?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && SuggestionList.SelectedIndex <= 0)
        {
            AutoCompletePopup.IsOpen = false;
            _activeAddressBox?.Focus();
            e.Handled = true;
        }
    }

    internal void SuggestionList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is ContactModel c) AcceptSuggestion(c);
    }

    private void AcceptSuggestion(ContactModel contact)
    {
        if (_activeAddressBox == null) return;
        var text  = _activeAddressBox.Text;
        var caret = _activeAddressBox.CaretIndex;
        var sub   = text[..caret];
        var lastComma = sub.LastIndexOf(',');
        var lastSemi = sub.LastIndexOf(';');
        var last = Math.Max(lastComma, lastSemi);
        // Determine which separator to use: whatever was last used, or default to semicolon
        var separator = lastSemi > lastComma ? ";" : ",";
        var prefix = last < 0 ? string.Empty : text[..(last + 1)] + " ";
        var suffix = text[caret..].TrimStart();
        _activeAddressBox.Text       = prefix + contact.Display + separator + " " + suffix;
        _activeAddressBox.CaretIndex = (prefix + contact.Display + separator + " ").Length;
        AutoCompletePopup.IsOpen     = false;
        _activeAddressBox.Focus();
    }

    private static string GetCurrentToken(string text, int caretIndex)
    {
        var sub  = text[..caretIndex];
        var last = Math.Max(sub.LastIndexOf(','), sub.LastIndexOf(';'));
        return last < 0 ? sub.Trim() : sub[(last + 1)..].Trim();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // If the message was sent, or nothing was edited, let the window close freely.
        if (_vm.IsSent || !_vm.IsDirty)
            return;

        // Prevent the window from closing until the user decides.
        e.Cancel = true;

        var result = MessageBox.Show(
            this,
            "Do you want to save this message as a draft before closing?",
            "Save Draft?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
            return; // stay open

        if (result == MessageBoxResult.No)
        {
            // Synchronous path — still inside the original Close() call stack.
            // Setting e.Cancel = false lets that Close() proceed normally
            // without a nested Close() call, which would crash WPF.
            Closing -= OnWindowClosing;
            e.Cancel = false;
            return;
        }

        // result == Yes: save the draft first
        await _vm.SaveDraftCommand.ExecuteAsync(null);
        // Only close if the save succeeded (status won't say "failed")
        if (_vm.StatusText.Contains("failed", System.StringComparison.OrdinalIgnoreCase))
            return;

        // After an await the original Close() has already returned (e.Cancel was true),
        // so we need a fresh Close() here to actually close the window.
        Closing -= OnWindowClosing;
        Close();
    }

    // Delete key removes selected attachment from the compose list.
    private void AttachmentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && AttachmentList.SelectedItem is AttachmentModel a)
        {
            _vm.RemoveAttachmentCommand.Execute(a);
            e.Handled = true;
        }
    }

    // Drag-and-drop: accept file drops anywhere on the compose window.
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files)
                _vm.AddAttachmentFromPath(f);
    }

    // Alt+U → Subject field; Alt+M → From combo; Ctrl+V with files → add attachments; Escape → cancel.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V: if the clipboard contains files, paste them as attachments
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsFileDropList())
        {
            foreach (string? f in Clipboard.GetFileDropList())
            {
                if (f != null) _vm.AddAttachmentFromPath(f);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.SystemKey == Key.U && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            SubjectBox.Focus();
            SubjectBox.SelectAll();
            e.Handled = true;
        }
        else if (e.SystemKey == Key.M && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            FromCombo.Focus();
            FromCombo.IsDropDownOpen = true;
            e.Handled = true;
        }
    }
}
