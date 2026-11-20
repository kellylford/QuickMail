using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Help, Sound Tutorial. Modeless, as the app's other windows over a live reading pane are, so Settings
/// can change the scheme while it's open and the view model catches up each time it's activated.
/// </summary>
public partial class SoundTutorialWindow : Window
{
    private readonly SoundTutorialViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();

    public SoundTutorialWindow(SoundTutorialViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.NoteAnnounced += OnNoteAnnounced;
        Activated += (_, _) => vm.Refresh();
        _localRegistry.Register(new CommandDefinition("soundTutorial.play", "Help", "Play Sound for Selected Event", () => _vm.PlaySelectedCommand.Execute(null)));
        _localRegistry.Register(new CommandDefinition("soundTutorial.close", "Help", "Close Sound Tutorial", Close));
        // Once everything else has settled: opened from the command palette, the palette puts focus back
        // in the main window after this has been shown, which would leave this window open but unfocused.
        Loaded += (_, _) => Dispatcher.InvokeAsync(TakeFocus, DispatcherPriority.ApplicationIdle);
    }

    private void TakeFocus()
    {
        if (!IsVisible) return;   // closed before it got this far
        Activate();
        FocusSelectedRow();
        // After focus, so the first row is read first and this follows it.
        AccessibilityHelper.Announce(this, _vm.Intro, interrupt: false, AnnouncementCategory.Hint);
    }

    private void OnNoteAnnounced(string note) =>
        AccessibilityHelper.Announce(this, note, interrupt: true, AnnouncementCategory.Result);

    /// <summary>Focuses the selected row, not the list, so the row is read.</summary>
    private void FocusSelectedRow()
    {
        if (_vm.SelectedRow is { } row)
        {
            SoundEventList.ScrollIntoView(row);
            SoundEventList.UpdateLayout();
            if (SoundEventList.ItemContainerGenerator.ContainerFromItem(row) is UIElement item)
            {
                item.Focus();
                return;
            }
        }
        SoundEventList.Focus();
    }

    // Space and Enter both play, and neither selects or closes anything. A held key plays once.
    private void SoundEventList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || Keyboard.Modifiers != ModifierKeys.None) return;
        if (!e.IsRepeat) PlayRowOf(e.OriginalSource);
        e.Handled = true;
    }

    private void SoundEventRow_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlayRowOf(sender);

    /// <summary>Plays the row an input came from, which Ctrl+arrows can leave different from the selected one.</summary>
    private void PlayRowOf(object? source)
    {
        if ((source as FrameworkElement)?.DataContext is SoundTutorialRow row) _vm.SelectedRow = row;
        _vm.PlaySelectedCommand.Execute(null);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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
            // Two panes, so forward and back are the same move: the list and the Close button.
            if (SoundEventList.IsKeyboardFocusWithin) CloseButton.Focus();
            else FocusSelectedRow();
            e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var previous = Keyboard.FocusedElement;
            new CommandPaletteWindow(_localRegistry) { Owner = this }.ShowDialog();
            if (IsLoaded) (previous ?? SoundEventList).Focus();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.NoteAnnounced -= OnNoteAnnounced;
        _vm.StopSounds();
        base.OnClosed(e);
    }
}
