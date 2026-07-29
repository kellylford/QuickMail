using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class AddAccountDialog : Window
{
    private readonly AddAccountViewModel _vm;
    private bool _restoreFocusToTestButton;

    public AddAccountDialog(AddAccountViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusText) && !string.IsNullOrEmpty(vm.StatusText))
                // The category lives on the VM (AccountEditorViewModel.StatusCategory) so both
                // account dialogs classify status the same way, and so a VM-side refusal can mark
                // itself a Result without the View having to guess from the wording.
                AccessibilityHelper.Announce(this, vm.StatusText,
                    interrupt: vm.StatusCategory == AnnouncementCategory.Result,
                    category: vm.StatusCategory);
            else if (e.PropertyName == nameof(vm.IsBusy))
                OnIsBusyChanged();
        };
        // #202: a different identity than the one entered completed sign-in (usually an admin approving
        // consent). Warn with a focus-grabbing dialog rather than a passing status line, since a screen
        // reader can miss the soft cue — the account is intentionally left bound to the entered user.
        vm.SignInIdentityMismatch += WarnIdentityMismatch;
        vm.ProviderApplied += OnProviderApplied;
        vm.DiscoveryCompleted += OnDiscoveryCompleted;
        vm.PasswordCleared += OnPasswordCleared;
    }

    /// <summary>
    /// A PasswordBox cannot be data-bound, so when the VM drops the password (switching to an OAuth
    /// provider, say) the box would otherwise keep showing dots for a password that no longer
    /// exists — and the account could then be saved with none.
    /// </summary>
    private void OnPasswordCleared()
    {
        if (PasswordBox.Password.Length > 0) PasswordBox.Clear();
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
    /// Announces what picking a provider actually did. The app-password requirement is delivered
    /// here as a Hint rather than baked into a control's AutomationProperties.Name, so it respects
    /// the user's AnnounceHints preference.
    /// </summary>
    private void OnProviderApplied(MailProvider provider)
    {
        if (provider.IsOther)
        {
            AccessibilityHelper.Announce(this,
                "Enter your server settings under Advanced settings.",
                category: AnnouncementCategory.Hint);
            return;
        }

        var message = provider.RequiresAppPassword
            ? provider.AppPasswordHint
            : $"{provider.DisplayName} settings applied.";
        AccessibilityHelper.Announce(this, message ?? string.Empty, category: AnnouncementCategory.Hint);
    }

    /// <summary>
    /// A lookup finished. The message itself is announced by the StatusText handler above; what this
    /// adds is putting the caret where the work now is — the IMAP host field — when nothing was found.
    /// </summary>
    private void OnDiscoveryCompleted(bool found, string message)
    {
        if (found) return;

        // Advanced was just expanded by the VM. Let the expander realize its content before focusing
        // into it, or Focus() lands on an element that does not exist yet.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ImapHostBox.IsVisible) return;
            ImapHostBox.Focus();
            Keyboard.Focus(ImapHostBox);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Speaks a field's usage hint when it takes keyboard focus.
    ///
    /// These belong here rather than in AutomationProperties.HelpText. HelpText is read by the
    /// screen reader directly, so it ignores the user's announcement preferences entirely;
    /// AccessibilityHelper.Announce routes through AnnounceHints, so a user who has turned hints off
    /// stops hearing them. Same rule as AutomationProperties.Name — instructions are announcements,
    /// not labels.
    /// </summary>
    private void OnFieldFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Keyboard focus arrives at the SAME control repeatedly through no action of the user's:
        // the window is re-activated after the OAuth sign-in window closes, a ComboBox dropdown
        // closes and hands focus back, a disabled button releases it. Repeating the hint each time
        // is noise, so a hint is spoken only when the focused control actually changed.
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
        if (ReferenceEquals(focused, DisplayNameBox))
            return "The name recipients see on messages you send.";
        if (ReferenceEquals(focused, PasswordBox))
            return "Stored in Windows Credential Manager.";
        if (ReferenceEquals(focused, LoginUsernameBox))
            return "Leave blank unless your mail server logs in under a different name than your email address.";
        if (ReferenceEquals(focused, SyncContactsCheckBox))
            return "Check this before signing in, so reading your contacts is part of the same permission.";
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
        // Land on the Provider picker: it is the choice that fills in everything below it, so it
        // comes before the fields it populates.
        ProviderComboBox.Focus();
        Keyboard.Focus(ProviderComboBox);
    }

    private void OnIsBusyChanged()
    {
        if (_vm.IsBusy)
        {
            // Test Connection (and SignIn) disable themselves while running via the
            // generated AsyncRelayCommand.CanExecute. If Test Connection was focused,
            // WPF will move focus to the default button (Add Account) when the button
            // is disabled. Remember the original focus so we can restore it.
            _restoreFocusToTestButton = ReferenceEquals(Keyboard.FocusedElement, TestConnectionButton);
        }
        else if (_restoreFocusToTestButton)
        {
            _restoreFocusToTestButton = false;
            if (TestConnectionButton.IsEnabled)
            {
                TestConnectionButton.Focus();
                Keyboard.Focus(TestConnectionButton);
            }
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            _vm.Password = pb.Password;
    }

    /// <summary>
    /// The network lookup waits for the address to be finished, which leaving the field signals.
    /// The command no-ops when the catalog already matched or the user hand-edited the hosts.
    /// </summary>
    private void UsernameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Leaving the field is what says the address is finished, so it is also where the
        // work-or-school-versus-consumer decision is made — never mid-word.
        _vm.CommitUsername();
        if (_vm.DiscoverSettingsCommand.CanExecute(null))
            _vm.DiscoverSettingsCommand.Execute(null);
    }

    private void AppPasswordLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Open in the default browser rather than anything embedded — this is an external
        // provider page where the user will sign in.
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Log($"AddAccountDialog: failed to open app-password page — {ex.Message}");
        }
        e.Handled = true;
    }

    private void BackendComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var label = _vm.SelectedBackend?.Label;
        if (string.IsNullOrEmpty(label)) return;
        AccessibilityHelper.Announce(
            this,
            _vm.IsGraphBackend
                ? $"{label}. IMAP and SMTP settings are not required."
                : $"{label}.",
            category: AnnouncementCategory.Hint);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        // The server fields live behind a collapsed expander now, so an incomplete account could
        // otherwise be saved without the user ever seeing what was missing. Refuse, say why (the
        // StatusText handler announces it), and put focus on the field that needs filling in.
        // Add Account is the default button, so Enter can fire it straight from the address field —
        // which never lost focus, so the address has not been treated as finished yet.
        _vm.CommitUsername();

        if (!_vm.IsReadyToSave(out var problem))
        {
            // A refused save is the outcome of the action the user just took, not background
            // progress. Announcing it as Status means a user who has turned background-progress
            // announcements off gets silence while focus jumps to a field, with no reason given.
            _vm.SetStatusOutcome(problem);

            // Not just "empty": a login name typed into the address box is also a problem with this
            // field, and sending focus to the IMAP host for it would point at the wrong box (#396).
            if (!EmailAddressValidator.IsValid(_vm.Username))
            {
                UsernameBox.Focus();
                Keyboard.Focus(UsernameBox);
            }
            else if (_vm.IsPasswordAuth && string.IsNullOrEmpty(_vm.Password))
            {
                PasswordBox.Focus();
                Keyboard.Focus(PasswordBox);
            }
            else
            {
                _vm.IsAdvancedExpanded = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ImapHostBox.IsVisible) return;
                    ImapHostBox.Focus();
                    Keyboard.Focus(ImapHostBox);
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // OnClosed, not OnClosing: the window can still cancel a close and stay open. Unsubscribe
        // before disposing so a late event can't reach a disposed VM.
        _vm.SignInIdentityMismatch -= WarnIdentityMismatch;
        _vm.ProviderApplied -= OnProviderApplied;
        _vm.DiscoveryCompleted -= OnDiscoveryCompleted;
        _vm.PasswordCleared -= OnPasswordCleared;
        // Cancels any in-flight settings lookup. ToAccountModel() (read by the caller after
        // ShowDialog returns) touches none of the disposed state.
        _vm.Dispose();
        base.OnClosed(e);
    }
}
