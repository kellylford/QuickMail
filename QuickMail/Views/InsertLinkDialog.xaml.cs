using System;
using System.Windows;
using System.Windows.Controls;

namespace QuickMail.Views;

/// <summary>
/// Collects a URL and optional display text for the compose Insert Link command.
/// OK is enabled once the URL parses as an absolute http/https/mailto URI.
/// </summary>
public partial class InsertLinkDialog : Window
{
    /// <summary>The validated URL. Set when the dialog returns true.</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>The display text; falls back to the URL when left empty.</summary>
    public string DisplayText { get; private set; } = string.Empty;

    public InsertLinkDialog(string initialDisplayText = "")
    {
        InitializeComponent();
        DisplayTextBox.Text = initialDisplayText;
        Loaded += (_, _) => UrlBox.Focus();
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = TryNormalizeUrl(UrlBox.Text, out _);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeUrl(UrlBox.Text, out var url)) return;
        Url = url;
        DisplayText = string.IsNullOrWhiteSpace(DisplayTextBox.Text) ? url : DisplayTextBox.Text.Trim();
        DialogResult = true;
    }

    /// <summary>Accepts http/https/mailto URLs; bare domains get https:// prepended.</summary>
    private static bool TryNormalizeUrl(string input, out string url)
    {
        url = string.Empty;
        var text = input.Trim();
        if (text.Length == 0) return false;

        if (!text.Contains("://") && !text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains('@') && !text.Contains('/'))
                text = "mailto:" + text;
            else
                text = "https://" + text;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto"
            && (uri.Scheme == "mailto" || uri.Host.Contains('.')))
        {
            url = uri.ToString();
            return true;
        }
        return false;
    }
}
