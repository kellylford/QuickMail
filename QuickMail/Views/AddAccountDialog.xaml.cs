using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
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
        AccountNameBox.Focus();
        Keyboard.Focus(AccountNameBox);
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
                ? $"{label}. IMAP and SMTP settings are not required."
                : $"{label}.",
            category: AnnouncementCategory.Hint);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        // PR 3 ships the Graph option behind the feature gate but not the backend itself.
        // Selecting it and saving is blocked with a clear message until PR 4 lands.
        if (_vm.SelectedBackend?.Kind == BackendKind.MicrosoftGraph)
        {
            MessageBox.Show(
                this,
                "The Microsoft 365 (Graph) backend is not yet implemented in this build. " +
                "Choose Standard IMAP/SMTP, or check for a later version.",
                "Not Yet Available",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
