using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Shown by the start that applied a settings import (issue #507): the report, and the accounts
/// still to sign in. Modeless — it opens over the main window and its live reading pane, and it
/// holds a password box (CLAUDE.md modal dialog rules).
/// </summary>
public partial class SettingsImportedWindow : Window
{
    private readonly SettingsImportedViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();
    private IInputElement? _restoreFocusTo;
    private bool _signInHintGiven;

    public SettingsImportedWindow(SettingsImportedViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.AnnouncementRequested += OnAnnouncementRequested;
        _vm.PasswordConsumed += OnPasswordConsumed;
        PasswordBox.PasswordChanged += (_, _) => _vm.Password = PasswordBox.Password;
        AccountList.GotKeyboardFocus += OnAccountListFocused;
        PreviewKeyDown += OnPreviewKeyDown;
        RegisterLocalCommands();
        Loaded += (_, _) => ReportBox.Focus();
    }

    public void SetFocusRestoreTarget(IInputElement? element) => _restoreFocusTo = element;

    private void OnAnnouncementRequested(string text, AnnouncementCategory category) =>
        AccessibilityHelper.Announce(this, text, category: category);

    private void OnPasswordConsumed() => PasswordBox.Clear();

    // The instruction is a hint on first arrival, not part of the list's name (CLAUDE.md).
    private void OnAccountListFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_signInHintGiven) return;
        _signInHintGiven = true;
        AccessibilityHelper.Announce(this,
            "Your settings arrived without passwords. Choose an account and press Enter to sign in.",
            category: AnnouncementCategory.Hint);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.Report);
            AccessibilityHelper.Announce(this, "Report copied.", category: AnnouncementCategory.Result);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            AccessibilityHelper.Announce(this, $"Could not copy the report. {ex.Message}", category: AnnouncementCategory.Result);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (ctrl && shift && e.Key == Key.P)
        {
            e.Handled = true;
            new CommandPaletteWindow(_localRegistry) { Owner = this }.ShowDialog();
        }
        else if (e.Key == Key.F6)
        {
            e.Handled = true;
            CyclePanes(forward: !shift);
        }
        else if (e.Key == Key.Enter && AccountList.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            // A password account needs the password first; Enter takes the user to it.
            if (_vm.PasswordNeeded && PasswordBox.Password.Length == 0) PasswordBox.Focus();
            else if (_vm.SignInCommand.CanExecute(null)) _vm.SignInCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && PasswordBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            if (_vm.SignInCommand.CanExecute(null)) _vm.SignInCommand.Execute(null);
        }
    }

    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition(id: "settingsImported.signIn", category: "Settings",
            title: "Sign In", execute: () => { if (_vm.SignInCommand.CanExecute(null)) _vm.SignInCommand.Execute(null); }));
        _localRegistry.Register(new CommandDefinition(id: "settingsImported.copy", category: "Settings",
            title: "Copy Report", execute: () => Copy_Click(this, new RoutedEventArgs())));
        _localRegistry.Register(new CommandDefinition(id: "settingsImported.close", category: "Settings",
            title: "Close Window", execute: Close));
    }

    // F6 ring: report → accounts → sign-in → buttons. The account stops only when there are accounts.
    private void CyclePanes(bool forward)
    {
        var panes = _vm.HasRows
            ? new UIElement[] { ReportBox, AccountList, SignInPanel, ButtonRow }
            : new UIElement[] { ReportBox, ButtonRow };
        var current = Array.FindIndex(panes, p => p.IsKeyboardFocusWithin);
        var next = current < 0 ? 0 : forward ? (current + 1) % panes.Length : (current - 1 + panes.Length) % panes.Length;
        var target = panes[next];
        if (target == AccountList)
        {
            AccountList.UpdateLayout();
            if (AccountList.ItemContainerGenerator.ContainerFromItem(_vm.SelectedRow) is UIElement item) item.Focus();
            else AccountList.Focus();
        }
        else if (target == SignInPanel)
        {
            if (_vm.PasswordNeeded) PasswordBox.Focus(); else SignInButton.Focus();
        }
        else if (target == ButtonRow)
        {
            CopyButton.Focus();
        }
        else
        {
            target.Focus();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.AnnouncementRequested -= OnAnnouncementRequested;
        _vm.PasswordConsumed -= OnPasswordConsumed;
        _vm.Dispose();
        base.OnClosed(e);
        try { _restoreFocusTo?.Focus(); } catch (InvalidOperationException) { /* the target may be gone */ }
    }
}
