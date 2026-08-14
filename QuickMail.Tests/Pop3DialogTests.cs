using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
/// The POP3 settings as they actually render (#128). QuickMail is built by someone who cannot see
/// the screen, so "the fields are there" is asserted against the real visual tree and the real
/// automation peers — never eyeballed, and never inferred from the XAML text.
/// </summary>
[Collection("WpfTests")]
public class Pop3DialogTests
{
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T Named<T>(Window window, string name) where T : class
    {
        var element = window.FindName(name) as T;
        Assert.True(element is not null, $"{name} is missing from {window.GetType().Name}.");
        return element!;
    }

    private static AddAccountViewModel NewAddVm() =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = true, [FeatureFlag.Pop3Backend] = true },
            new StubImapMailService(), new StubOAuthService(), new ProviderCatalog());

    private static AddAccountDialog ShowAddDialog(AddAccountViewModel vm)
    {
        var window = new AddAccountDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        return window;
    }

    /// <summary>The accessible name a screen reader would read for this element.</summary>
    private static string PeerName(UIElement element) =>
        UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? string.Empty;

    /// <summary>
    /// The accessible name once WPF has settled.
    ///
    /// <para><c>AutomationProperties.LabeledBy</c> is an <c>ElementName</c> binding, and ElementName
    /// resolution is asynchronous — WPF retries it as the tree loads. A single dispatcher drain is
    /// enough on a fast machine and was not on the CI runner, where this read came back empty
    /// (build job of PR 538). Draining until the name appears makes the test measure the wiring
    /// rather than the machine; a name that never appears still fails, with the diagnosis attached.</para>
    /// </summary>
    private static string SettledPeerName(FrameworkElement element)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var name = PeerName(element);
            if (!string.IsNullOrEmpty(name)) return name;
            Drain();
        }

        // Empty after settling is a real failure. Say which half of the wiring is missing: a null
        // LabeledBy means the ElementName never resolved (#168's failure mode), while a resolved
        // LabeledBy with no name means the label itself carries no content.
        var labeledBy = AutomationProperties.GetLabeledBy(element);
        Assert.Fail(
            $"{element.Name} has no accessible name after settling. " +
            $"LabeledBy={(labeledBy is null ? "null (ElementName never resolved)" : labeledBy.GetType().Name)}, " +
            $"IsVisible={element.IsVisible}, IsLoaded={element.IsLoaded}.");
        return string.Empty;   // unreachable; Assert.Fail throws
    }

    [StaFact]
    public void ChoosingPop3_ShowsThePop3FieldsAndHidesTheImapOnes()
    {
        var vm = NewAddVm();
        var window = ShowAddDialog(vm);
        try
        {
            vm.IsAdvancedExpanded = true;
            vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.Pop3Smtp);
            window.UpdateLayout();
            Drain();

            // IsVisible, not Visibility: an element inside a collapsed panel keeps its own
            // Visibility.Visible, so asserting on that would pass whatever the section did.
            Assert.True(Named<TextBox>(window, "Pop3HostBox").IsVisible);
            Assert.True(Named<TextBox>(window, "Pop3PortBox").IsVisible);
            Assert.True(Named<CheckBox>(window, "Pop3LeaveOnServerCheckBox").IsVisible);

            // The two incoming sections are mutually exclusive; showing both would offer the user a
            // host box that the account they are creating will never dial.
            Assert.False(Named<TextBox>(window, "ImapHostBox").IsVisible);

            // SMTP stays: a POP3 account still sends.
            Assert.True(Named<TextBox>(window, "SmtpHostBox").IsVisible);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ThePop3FieldsAreLabelledForAScreenReader()
    {
        var vm = NewAddVm();
        var window = ShowAddDialog(vm);
        try
        {
            vm.IsAdvancedExpanded = true;
            vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.Pop3Smtp);
            window.UpdateLayout();
            Drain();

            // Read from the automation peer, not from the XAML: a Label whose Target failed to bind
            // renders perfectly and announces nothing (#168).
            Assert.Equal("POP3 host:", SettledPeerName(Named<TextBox>(window, "Pop3HostBox")));
            Assert.Equal("POP3 port:", SettledPeerName(Named<TextBox>(window, "Pop3PortBox")));

            // A short label, with no instruction baked in — the consequence of clearing it is a Hint.
            var keep = SettledPeerName(Named<CheckBox>(window, "Pop3LeaveOnServerCheckBox"));
            Assert.Equal("Keep mail on the server after downloading", keep);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheKeepMailCheckboxSpeaksWhatClearingItCosts()
    {
        var vm = NewAddVm();
        var window = ShowAddDialog(vm);
        var heard = new List<(string Text, AnnouncementCategory Category)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));
        try
        {
            vm.IsAdvancedExpanded = true;
            vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.Pop3Smtp);
            window.UpdateLayout();
            Drain();

            heard.Clear();
            var checkbox = Named<CheckBox>(window, "Pop3LeaveOnServerCheckBox");
            checkbox.Focus();
            Keyboard.Focus(checkbox);
            Drain();

            var hint = heard.FirstOrDefault(h => h.Category == AnnouncementCategory.Hint);
            Assert.False(string.IsNullOrWhiteSpace(hint.Text),
                "focusing the keep-mail checkbox produced no hint. Heard: "
                + (heard.Count == 0 ? "(nothing)" : string.Join(" | ", heard.Select(h => $"{h.Category}: {h.Text}"))));
            // It must say what is at stake, not merely restate the label.
            Assert.Contains("only copy", hint.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void TheConnectionMethodComboOffersPop3ByItsDisplayName()
    {
        var vm = NewAddVm();
        var window = ShowAddDialog(vm);
        try
        {
            vm.IsAdvancedExpanded = true;
            window.UpdateLayout();
            Drain();

            var combo = Named<ComboBox>(window, "BackendComboBox");
            Assert.True(combo.IsVisible);

            // A Selector item's accessible name comes from ToString(), not DisplayMemberPath — the
            // bug that shipped in the theme combo and looks perfect on screen.
            var names = combo.Items.Cast<object>().Select(i => i.ToString()).ToList();
            Assert.Contains("POP3/SMTP", names);
            Assert.DoesNotContain(names, n => n?.Contains("BackendKindOption", StringComparison.Ordinal) == true);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void AnExistingPop3AccountShowsItsSettingsInTheAccountManager()
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), new ProviderCatalog());

        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            AccountName = "POP account",
            Username = "kelly@example.com",
            AuthType = AuthType.Password,
            BackendKind = BackendKind.Pop3Smtp,
            Pop3Host = "pop.example.com",
            SmtpHost = "smtp.example.com",
        };
        vm.Accounts.Add(account);
        vm.SelectedAccount = account;

        var window = new AccountManagerDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try
        {
            vm.IsAdvancedExpanded = true;
            window.UpdateLayout();
            Drain();

            // The gate hides the OFFER of POP3 in Add Account; an account already using it must still
            // show its own settings, whatever the flag is set to.
            var host = Named<TextBox>(window, "Pop3HostBox");
            Assert.True(host.IsVisible);
            Assert.Equal("pop.example.com", host.Text);
            Assert.False(Named<TextBox>(window, "ImapHostBox").IsVisible);
        }
        finally { window.Close(); }
    }
}
