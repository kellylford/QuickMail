using System.Windows;
using System.Windows.Controls;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class AddAccountDialog : Window
{
    private readonly AddAccountViewModel _vm;

    public AddAccountDialog(AddAccountViewModel vm)
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
}
