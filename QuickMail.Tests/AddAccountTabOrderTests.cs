using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tab order in the Add Account dialog, measured by walking WPF's real focus traversal rather than
/// by reading TabIndex values — which is how the Advanced settings regression got in: the expander
/// header's own TabIndex defaults to int.MaxValue, so every control inside the expander came BEFORE
/// the header that reveals them, and a keyboard user had to Shift+Tab backwards to reach settings
/// they had just opened.
/// </summary>
[Collection("WpfTests")]
public class AddAccountTabOrderTests
{
    private static AddAccountViewModel NewVm() =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = true },
            new StubImapMailService(), new StubOAuthService(), new ProviderCatalog());

    /// <summary>
    /// Walks Tab from the currently focused control and records each stop. Uses MoveFocus, not
    /// PredictFocus — PredictFocus only supports directional navigation and throws on Next.
    /// </summary>
    private static List<string> WalkTabOrder(Window window, int maxStops = 40) =>
        TabOrderWalker.Walk(window, maxStops);


    private static void Drain() => TabOrderWalker.Drain();

    [StaFact]
    public void AdvancedSettingsControlsComeAfterTheExpanderThatRevealsThem()
    {
        var vm = NewVm();
        vm.SelectedProvider = new ProviderCatalog().Other;   // "Other" => Advanced is the point
        var window = new AddAccountDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try
        {
            vm.IsAdvancedExpanded = true;
            window.UpdateLayout();
            Drain();

            var provider = (Control)window.FindName("ProviderComboBox");
            TabOrderWalker.StartAt(window, provider, "ProviderComboBox");

            var stops = WalkTabOrder(window);

            var order = string.Join(" -> ", stops);

            // "HeaderSite" is the name WPF's Expander template gives its header ToggleButton — the
            // thing that actually takes focus when you Tab to "Advanced settings".
            var expanderAt = stops.IndexOf("HeaderSite");
            var imapHostAt = stops.IndexOf("ImapHostBox");
            var authAt = stops.IndexOf("AuthTypeCombo");
            var signatureAt = stops.IndexOf("SignatureBox");

            Assert.True(expanderAt >= 0, $"expander header never reached. Order: {order}");
            Assert.True(imapHostAt >= 0, $"IMAP host never reached. Order: {order}");

            // The whole point: open Advanced, keep pressing Tab, arrive at the settings. Needing
            // Shift+Tab to reach settings you just revealed is the bug this guards.
            Assert.True(expanderAt < authAt, $"Authentication precedes the expander header. Order: {order}");
            Assert.True(expanderAt < imapHostAt, $"IMAP host precedes the expander header. Order: {order}");
            Assert.True(expanderAt < signatureAt, $"Signature precedes the expander header. Order: {order}");

            // And the expander itself comes after the main-surface fields, not before them.
            Assert.True(stops.IndexOf("DisplayNameBox") < expanderAt, $"Order: {order}");

            // Within Advanced, the fields stay in visual order.
            Assert.True(authAt < imapHostAt, $"Order: {order}");
            Assert.True(imapHostAt < stops.IndexOf("SmtpHostBox"), $"Order: {order}");
            Assert.True(stops.IndexOf("SmtpHostBox") < signatureAt, $"Order: {order}");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheFieldsBeforeAdvancedAreInVisualOrder()
    {
        var vm = NewVm();
        var window = new AddAccountDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Drain();
            var provider = (Control)window.FindName("ProviderComboBox");
            TabOrderWalker.StartAt(window, provider, "ProviderComboBox");

            var stops = WalkTabOrder(window);

            int At(string name) => stops.IndexOf(name);
            Assert.True(At("UsernameBox") >= 0, string.Join(" -> ", stops));
            Assert.True(At("UsernameBox") < At("AccountNameBox"), string.Join(" -> ", stops));
            Assert.True(At("AccountNameBox") < At("DisplayNameBox"), string.Join(" -> ", stops));
        }
        finally { window.Close(); }
    }
}
