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
        vm.EmailAddressRejected += OnEmailAddressRejected;
        // #31: removing a parent takes its shared mailboxes with it — confirm, naming them, first.
        vm.ConfirmCascadeRemoval = message =>
            MessageBox.Show(this, message, "Remove shared mailboxes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        // #637: removing an account destroys drafts that exist nowhere else — confirm, saying how
        // many, first. A plain Yes/No MessageBox like the one above; the VM fails closed if unset.
        vm.ConfirmUnsentMailLoss = message =>
            MessageBox.Show(this, message, "Unsent drafts will be deleted",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        // #529 step 4: the convert re-downloads the mailbox and can leave older mail off this PC — confirm
        // first. A plain Yes/No MessageBox (no editable field) is safe here, the same as the cascade
        // confirmation above; an unset callback fails closed in the VM, so nothing converts unconfirmed.
        vm.ConfirmConvertToGraph = message =>
            MessageBox.Show(this, message, "Convert to Microsoft 365",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void AddSharedMailboxButton_Click(object sender, RoutedEventArgs e)
    {
        var addVm = _vm.CreateAddSharedMailboxViewModel();
        // No shared-capable account to read through: don't open a dead-end form the user can't complete.
        // Report why in the manager's status line, where it is announced as a Result.
        if (addVm.ParentOptions.Count == 0)
        {
            _vm.SetStatusOutcome(AddSharedMailboxViewModel.NoEligibleParentMessage);
            return;
        }

        // Commit + persist, then drive the shared-mailbox consent (#31). async void is the sanctioned
        // View pattern for a fire-and-forget UI reaction; CommitNewSharedMailboxAsync handles its own
        // consent failures (leaving the mailbox added but disconnected), so this only guards the add.
        async void OnSharedMailboxAdded(AccountModel shared)
        {
            try { await _vm.CommitNewSharedMailboxAsync(shared); }
            catch (Exception ex) { LogService.Log($"AccountManagerDialog: committing shared mailbox failed — {ex.Message}"); }
        }

        addVm.SharedMailboxAdded += OnSharedMailboxAdded;   // the add window closes itself on the same event
        // Modeless (Show), not ShowDialog: this window has an editable field and can sit over the main
        // window's live WebView2. Note the manager itself is modal (ShowDialog), so the editable field is
        // already inside a nested modal loop — the same position the manager's own text boxes occupy and
        // use without issue; Show() keeps this window from adding a SECOND nested loop. Verify with a
        // screen reader before default-on (spec §7 modal rule; PR review finding 5).
        var window = new AddSharedMailboxWindow(addVm) { Owner = this };
        // Don't let repeated presses stack several add windows.
        AddSharedButton.IsEnabled = false;
        window.Closed += (_, _) =>
        {
            addVm.SharedMailboxAdded -= OnSharedMailboxAdded;   // pair the += so no ghost callback survives
            AddSharedButton.IsEnabled = true;
            // Focus restoration (New-Window-Checklist): a modeless window does not reliably return focus
            // to its launcher, so put it back on the button that opened this dialog.
            AddSharedButton.Focus();
        };
        window.Show();
    }

    private void OnPasswordCleared()
    {
        if (PasswordBox.Password.Length > 0) PasswordBox.Clear();
    }

    /// <summary>
    /// A refused save names the Login username box under Advanced settings, so open it — otherwise
    /// the instruction points at a control that is not in the visual tree, and there is no keyboard
    /// route to it. Focus goes to the address box, which is the field that has to change.
    /// </summary>
    private void OnEmailAddressRejected()
    {
        _vm.IsAdvancedExpanded = true;
        // Let the expander realize its content before moving focus, or Focus() lands on an element
        // that does not exist yet — the same ordering AddAccountDialog uses after expanding.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!UsernameBox.IsVisible) return;
            UsernameBox.Focus();
            Keyboard.Focus(UsernameBox);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    protected override void OnClosed(EventArgs e)
    {
        // OnClosed, not OnClosing: the window can still cancel a close and stay open.
        _vm.SignInIdentityMismatch -= WarnIdentityMismatch;
        _vm.PasswordCleared -= OnPasswordCleared;
        _vm.EmailAddressRejected -= OnEmailAddressRejected;
        base.OnClosed(e);
    }

    private void WarnIdentityMismatch(string entered, string actual)
    {
        // #606: same behavior as Add Account — offer to sign in again as `entered` (Yes re-runs the sign-in,
        // which now succeeds if an admin just granted consent; No returns to the screen) rather than a
        // dead-end OK. Re-invoked via the dispatcher so the current sign-in call unwinds first.
        var answer = MessageBox.Show(this,
            Helpers.AccountSignInMessages.IdentityMismatchPrompt(entered, actual, _vm.IsOAuth2),
            "Different account signed in",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;
        var retry = _vm.IsOAuth2 ? _vm.SignInMicrosoftCommand : _vm.SignInGoogleCommand;
        Dispatcher.BeginInvoke(() => retry.Execute(null), System.Windows.Threading.DispatcherPriority.Background);
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
        // Hints the Add Account dialog shows for the same field live in AccountFieldHints, so the two
        // dialogs cannot describe one control two different ways. The rest are specific to this one.
        if (ReferenceEquals(focused, AccountNameBox))
            return AccountFieldHints.AccountName;
        if (ReferenceEquals(focused, PasswordBox))
            return AccountFieldHints.Password;
        if (ReferenceEquals(focused, LoginUsernameBox))
            return AccountFieldHints.LoginUsername;
        if (ReferenceEquals(focused, SyncContactsCheckBox))
            return "Pulls this account's contacts into the address book. Enabling asks for a one-time read-only permission.";
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

    // The shared-mailbox "Notify me of new mail" checkbox (#31 PR 5) applies immediately and persists,
    // same pattern as the sync toggles. Click fires only on real user interaction, so the programmatic
    // assignment in OnSelectedAccountChanged does not re-trigger it.
    private void SharedNotifyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: { } isChecked })
            _vm.SetNotifyOnNewMail(isChecked);
    }
}
