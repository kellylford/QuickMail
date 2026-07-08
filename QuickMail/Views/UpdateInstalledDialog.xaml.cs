using System;
using System.Windows;
using System.Windows.Navigation;

namespace QuickMail.Views;

/// <summary>
/// Shown once on the first launch after an update was applied (installed copies, gated by
/// the ShowUpdateInstalledAlerts setting). Escape / Exit dismiss it.
/// </summary>
public partial class UpdateInstalledDialog : Window
{
    public UpdateInstalledDialog(string version, string whatsNewUrl)
    {
        InitializeComponent();
        MessageText.Text = $"QuickMail was updated to version {version}.";
        WhatsNewLink.NavigateUri = new Uri(whatsNewUrl);
        Loaded += (_, _) => ExitButton.Focus();
    }

    private void WhatsNewLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // All ShellExecute launches go through the allow-list. See ExternalUriPolicy.
        Helpers.ExternalUriPolicy.TryOpenExternal(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
