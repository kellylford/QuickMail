using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Lists the sound scheme gallery and fetches one scheme (#937). Shown with ShowDialog from Settings;
/// on Add it closes with <see cref="DownloadedPath"/> set to a checked file for Settings to import.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "The window cancels and disposes _cts on close; WPF never calls Dispose on a Window.")]
public partial class SoundGalleryWindow : Window
{
    private readonly SoundGalleryViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();
    private CancellationTokenSource? _cts;
    private bool _closed;

    public SoundGalleryWindow(SoundGalleryViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.AnnouncementRequested += OnAnnouncement;
        RegisterLocalCommands();
        Loaded += async (_, _) =>
        {
            SchemeList.Focus();
            await LoadAsync();
        };
    }

    /// <summary>The checked scheme file Add fetched, for the owner to import and then delete.</summary>
    public string? DownloadedPath { get; private set; }

    /// <summary>The gallery's listing for <see cref="DownloadedPath"/>, so the owner can remember where it came from.</summary>
    public SoundGalleryEntry? DownloadedEntry { get; private set; }

    private void OnAnnouncement(string text, AnnouncementCategory category) =>
        AccessibilityHelper.Announce(this, text, interrupt: category == AnnouncementCategory.Result, category);

    private CancellationToken NewToken()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private async Task LoadAsync()
    {
        try
        {
            await _vm.LoadAsync(NewToken());
        }
        catch (OperationCanceledException)
        {
            return;   // closed while loading
        }
        FocusList();
    }

    private void FocusList()
    {
        if (_vm.SelectedEntry is { } entry)
        {
            SchemeList.ScrollIntoView(entry);
            SchemeList.UpdateLayout();
            if (SchemeList.ItemContainerGenerator.ContainerFromItem(entry) is UIElement row)
            {
                row.Focus();
                return;
            }
        }
        // Empty after a failure: Reload is the way on.
        if (_vm.Entries.Count == 0) ReloadButton.Focus();
        else SchemeList.Focus();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await AddAsync();

    private async Task AddAsync()
    {
        var entry = _vm.SelectedEntry?.Entry;
        string? path;
        try
        {
            path = await _vm.DownloadSelectedAsync(NewToken());
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_closed)
        {
            // Closed while the download finished: nobody will import it, and DialogResult can't be set now.
            if (path is not null) SoundGalleryService.DeleteDownload(path);
            return;
        }
        if (path is null)
        {
            FocusList();
            return;
        }
        DownloadedPath = path;
        DownloadedEntry = entry;
        DialogResult = true;
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition("gallery.add", "Settings", "Add or Update Sound Scheme",
            () => _ = AddAsync(), isAvailable: () => _vm.CanAdd));
        _localRegistry.Register(new CommandDefinition("gallery.reload", "Settings", "Reload the Gallery",
            () => _ = LoadAsync(), isAvailable: () => !_vm.IsBusy));
        _localRegistry.Register(new CommandDefinition("gallery.close", "Settings", "Close Gallery", Close));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            // Three panes: the list, the selected scheme's details, and the buttons.
            var reverse = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var pane = SchemeList.IsKeyboardFocusWithin ? 0 : DetailsBox.IsKeyboardFocusWithin ? 1 : 2;
            var next = (pane + (reverse ? 2 : 1)) % 3;
            if (next == 1 && !DetailsBox.IsVisible) next = reverse ? 0 : 2;
            if (next == 0) FocusList();
            else if (next == 1) FocusDetails();
            else (AddButton.IsEnabled ? AddButton : ReloadButton).Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var previous = Keyboard.FocusedElement;
            new CommandPaletteWindow(_localRegistry) { Owner = this }.ShowDialog();
            if (IsLoaded) (previous ?? SchemeList).Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is ListBoxItem)
        {
            // Enter on a scheme adds or updates it, as the button does; on one already added, says so
            // rather than doing nothing.
            if (_vm.CanAdd) _ = AddAsync();
            else _vm.SayAlreadyAdded();
            e.Handled = true;
        }
    }

    /// <summary>Focuses the details at their start, so reading begins with the description.</summary>
    private void FocusDetails()
    {
        DetailsBox.Focus();
        DetailsBox.CaretIndex = 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _vm.AnnouncementRequested -= OnAnnouncement;
        base.OnClosed(e);
    }
}
