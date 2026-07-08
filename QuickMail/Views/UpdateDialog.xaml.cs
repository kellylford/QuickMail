using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;

namespace QuickMail.Views;

/// <summary>
/// Shown for self-updating (installed) copies when the Help update entry is activated.
/// Tells the user the update installs itself on relaunch and offers to restart now.
/// Escape / Exit dismiss the dialog; the update still applies on the next normal exit.
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly Func<Task> _restartToUpdate;

    public UpdateDialog(string version, string whatsNewUrl, Func<Task> restartToUpdate)
    {
        _restartToUpdate = restartToUpdate;
        InitializeComponent();
        MessageText.Text =
            $"Version {version} of QuickMail is available and will be installed automatically " +
            "the next time QuickMail starts.";
        WhatsNewLink.NavigateUri = new Uri(whatsNewUrl);
        Loaded += (_, _) => RestartButton.Focus();
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        // On success this call never returns — the app exits and relaunches updated.
        // On failure the VM announces the outcome; close so the user is not left staring
        // at a dialog whose primary action just declined to act.
        RestartButton.IsEnabled = false;
        await _restartToUpdate();
        Close();
    }

    private void WhatsNewLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // All ShellExecute launches go through the allow-list. See ExternalUriPolicy.
        Helpers.ExternalUriPolicy.TryOpenExternal(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
