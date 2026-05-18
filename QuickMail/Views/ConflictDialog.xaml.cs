using System.Windows;

namespace QuickMail.Views;

public partial class ConflictDialog : Window
{
    public ConflictDialog(string conflictingCommandTitle)
    {
        InitializeComponent();
        ConflictingCommandText.Text = conflictingCommandTitle;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true; // "Try Again"
        Close();
    }
}
