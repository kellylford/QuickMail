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
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedContact))
            {
                if (vm.SelectedContact != null)
                {
                    NewNameBox.Text = vm.SelectedContact.DisplayName;
                    NewEmailBox.Text = vm.SelectedContact.EmailAddress;
                }
                else
                {
                    NewNameBox.Text = string.Empty;
                    NewEmailBox.Text = string.Empty;
                }
            }
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

    private void NewNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // If user edits the name field and a contact is selected, deselect it to allow adding new contact
        if (_vm.SelectedContact != null && NewNameBox.Text != _vm.SelectedContact.DisplayName)
            _vm.SelectedContact = null;
    }

    private void NewEmailBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // If user edits the email field and a contact is selected, deselect it to allow adding new contact
        if (_vm.SelectedContact != null && NewEmailBox.Text != _vm.SelectedContact.EmailAddress)
            _vm.SelectedContact = null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
