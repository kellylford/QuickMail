using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
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
                AccessibilityHelper.Announce(this, vm.StatusText, category: AnnouncementCategory.Status);
        };
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
}
