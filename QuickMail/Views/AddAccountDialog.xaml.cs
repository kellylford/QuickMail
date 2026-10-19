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

/// <summary>How the Add Account dialog was left (#834).</summary>
public enum AddAccountOutcome
{
    /// <summary>Cancelled, or closed before an account was added.</summary>
    None,
    // Closing the window at the "added" step (its close box, Alt+F4) counts as Done, like Escape:
    // the account exists, and leaving it should not drop the user into its settings.
    /// <summary>An account was added and the user chose Add another account.</summary>
    AddAnother,
    /// <summary>An account was added and the user chose Done.</summary>
    Done,
    /// <summary>The user chose Import Settings (a new install only); nothing was added.</summary>
    ImportSettings,
}

public partial class AddAccountDialog : Window
{
    private readonly AddAccountViewModel _vm;
    private bool _restoreFocusToTestButton;

    /// <summary>
    /// Saves the account, set by the window that opened this dialog. It runs when Add Account is
    /// pressed and the form is complete, before the dialog moves to its "added" step, so the account
    /// exists whichever way the user leaves from there. Returns the label to report ("… added.").
    /// Unset, the dialog closes with DialogResult true and the owner saves, as it used to.
    /// </summary>
    public Func<string>? CommitAccount { get; set; }

    /// <summary>How the dialog was left; read by the owner after ShowDialog returns.</summary>
    public AddAccountOutcome Outcome { get; private set; } = AddAccountOutcome.None;

    /// <summary>
    /// Shows Add Account over <paramref name="owner"/> until the user is finished: Add another
    /// account opens a fresh form, and anything else ends it. Each account is saved through
    /// <paramref name="accounts"/> the moment Add Account is pressed. Shared by Manage Accounts' New
    /// and by first run, which opens this directly (#834).
    /// </summary>
    /// <returns>How the last dialog was left. Done, or None after Add another and then Cancel, can
    /// follow accounts having been added; <see cref="AccountManagerViewModel.LastAddedAccountId"/>
    /// says whether any were.</returns>
    public static AddAccountOutcome RunUntilDone(Window owner, AccountManagerViewModel accounts,
        bool offerImportSettings = false)
    {
        while (true)
        {
            var addVm = accounts.CreateAddAccountViewModel();
            // Only while there is still nothing to lose by importing over it.
            addVm.OfferImportSettings = offerImportSettings && accounts.Accounts.Count == 0;
            var dialog = new AddAccountDialog(addVm) { Owner = owner };
            dialog.CommitAccount = () =>
            {
                var account = addVm.ToAccountModel();
                accounts.CommitNewAccount(account, addVm.Password);
                return account.AccountLabel;
            };
            dialog.ShowDialog();

            if (dialog.Outcome != AddAccountOutcome.AddAnother) return dialog.Outcome;
        }
    }

    public AddAccountDialog(AddAccountViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            // Once the account is added the form is gone; a lookup or Test Connection still
            // finishing must not speak about fields that are no longer there.
            if (e.PropertyName == nameof(vm.StatusText) && !string.IsNullOrEmpty(vm.StatusText) && !vm.IsAdded)
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
        // #606: the #202 guard kept the account as `entered` and discarded the other sign-in (typically an
        // admin who just approved QuickMail for the org — that approval IS saved). Instead of a dead-end OK,
        // ask whether to sign in again as `entered`: Yes re-runs the sign-in (which now succeeds if consent
        // was just granted); No returns to the account screen. Re-invoked via the dispatcher so the current
        // sign-in call unwinds first (no re-entrancy).
        var answer = MessageBox.Show(this,
            Helpers.AccountSignInMessages.IdentityMismatchPrompt(entered, actual, _vm.IsOAuth2),
            "Different account signed in",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;
        var retry = _vm.IsOAuth2 ? _vm.SignInMicrosoftCommand : _vm.SignInGoogleCommand;
        Dispatcher.BeginInvoke(() => retry.Execute(null), System.Windows.Threading.DispatcherPriority.Background);
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

    /// <summary>Focuses whichever incoming-server host box is currently presented.</summary>
    private void FocusIncomingHostBox()
    {
        var box = ImapHostBox.IsVisible ? ImapHostBox
                : Pop3HostBox.IsVisible ? Pop3HostBox
                : null;
        if (box is null) return;
        box.Focus();
        Keyboard.Focus(box);
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
        // Hints the Account Manager shows for the same field live in AccountFieldHints, so the two
        // dialogs cannot describe one control two different ways. The rest are specific to this one.
        if (ReferenceEquals(focused, AccountNameBox))
            return AccountFieldHints.AccountName;
        if (ReferenceEquals(focused, DisplayNameBox))
            return "The name recipients see on messages you send.";
        if (ReferenceEquals(focused, PasswordBox))
            return AccountFieldHints.Password;
        if (ReferenceEquals(focused, LoginUsernameBox))
            return AccountFieldHints.LoginUsername;
        if (ReferenceEquals(focused, SyncContactsCheckBox))
            return "Check this before signing in, so reading your contacts is part of the same permission.";
        if (ReferenceEquals(focused, SyncCalendarCheckBox))
            return AccountFieldHints.SyncCalendar;
        if (ReferenceEquals(focused, SmtpImplicitSslCheckBox))
            return AccountFieldHints.SmtpImplicitSsl;
        if (ReferenceEquals(focused, Pop3LeaveOnServerCheckBox))
            return AccountFieldHints.Pop3LeaveOnServer;
        if (ReferenceEquals(focused, SignatureBox))
            return AccountFieldHints.Signature;
        return null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Land on the Provider picker: it is the choice that fills in everything below it, so it
        // comes before the fields it populates.
        ProviderComboBox.Focus();
        Keyboard.Focus(ProviderComboBox);

        // First run on a new install: say once that there is another way in. A Hint, after the
        // window and the Provider list have been read, so it neither cuts them off nor speaks
        // for a user who has turned hints off.
        if (_vm.OfferImportSettings)
            Dispatcher.BeginInvoke(new Action(() => AccessibilityHelper.Announce(this,
                    "Already use QuickMail on another computer? Import Settings, Alt+I, brings your accounts across.",
                    category: AnnouncementCategory.Hint)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // #507/#834: the import opens after this dialog has closed (MainWindow.OpenFirstAccountSetup),
    // never inside its modal loop.
    private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Outcome = AddAccountOutcome.ImportSettings;
        DialogResult = false;
        Close();
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
                Dispatcher.BeginInvoke(new Action(FocusIncomingHostBox),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
            return;
        }

        // Focus sits on the now-hidden Add button until the Input-priority move to Done runs; a
        // repeated Enter in that gap must not save the account a second time.
        if (_vm.IsAdded) return;

        if (CommitAccount is null)
        {
            DialogResult = true;
            Close();
            return;
        }

        var label = CommitAccount();
        _vm.MarkAdded(label);

        // The form has just collapsed under the focused Add Account button. Put focus on Done once
        // the panel is laid out, then say what happened: focus first, so the outcome is not cut off
        // by the button being read.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            DoneButton.Focus();
            Keyboard.Focus(DoneButton);
        }), System.Windows.Threading.DispatcherPriority.Input);
        Dispatcher.BeginInvoke(new Action(() =>
            AccessibilityHelper.Announce(this, _vm.AddedMessage, category: AnnouncementCategory.Result)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AddAnotherButton_Click(object sender, RoutedEventArgs e)
    {
        Outcome = AddAccountOutcome.AddAnother;
        DialogResult = true;
        Close();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        Outcome = AddAccountOutcome.Done;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsAdded && Outcome == AddAccountOutcome.None) Outcome = AddAccountOutcome.Done;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // OnClosed, not OnClosing: the window can still cancel a close and stay open. Unsubscribe
        // before disposing so a late event can't reach a disposed VM.
        _vm.SignInIdentityMismatch -= WarnIdentityMismatch;
        _vm.ProviderApplied -= OnProviderApplied;
        _vm.PasswordCleared -= OnPasswordCleared;
        // Cancels any in-flight settings lookup. ToAccountModel() (read by the caller after
        // ShowDialog returns) touches none of the disposed state.
        _vm.Dispose();
        base.OnClosed(e);
    }
}
