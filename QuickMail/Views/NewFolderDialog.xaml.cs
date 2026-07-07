using System.Windows;
using System.Windows.Input;

namespace QuickMail.Views;

/// <summary>
/// Simple dialog that collects a new folder name from the user.
/// Set <see cref="ParentFolderName"/> before showing to display a context label.
/// </summary>
public partial class NewFolderDialog : Window
{
    /// <summary>Display name of the parent folder shown as a label.</summary>
    public string ParentFolderName
    {
        get => ParentLabel.Text;
        set => ParentLabel.Text = string.IsNullOrEmpty(value)
            ? string.Empty
            : $"Parent folder: {value}";
    }

    /// <summary>The folder name entered by the user. Only meaningful when DialogResult == true.</summary>
    public string FolderName => FolderNameBox.Text.Trim();

    public NewFolderDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => FolderNameBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Commit();

    private void FolderNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
    }

    private void Commit()
    {
        if (string.IsNullOrWhiteSpace(FolderNameBox.Text)) return;
        DialogResult = true;
    }
}
