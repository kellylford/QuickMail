using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Organization admin-consent window (#607). Hosts a WebView2 that drives the
/// <c>/organizations/adminconsent</c> flow so a Global Admin grants QuickMail's whole declared
/// permission set, org-wide, in one screen — before any user has signed in.
///
/// A LEAF window: no F6 ring and no command palette (there is nothing to cycle between but the one
/// browser). Shown modeless (<c>Show()</c>) because it hosts a live WebView2 with editable content and
/// opens over the main window's live reading-pane WebView2 — the GrabAddresses deadlock lesson. The
/// outcome is self-contained: it is announced (Result) and shown in the status line; nothing in the app
/// waits on a return value, so there is no result plumbing back to a caller.
///
/// Unlike the CSP-locked reading-pane WebView2s, this one deliberately PERMITS navigation — it is a real
/// interactive Microsoft sign-in. It intercepts only the <c>http://localhost</c> redirect (via
/// <see cref="OAuthService.ParseAdminConsentRedirect"/>) to read the outcome, and never injects a keydown
/// script, so the Microsoft login form keeps its own Tab/Escape/typing.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "_cts is cancelled and disposed in OnClosed; WPF never calls Dispose on a Window, so implementing IDisposable would be dead code.")]
public partial class AdminConsentWindow : Window
{
    // Echoed on the redirect and verified by ParseAdminConsentRedirect (CSRF guard). A per-window value.
    private readonly string _state = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private bool _completed;

    public AdminConsentWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The static instruction is invisible to a screen reader on open — speak it once as a Hint.
        AccessibilityHelper.Announce(this, StatusText.Text, category: AnnouncementCategory.Hint);

        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickMail", "WebView2AdminConsent");
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            if (_cts.IsCancellationRequested) return;
            await ConsentBrowser.EnsureCoreWebView2Async(env);
            if (_cts.IsCancellationRequested) return;

            ConsentBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ConsentBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            ConsentBrowser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            ConsentBrowser.CoreWebView2.Navigate(OAuthService.BuildAdminConsentUrl(_state));
        }
        catch (Exception ex)
        {
            LogService.Log("AdminConsentWindow: failed to start the consent flow", ex);
            Complete(new AdminConsentResult(AdminConsentStatus.Error, ex.Message));
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        AdminConsentResult? result;
        try { result = OAuthService.ParseAdminConsentRedirect(new Uri(args.Uri), _state); }
        catch { return; } // an unparseable interstitial URL — let the flow continue

        if (result is null) return; // still on the Azure AD pages — permit navigation

        // We reached the http://localhost redirect. Nothing serves it, so cancel the navigation and
        // read the outcome from its query instead.
        args.Cancel = true;
        // NavigationStarting fires on a WebView2 callback; marshal UI work to the dispatcher.
        Dispatcher.BeginInvoke(() => Complete(result.Value));
    }

    private void Complete(AdminConsentResult result)
    {
        if (_completed) return;
        _completed = true;

        var text = result.Status switch
        {
            AdminConsentStatus.Granted =>
                "Admin consent granted for your organization. Everyone can now sign in without consent prompts.",
            AdminConsentStatus.Declined =>
                "Admin consent was not granted. Nothing changed for your organization.",
            _ => $"Admin consent could not be completed: {result.Error}",
        };

        // Show the outcome and move focus to the (now Close) button, so the result is both visible and,
        // with the announce, spoken — then the user closes when ready (auto-closing risks cutting off the
        // announcement). The browser is collapsed so the stale AAD page isn't left showing behind it.
        StatusText.Text = text;
        ConsentBrowser.Visibility = Visibility.Collapsed;
        CloseBtn.Content = "_Close";
        AccessibilityHelper.Announce(this, text, category: AnnouncementCategory.Result);
        CloseBtn.Focus();
        Keyboard.Focus(CloseBtn);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // Modeless: no DialogResult, so Escape is wired explicitly. Only fires when a WPF element (e.g.
        // the Close button) has focus — Escape inside the WebView2 is the login page's own.
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _cts.Cancel(); _cts.Dispose(); } catch { /* best effort */ }
        if (ConsentBrowser.CoreWebView2 != null)
            ConsentBrowser.CoreWebView2.NavigationStarting -= OnNavigationStarting;
        ConsentBrowser.Dispose();
        base.OnClosed(e);
    }
}
