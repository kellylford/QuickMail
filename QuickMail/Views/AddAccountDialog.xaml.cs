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
                AccessibilityHelper.Announce(this, vm.StatusText, category: AnnouncementCategory.Status);
            else if (e.PropertyName == nameof(vm.IsBusy))
                OnIsBusyChanged();
        };
        // #202: a different identity than the one entered completed sign-in (usually an admin approving
        // consent). Warn with a focus-grabbing dialog rather than a passing status line, since a screen
        // reader can miss the soft cue — the account is intentionally left bound to the entered user.
        vm.SignInIdentityMismatch += WarnIdentityMismatch;
        vm.ProviderApplied += OnProviderApplied;
        vm.DiscoveryCompleted += OnDiscoveryCompleted;
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
        // Cancels any in-flight settings lookup. ToAccountModel() (read by the caller after
        // ShowDialog returns) touches none of the disposed state.
        _vm.Dispose();
        base.OnClosed(e);
    }
}
