using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Resources;
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
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Land on the Account Type picker first when it's shown (more than one backend available),
        // so the choice that changes which fields matter comes before those fields. When there's
        // only one backend the picker is hidden, so start on Account Name as before.
        var first = _vm.ShowBackendPicker ? (Control)BackendComboBox : AccountNameBox;
        first.Focus();
        Keyboard.Focus(first);
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

    private void BackendComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var label = _vm.SelectedBackend?.Label;
        if (string.IsNullOrEmpty(label)) return;
        AccessibilityHelper.Announce(
            this,
            _vm.IsGraphBackend
                ? string.Format(Strings.AddAccount_BackendSelected_NoImapSmtp_Announce, label)
                : string.Format(Strings.AddAccount_BackendSelected_Announce, label),
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
}
