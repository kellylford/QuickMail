using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Asks for a folder's new name (#1083). Modeless and unowned, like the Rules window: the box is
/// editable and the window opens over the main window's live WebView2, which is the combination
/// CLAUDE.md's Modal Dialog Rules forbid as a <c>ShowDialog</c>; and an owned window has its title
/// read as the main window's by some screen readers. So Escape, Cancel and the command palette are
/// wired here, and the opener tracks the instance, places it, closes it with the main window and
/// puts focus back when it closes.
/// </summary>
public partial class RenameFolderWindow : Window
{
    private readonly RenameFolderViewModel _vm;
    private readonly CommandRegistry _registry = new();

    /// <summary>The rename that happened, when the window closed because of one; null otherwise.</summary>
    public FolderRenameResult? Result { get; private set; }

    public RenameFolderWindow(RenameFolderViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        _vm.Renamed += result =>
        {
            Result = result;
            Close();
        };
        _vm.Cancelled += Close;
        _vm.AnnouncementRequested += (text, category) =>
            AccessibilityHelper.Announce(this, text, interrupt: category == AnnouncementCategory.Result, category: category);
        _vm.SelectNameRequested += () => Dispatcher.InvokeAsync(FocusName, DispatcherPriority.Input);

        RegisterPaletteCommands();

        Loaded += (_, _) => FocusName();
    }

    private void FocusName()
    {
        NameBox.Focus();
        Keyboard.Focus(NameBox);
        NameBox.SelectAll();
    }

    private void RegisterPaletteCommands()
    {
        _registry.Register(new CommandDefinition(
            id: "renameFolder.rename", category: "Mail", title: "Rename",
            execute: () => { if (_vm.RenameCommand.CanExecute(null)) _vm.RenameCommand.Execute(null); },
            defaultKey: Key.Enter, defaultModifiers: ModifierKeys.Control));
        _registry.Register(new CommandDefinition(
            id: "renameFolder.cancel", category: "Mail", title: "Cancel",
            execute: () => { if (_vm.CancelCommand.CanExecute(null)) _vm.CancelCommand.Execute(null); },
            defaultKey: Key.Escape, defaultModifiers: ModifierKeys.None));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+P — command palette (framework-level; cannot dispatch through itself).
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            new CommandPaletteWindow(_registry) { Owner = this }.ShowDialog();
            return;
        }

        // F6 / Shift+F6 — cycle the logical stops: the name, then the Rename button.
        if (e.Key == Key.F6)
        {
            e.Handled = true;
            if (NameBox.IsKeyboardFocusWithin) RenameButton.Focus();
            else FocusName();
            return;
        }

        // Registry dispatch, so Escape (cancel) and Ctrl+Enter (rename) go through the commands the
        // palette lists. Escape is ignored while the server is renaming: the rename is on its way and
        // cannot be called back, so closing would only hide how it ended.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var cmd = _registry.FindByGesture(key, Keyboard.Modifiers);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            e.Handled = true;
            cmd.Execute();
        }
    }
}
