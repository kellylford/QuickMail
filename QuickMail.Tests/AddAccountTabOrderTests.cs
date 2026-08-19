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

    /// <summary>
    /// #584: the OAuth "Sign in with Microsoft" button is the LAST stop in the form — after the
    /// Advanced expander and everything it reveals — so signing in is the final thing a keyboard user
    /// Tabs to, not a control stranded above Advanced that forces a Shift+Tab back up. The sync
    /// opt-ins stay before sign-in so their permission is part of the same consent (#256, #282).
    /// </summary>
    [StaFact]
    public void OAuthSignInIsTheLastStop_AfterAdvancedSettings()
    {
        var vm = NewVm();
        // A Microsoft provider makes the account OAuth (ApplyProvider sets AuthType.OAuth2Microsoft),
        // so the "Sign in with Microsoft" button and the sync opt-ins appear.
        vm.SelectedProvider = new ProviderCatalog().All.First(p => p.Id == ProviderCatalog.MicrosoftId);
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

            // The button has no x:Name, so the walker reports it by its AutomationProperties.Name.
            const string SignIn = "Sign in with Microsoft";

            // Forward (Tab): sign-in comes after the expander header and after the last Advanced
            // control (Signature); the sync opt-ins still precede it.
            TabOrderWalker.StartAt(window, provider, "ProviderComboBox");
            var stops = WalkTabOrder(window);
            var order = string.Join(" -> ", stops);
            int At(string name) => stops.IndexOf(name);

            Assert.True(At(SignIn) >= 0, $"sign-in button not reachable. Order: {order}");
            Assert.True(At("HeaderSite") >= 0, $"expander header never reached. Order: {order}");
            Assert.True(At("SignatureBox") >= 0, $"Signature never reached. Order: {order}");
            Assert.True(At("HeaderSite") < At(SignIn), $"sign-in precedes the Advanced expander. Order: {order}");
            Assert.True(At("SignatureBox") < At(SignIn), $"sign-in precedes the Advanced contents. Order: {order}");
            // Guard the >= 0 explicitly: IndexOf returns -1 for an unreachable control, and -1 < a
            // real index passes vacuously — that would hide the very regression this pins.
            Assert.True(At("SyncContactsCheckBox") >= 0 && At("SyncContactsCheckBox") < At(SignIn),
                $"sync contacts opt-in is unreachable or falls after sign-in. Order: {order}");
            Assert.True(At("SyncCalendarCheckBox") >= 0 && At("SyncCalendarCheckBox") < At(SignIn),
                $"sync calendar opt-in is unreachable or falls after sign-in. Order: {order}");

            // Backward (Shift+Tab): from the Add button, past the bottom row, going back reaches
            // sign-in first, then the Advanced contents — the exact reverse of Tab.
            var add = (Control)window.FindName("AddButton");
            TabOrderWalker.StartAt(window, add, "AddButton");
            var back = TabOrderWalker.WalkBackward(window);
            var backOrder = string.Join(" -> ", back);
            int BackAt(string name) => back.IndexOf(name);

            Assert.True(BackAt(SignIn) >= 0, $"Shift+Tab never reached sign-in. Backward: {backOrder}");
            Assert.True(BackAt(SignIn) < BackAt("SignatureBox"),
                $"Shift+Tab reached Advanced before sign-in. Backward: {backOrder}");
            Assert.True(BackAt("SignatureBox") < BackAt("HeaderSite"),
                $"Shift+Tab order is not the reverse of Tab. Backward: {backOrder}");
        }
        finally { window.Close(); }
    }
}
