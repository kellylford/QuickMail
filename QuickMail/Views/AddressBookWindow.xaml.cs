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
                    vm.NewName = vm.SelectedContact.DisplayName;
                    vm.NewEmail = vm.SelectedContact.EmailAddress;
                }
                else
                {
                    vm.NewName = string.Empty;
                    vm.NewEmail = string.Empty;
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

    private async void ContactList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm.SelectedContact != null)
        {
            var contact = _vm.SelectedContact;
            var result = MessageBox.Show(
                $"Delete {contact.Display}?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                await _vm.DeleteConfirmedAsync();
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

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedContact == null) return;
        var contact = _vm.SelectedContact;
        var result = MessageBox.Show(
            $"Delete {contact.Display}?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            await _vm.DeleteConfirmedAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
