using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;

namespace QuickMail.Views;

/// <summary>
/// Shown for self-updating (installed) copies when the Help update entry is activated.
/// Tells the user the update installs itself on relaunch and offers to restart now.
/// Escape / Exit dismiss the dialog; the update still applies on the next normal exit.
/// Dismissal also cancels a pending restart, so a restart requested while the download
/// is still in flight can never fire after the user has moved on.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "_restartCts is cancelled and disposed in the Closed handler; WPF never calls Dispose on Window instances, so implementing IDisposable here would be dead code.")]
public partial class UpdateDialog : Window
{
    private readonly Func<CancellationToken, Task> _restartToUpdate;
    private readonly CancellationTokenSource _restartCts = new();

    public UpdateDialog(string version, string whatsNewUrl, Func<CancellationToken, Task> restartToUpdate)
    {
        _restartToUpdate = restartToUpdate;
        InitializeComponent();
        MessageText.Text =
            $"Version {version} of QuickMail is available and will be installed automatically " +
            "the next time QuickMail starts.";
        WhatsNewLink.NavigateUri = new Uri(whatsNewUrl);
        Loaded += (_, _) => RestartButton.Focus();
        Closed += (_, _) => { _restartCts.Cancel(); _restartCts.Dispose(); };
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        // On success this call never returns — the app exits and relaunches updated.
        // On failure the VM announces the outcome; close so the user is not left staring
        // at a dialog whose primary action just declined to act. If the user closes the
        // dialog while this is waiting on the download, Closed cancels the token and the
        // restart is retracted silently.
        RestartButton.IsEnabled = false;
        await _restartToUpdate(_restartCts.Token);
        if (IsVisible) Close();   // the user may have closed the dialog during the wait
    }

    private void WhatsNewLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // All ShellExecute launches go through the allow-list. See ExternalUriPolicy.
        Helpers.ExternalUriPolicy.TryOpenExternal(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
