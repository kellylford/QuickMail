using System.Windows;
using System.Windows.Input;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class AddressBookWindow : Window
{
    private readonly AddressBookViewModel _vm;

    public AddressBookWindow(AddressBookViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            await vm.LoadAsync();
            SearchBox.Focus();
        };
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ContactList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm.HasSelectedContact)
        {
            _vm.DeleteContactCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void NewEmailBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            _vm.AddContactCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
