using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Resources;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// The Check Spelling dialog ("Spelling"). Launched modeless (<c>Show()</c>)
/// per the project's Modal Dialog Rules: it contains an editable text field and
/// continuously updates the owning compose window's editors, so a nested modal
/// message loop is exactly the pattern those rules forbid. Escape and Close are
/// wired explicitly (a modeless window has no DialogResult / IsCancel plumbing).
///
/// Focus choreography reproduces the classic experience: each error is announced
/// as "Not in dictionary: word" and focus lands on the suggestions list with the
/// first suggestion selected, so the screen reader voices the suggestion natively.
///
/// The dialog performs no async loads, so it deliberately has no
/// CancellationTokenSource (New Window Checklist).
/// </summary>
public partial class SpellCheckDialog : Window
{
    private readonly SpellCheckDialogViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();

    public SpellCheckDialog(SpellCheckDialogViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        vm.CheckingSourceChanged += OnCheckingSourceChanged;
        Closed += (_, _) => vm.CheckingSourceChanged -= OnCheckingSourceChanged;

        RegisterLocalCommands();

        // The owner already advanced the VM to the first error before showing
        // the dialog, so presenting is announce + focus only.
        Loaded += (_, _) => PresentCurrent();
    }

    private void OnCheckingSourceChanged(string sourceName) =>
        AccessibilityHelper.Announce(this, string.Format(Strings.SpellCheck_Announce_CheckingSource, sourceName),
            category: AnnouncementCategory.Status);

    /// <summary>Announces the current error and lands focus per the classic flow.</summary>
    private void PresentCurrent()
    {
        AccessibilityHelper.Announce(this, _vm.BuildErrorAnnouncement(),
            category: AnnouncementCategory.Result);
        FocusCurrentInput();
    }

    /// <summary>
    /// Suggestions exist → focus the first suggestion (the screen reader voices
    /// it natively). No suggestions → focus the Change-to box with the original
    /// word selected so typing replaces it.
    /// </summary>
    private void FocusCurrentInput()
    {
        if (_vm.HasSuggestions)
        {
            SuggestionsList.UpdateLayout();
            if (SuggestionsList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
                item.Focus();
            else
                SuggestionsList.Focus();
        }
        else
        {
            ChangeToBox.Focus();
            ChangeToBox.SelectAll();
        }
    }

    /// <summary>Reacts to a verb: re-present while errors remain, close when the scan is done.</summary>
    private void Advance(bool moreErrors)
    {
        if (moreErrors)
        {
            PresentCurrent();
            return;
        }
        // _vm.IsCompleted is now true; the owner's Closed handler shows the
        // completion confirmation and restores focus.
        Close();
    }

    private void Change_Click(object sender, RoutedEventArgs e)          => Advance(_vm.Change());
    private void ChangeAll_Click(object sender, RoutedEventArgs e)       => Advance(_vm.ChangeAll());
    private void Ignore_Click(object sender, RoutedEventArgs e)          => Advance(_vm.Ignore());
    private void IgnoreAll_Click(object sender, RoutedEventArgs e)       => Advance(_vm.IgnoreAll());
    private void AddToDictionary_Click(object sender, RoutedEventArgs e) => Advance(_vm.AddToDictionary());

    private void ReadContext_Click(object sender, RoutedEventArgs e)
    {
        AccessibilityHelper.Announce(this, _vm.ContextLine, interrupt: true,
            category: AnnouncementCategory.Result);
        // The access key moved focus to the button; put it back on the
        // suggestion / change-to input so the review flow is uninterrupted.
        FocusCurrentInput();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            var previousFocus = Keyboard.FocusedElement as IInputElement;
            var palette = new CommandPaletteWindow(_localRegistry) { Owner = this };
            palette.ShowDialog();
            (previousFocus ?? SuggestionsList).Focus();
            return;
        }

        if (e.Key == Key.F6)
        {
            e.Handled = true;
            CycleFocus(backward: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        }
    }

    /// <summary>
    /// F6 ring: context box → suggestions (or Change-to when there are no
    /// suggestions) → button column. Shift+F6 cycles in reverse.
    /// </summary>
    private void CycleFocus(bool backward)
    {
        int current = GetFocusedPaneIndex();
        int next = backward ? (current + 2) % 3 : (current + 1) % 3;
        switch (next)
        {
            case 0: ContextBox.Focus(); break;
            case 1: FocusCurrentInput(); break;
            default: ChangeButton.Focus(); break;
        }
    }

    private int GetFocusedPaneIndex()
    {
        if (ContextBox.IsKeyboardFocusWithin) return 0;
        if (SuggestionsList.IsKeyboardFocusWithin || ChangeToBox.IsKeyboardFocusWithin) return 1;
        return 2;
    }

    /// <summary>
    /// Every dialog action is discoverable in the window-local command palette
    /// (Ctrl+Shift+P), including the verbs that are otherwise access-key only.
    /// </summary>
    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition(
            id: "spelling.change", category: "Spelling", title: "Change",
            execute: () => Advance(_vm.Change())));
        _localRegistry.Register(new CommandDefinition(
            id: "spelling.changeAll", category: "Spelling", title: "Change All",
            execute: () => Advance(_vm.ChangeAll())));
        _localRegistry.Register(new CommandDefinition(
            id: "spelling.ignore", category: "Spelling", title: "Ignore",
            execute: () => Advance(_vm.Ignore())));
        _localRegistry.Register(new CommandDefinition(
            id: "spelling.ignoreAll", category: "Spelling", title: "Ignore All",
            execute: () => Advance(_vm.IgnoreAll())));
        _localRegistry.Register(new CommandDefinition(
            id: "spelling.addToDictionary", category: "Spelling", title: "Add to Dictionary",
            execute: () => Advance(_vm.AddToDictionary())));
        _localRegistry.Register(new CommandDefinition(
            id: "spelling.readInContext", category: "Spelling", title: "Read in Context",
            execute: () => AccessibilityHelper.Announce(this, _vm.ContextLine, interrupt: true,
                category: AnnouncementCategory.Result)));
        _localRegistry.Register(new CommandDefinition(
            id: "spelling.close", category: "Spelling", title: "Close",
            execute: Close));
    }
}
