using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class AccountManagerDialog : Window
{
    private readonly AccountManagerViewModel _vm;

    public AccountManagerDialog(AccountManagerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusText) && !string.IsNullOrEmpty(vm.StatusText))
                // The VM classifies its own status text — a refused save is a Result, not progress,
                // and must not be silent for a user who has progress announcements off (#396).
                AccessibilityHelper.Announce(this, vm.StatusText,
                    interrupt: vm.StatusCategory == AnnouncementCategory.Result,
                    category: vm.StatusCategory);
        };
        // #202: warn with a focus-grabbing dialog when a different identity than the one entered signs
        // in (typically an admin approving consent) — the account stays bound to the entered user.
        vm.SignInIdentityMismatch += WarnIdentityMismatch;
        // A PasswordBox cannot be data-bound, so the View has to be told when the VM drops the
        // password itself — otherwise the box keeps showing dots for a password that is gone.
        vm.PasswordCleared += OnPasswordCleared;
    }

    private void OnPasswordCleared()
    {
        if (PasswordBox.Password.Length > 0) PasswordBox.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        // OnClosed, not OnClosing: the window can still cancel a close and stay open.
        _vm.SignInIdentityMismatch -= WarnIdentityMismatch;
        _vm.PasswordCleared -= OnPasswordCleared;
        base.OnClosed(e);
    }

    private void WarnIdentityMismatch(string entered, string actual)
    {
        MessageBox.Show(this,
            $"You entered {entered}, but sign-in completed as {actual}.\n\n" +
            "This usually happens when an administrator signs in to approve access for your " +
            $"organization. The account was not changed. Please sign in again as {entered}.",
            "Different account signed in",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Speaks a field's usage hint on focus. Not AutomationProperties.HelpText: that is read by the
    /// screen reader directly and so ignores the user's announcement preferences, where
    /// AccessibilityHelper.Announce respects AnnounceHints.
    /// </summary>
    private void OnFieldFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Keyboard focus arrives at the SAME control repeatedly through no action of the user's:
        // the window is re-activated after the OAuth sign-in window closes, a ComboBox dropdown
        // closes and hands focus back. Repeating the hint each time is noise, so a hint is spoken
        // only when the focused control actually changed.
        var repeat = ReferenceEquals(e.NewFocus, _lastFocusHinted);
        _lastFocusHinted = e.NewFocus;
        if (repeat) return;

        var hint = HintFor(e.NewFocus);
        if (hint is not null)
            AccessibilityHelper.Announce(this, hint, category: AnnouncementCategory.Hint);
    }

    /// <summary>The control the last focus hint was evaluated for. See <see cref="OnFieldFocused"/>.</summary>
    private IInputElement? _lastFocusHinted;

    private string? HintFor(IInputElement? focused)
    {
        if (ReferenceEquals(focused, AccountNameBox))
            return "Leave blank to use your email address.";
        if (ReferenceEquals(focused, PasswordBox))
            return "Stored in Windows Credential Manager.";
        if (ReferenceEquals(focused, LoginUsernameBox))
            return "Leave blank unless your mail server logs in under a different name than your email address.";
        if (ReferenceEquals(focused, SyncContactsCheckBox))
            return "Pulls this account's contacts into the address book. Enabling asks for a one-time read-only permission.";
        if (ReferenceEquals(focused, SyncCalendarCheckBox))
            return "Shows this account's calendar in the Calendar view.";
        // The ports are in the checkbox's visible Content, but an explicit
        // AutomationProperties.Name OVERRIDES that text — so without this the port guidance existed
        // for sighted users only. It belongs in a hint rather than back in the Name, which must
        // stay a short label.
        if (ReferenceEquals(focused, SmtpImplicitSslCheckBox))
            return "Checked uses port 465. Cleared uses STARTTLS on port 587.";
        if (ReferenceEquals(focused, SignatureBox))
            return "Added to the end of new messages, replies, and forwards.";
        return null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_vm.Accounts.Count == 0)
        {
            // First-run "new account" experience: there's no account row to land on, so put keyboard
            // focus on the Add button — otherwise focus is left unset and the user has to hunt for it.
            NewButton.Focus();
            Keyboard.Focus(NewButton);
            return;
        }

        // Land keyboard focus on the FIRST account item (not the list container, which a screen
        // reader announces as "0 items"). Focusing the item container gives it keyboard focus
        // WITHOUT selecting it — selection happens only on arrow/Space/click — so the user hears
        // the first account and can then choose one. Realize the container first.
        AccountListBox.UpdateLayout();
        if (AccountListBox.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
        {
            first.Focus();
            Keyboard.Focus(first);
        }
        else
        {
            AccountListBox.Focus();
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            _vm.Password = pb.Password;
    }

    private void AppPasswordLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Open in the default browser rather than anything embedded — this is an external provider
        // page where the user will sign in.
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Log($"AccountManagerDialog: failed to open app-password page — {ex.Message}");
        }
        e.Handled = true;
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        var addVm = _vm.CreateAddAccountViewModel();
        var dialog = new AddAccountDialog(addVm) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.CommitNewAccount(addVm.ToAccountModel(), addVm.Password);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    // The "Sync contacts" checkbox applies immediately (issue #256): checking it prompts for consent
    // and pulls contacts; unchecking purges them. Click fires only on real user interaction, so
    // switching accounts (which sets IsChecked programmatically) does not trigger this. async void is
    // the sanctioned pattern for a fire-and-forget UI reaction in a View; the VM method handles its
    // own errors.
    private async void SyncContactsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: { } isChecked })
            await _vm.SetContactSyncAsync(isChecked);
    }

    // The "Sync calendar" checkbox applies immediately (#282), same pattern as contacts above:
    // checking prompts for consent where needed and pulls events; unchecking removes them. Click
    // fires only on real user interaction, so programmatic re-selection does not trigger it.
    private async void SyncCalendarCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: { } isChecked })
            await _vm.SetCalendarSyncAsync(isChecked);
    }
}
