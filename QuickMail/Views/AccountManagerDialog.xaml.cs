using System.Windows;
using System.Windows.Controls;
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
}
