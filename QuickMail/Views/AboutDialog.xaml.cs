using System.Windows;
using System.Windows.Navigation;

namespace QuickMail.Views;

/// <summary>
/// Displays the application name, version number, and a link to the license.
/// </summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Helpers.AppVersion.Display}";
        Loaded += (_, _) => LicenseLink.Focus();
    }

    private void LicenseLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // All ShellExecute launches go through the allow-list. See ExternalUriPolicy.
        Helpers.ExternalUriPolicy.TryOpenExternal(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
