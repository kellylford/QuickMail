using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #834, the first fixes from the add-account review:
///
/// 1. The bottom buttons of Add Account and Manage Accounts were <c>DockPanel.Dock="Right"</c>,
///    declared Cancel/Close first. Docking reversed them on screen but not in the Tab order, so Tab
///    reached Cancel before Add Account, and Shift+Tab from the first field landed on Add Account.
///    They must tab in the order they are drawn, with Cancel/Close the last stop.
/// 2. Manage Accounts had New on Alt+W, shared with Password and the Sign in buttons; WPF answers a
///    shared access key by moving focus between the owners instead of pressing any of them, so
///    Alt+W with an account selected only moved focus to New. No two visible controls may share
///    an access key, in any state either dialog can be in.
/// 3. A settings lookup that found nothing moved focus to IMAP host seconds after the user had
///    tabbed into Password, so the rest of their password went into the server field.
/// </summary>
[Collection("WpfTests")]
public class AccountDialogButtonsAndAccessKeyTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static void Drain() => TabOrderWalker.Drain();

    /// <summary>Every feature flag on, so the access-key scans see every control the app can show.</summary>
    private static StubFeatureGate AllFlags()
    {
        var gate = new StubFeatureGate();
        foreach (var flag in Enum.GetValues<FeatureFlag>()) gate[flag] = true;
        return gate;
    }

    private static AddAccountViewModel NewAddVm(IAutoDiscoverService? discover = null) =>
        new(AllFlags(), new StubImapMailService(), new StubOAuthService(), Catalog, discover);

    private static AccountManagerViewModel NewManagerVm() =>
        new(new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            AllFlags(), Catalog);

    private static AccountModel SelectAccount(AccountManagerViewModel vm, AuthType authType,
        BackendKind backend = BackendKind.ImapSmtp)
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            AccountName = "Test account",
            Username = "kelly@example.com",
            AuthType = authType,
            BackendKind = backend,
            ImapHost = "imap.example.com",
            Pop3Host = "pop.example.com",
            SmtpHost = "smtp.example.com",
        };
        vm.Accounts.Add(account);
        vm.SelectedAccount = account;
        return account;
    }

    private static T Show<T>(T window) where T : Window
    {
        window.WindowStyle = WindowStyle.None;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        window.UpdateLayout();
        Drain();
        return window;
    }

    private static FrameworkElement Named(Window window, string name)
    {
        var element = window.FindName(name) as FrameworkElement;
        Assert.NotNull(element);
        return element!;
    }

    private static double LeftOf(Window window, string name) =>
        Named(window, name).TranslatePoint(new Point(0, 0), window).X;

    // ── 1. Bottom buttons tab in the order they are drawn ─────────────────────────────────────────

    [StaFact]
    public void AddAccount_BottomButtonsTabLeftToRight_AndCancelIsTheLastStop()
    {
        var vm = NewAddVm();
        var window = Show(new AddAccountDialog(vm));
        try
        {
            // Drawn left to right: Add Account, Test Connection, Cancel.
            Assert.True(LeftOf(window, "AddButton") < LeftOf(window, "TestConnectionButton"));
            Assert.True(LeftOf(window, "TestConnectionButton") < LeftOf(window, "CancelButton"));

            // Forward from Cancel wraps, so the walk records every stop and ends back on Cancel.
            TabOrderWalker.StartAt(window, Named(window, "CancelButton"), "CancelButton");
            var stops = TabOrderWalker.Walk(window);
            var order = string.Join(" -> ", stops);

            Assert.Equal("ProviderComboBox", stops[0]);   // Cancel is last, so Tab wraps to the top
            Assert.True(stops.Count >= 4, $"Order: {order}");
            Assert.Equal(new[] { "AddButton", "TestConnectionButton", "CancelButton" }, stops.TakeLast(3));

            // Shift+Tab from the first field goes to Cancel, not to the button that commits.
            TabOrderWalker.StartAt(window, Named(window, "ProviderComboBox"), "ProviderComboBox");
            var back = TabOrderWalker.WalkBackward(window);
            Assert.Equal("CancelButton", back[0]);
            Assert.Equal("TestConnectionButton", back[1]);
            Assert.Equal("AddButton", back[2]);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ManageAccounts_BottomButtonsTabLeftToRight_AndCloseIsTheLastStop()
    {
        var vm = NewManagerVm();
        SelectAccount(vm, AuthType.Password);
        var window = Show(new AccountManagerDialog(vm));
        try
        {
            Assert.True(LeftOf(window, "SaveButton") < LeftOf(window, "TestConnectionButton"));
            Assert.True(LeftOf(window, "TestConnectionButton") < LeftOf(window, "CloseButton"));

            TabOrderWalker.StartAt(window, Named(window, "CloseButton"), "CloseButton");
            var stops = TabOrderWalker.Walk(window);
            var order = string.Join(" -> ", stops);

            Assert.Equal("AccountListBox", stops[0]);
            Assert.Equal(new[] { "SaveButton", "TestConnectionButton", "CloseButton" }, stops.TakeLast(3));

            // Going backward, Close comes first after the list — it is the stop "before" the top.
            TabOrderWalker.StartAt(window, Named(window, "AccountNameBox"), "AccountNameBox");
            var back = TabOrderWalker.WalkBackward(window);
            var closeBack = back.IndexOf("CloseButton");
            Assert.True(closeBack >= 0, $"Backward: {string.Join(" -> ", back)}");
            Assert.Equal("TestConnectionButton", back[closeBack + 1]);
            Assert.Equal("SaveButton", back[closeBack + 2]);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ManageAccounts_WithNoAccounts_CloseIsStillTheLastStop()
    {
        // First run: Test Connection is hidden (nothing is being edited), the form is disabled, and
        // Close must still be the last stop, after Save.
        var vm = NewManagerVm();
        var window = Show(new AccountManagerDialog(vm));
        try
        {
            TabOrderWalker.StartAt(window, Named(window, "CloseButton"), "CloseButton");
            var stops = TabOrderWalker.Walk(window);
            Assert.Equal(new[] { "SaveButton", "CloseButton" }, stops.TakeLast(2));
        }
        finally { window.Close(); }
    }

    // ── 2. No shared access keys ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every access key a visible, enabled control offers, keyed by letter. That is WPF's own rule:
    /// AccessKeyManager skips a target that is hidden or disabled. So a control inside a collapsed
    /// expander, or one hidden by its mode, cannot clash — which is how Password and Sign in with
    /// Microsoft legitimately share Alt+W.
    /// </summary>
    private static Dictionary<char, List<string>> VisibleAccessKeys(Window window)
    {
        var keys = new Dictionary<char, List<string>>();
        void Visit(DependencyObject node)
        {
            if (node is AccessText { IsVisible: true, IsEnabled: true } text && text.AccessKey != '\0')
            {
                var key = char.ToUpperInvariant(text.AccessKey);
                if (!keys.TryGetValue(key, out var owners)) keys[key] = owners = new List<string>();
                owners.Add(text.Text);
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(window);
        return keys;
    }

    private static void AssertNoSharedAccessKeys(Window window, string state)
    {
        window.UpdateLayout();
        Drain();
        var keys = VisibleAccessKeys(window);
        // First run has the fewest: New, Import Settings, Delete, Add shared mailbox and Save, with the form disabled.
        Assert.True(keys.Count >= 4, $"{state}: found only {keys.Count} access keys; the scan is not seeing the dialog.");
        var clashes = keys.Where(k => k.Value.Count > 1)
                          .Select(k => $"Alt+{k.Key}: {string.Join(" | ", k.Value)}")
                          .ToList();
        Assert.True(clashes.Count == 0, $"{state}: shared access keys — {string.Join("; ", clashes)}");
    }

    [StaTheory]
    [InlineData(ProviderCatalog.OtherId, true)]
    [InlineData(ProviderCatalog.GmailId, false)]
    [InlineData(ProviderCatalog.GmailId, true)]
    [InlineData(ProviderCatalog.MicrosoftId, false)]
    [InlineData(ProviderCatalog.MicrosoftId, true)]
    [InlineData(ProviderCatalog.ICloudId, true)]
    [InlineData(ProviderCatalog.GmailOAuthId, false)]
    [InlineData(ProviderCatalog.GmailOAuthId, true)]
    public void AddAccount_NoTwoVisibleControlsShareAnAccessKey(string providerId, bool advancedOpen)
    {
        var vm = NewAddVm();
        vm.SelectedProvider = Catalog.All.First(p => p.Id == providerId);
        vm.OfferImportSettings = true;   // first run on a new install: the most controls at once (Alt+I)
        var window = Show(new AddAccountDialog(vm));
        try
        {
            vm.IsAdvancedExpanded = advancedOpen;
            AssertNoSharedAccessKeys(window, $"Add Account, {providerId}, Advanced {(advancedOpen ? "open" : "closed")}");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void AddAccount_NoTwoVisibleControlsShareAnAccessKey_Pop3()
    {
        var vm = NewAddVm();
        vm.SelectedProvider = Catalog.Other;
        var window = Show(new AddAccountDialog(vm));
        try
        {
            vm.IsAdvancedExpanded = true;
            var pop3 = vm.AvailableBackends.FirstOrDefault(b => b.Kind == BackendKind.Pop3Smtp);
            Assert.NotNull(pop3);
            vm.SelectedBackend = pop3;
            window.UpdateLayout();
            Assert.True(Named(window, "Pop3HostBox").IsVisible);
            AssertNoSharedAccessKeys(window, "Add Account, POP3, Advanced open");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ManageAccounts_NoTwoVisibleControlsShareAnAccessKey_FirstRun()
    {
        var vm = NewManagerVm();
        vm.OfferImportSettings = true;   // a new install offers Import Settings beside New
        var window = Show(new AccountManagerDialog(vm));
        try
        {
            Assert.True(Named(window, "ImportSettingsButton").IsVisible);
            AssertNoSharedAccessKeys(window, "Manage Accounts, no accounts");
        }
        finally { window.Close(); }
    }

    [StaTheory]
    [InlineData(AuthType.Password, false)]
    [InlineData(AuthType.Password, true)]
    [InlineData(AuthType.OAuth2Microsoft, false)]
    [InlineData(AuthType.OAuth2Microsoft, true)]
    public void ManageAccounts_NoTwoVisibleControlsShareAnAccessKey(AuthType authType, bool advancedOpen)
    {
        // OAuth on the IMAP backend is also the state that offers Convert to Microsoft 365.
        var vm = NewManagerVm();
        SelectAccount(vm, authType);
        var window = Show(new AccountManagerDialog(vm));
        try
        {
            vm.IsAdvancedExpanded = advancedOpen;
            AssertNoSharedAccessKeys(window, $"Manage Accounts, {authType}, Advanced {(advancedOpen ? "open" : "closed")}");
        }
        finally { window.Close(); }
    }

    [StaTheory]
    [InlineData(BackendKind.MicrosoftGraph, false)]
    [InlineData(BackendKind.MicrosoftGraph, true)]
    [InlineData(BackendKind.Pop3Smtp, true)]
    public void ManageAccounts_NoTwoVisibleControlsShareAnAccessKey_OtherBackends(BackendKind backend, bool advancedOpen)
    {
        var vm = NewManagerVm();
        SelectAccount(vm, backend == BackendKind.MicrosoftGraph ? AuthType.OAuth2Microsoft : AuthType.Password, backend);
        var window = Show(new AccountManagerDialog(vm));
        try
        {
            vm.IsAdvancedExpanded = advancedOpen;
            AssertNoSharedAccessKeys(window, $"Manage Accounts, {backend}, Advanced {(advancedOpen ? "open" : "closed")}");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ManageAccounts_NoTwoVisibleControlsShareAnAccessKey_SharedMailboxSelected()
    {
        var vm = NewManagerVm();
        var parent = SelectAccount(vm, AuthType.OAuth2Microsoft, BackendKind.MicrosoftGraph);
        var shared = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "support@work.com", Username = "support@work.com",
            SharedAddress = "support@work.com", IsShared = true, ParentAccountId = parent.Id,
            BackendKind = parent.BackendKind, AuthType = parent.AuthType,
        };
        vm.Accounts.Add(shared);
        vm.SelectedAccount = shared;
        var window = Show(new AccountManagerDialog(vm));
        try
        {
            Assert.True(Named(window, "SharedNotifyCheckBox").IsVisible);
            AssertNoSharedAccessKeys(window, "Manage Accounts, shared mailbox selected");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ManageAccounts_NewIsAltN()
    {
        // The key the user reaches for to add an account; it was Alt+W and did nothing useful.
        var vm = NewManagerVm();
        SelectAccount(vm, AuthType.Password);
        var window = Show(new AccountManagerDialog(vm));
        try
        {
            var keys = VisibleAccessKeys(window);
            Assert.True(keys.TryGetValue('N', out var owners), "nothing is on Alt+N");
            Assert.Equal(new[] { "_New" }, owners);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task ManageAccounts_DeleteAsksFirst_EvenWithNoSharedMailboxes()
    {
        var vm = NewManagerVm();
        var account = SelectAccount(vm, AuthType.Password);
        string? asked = null;
        vm.ConfirmAccountRemoval = message => { asked = message; return false; };

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Contains("Test account", asked, StringComparison.Ordinal);
        Assert.Contains(vm.Accounts, a => a.Id == account.Id);   // declined: nothing removed

        vm.SelectedAccount = account;
        vm.ConfirmAccountRemoval = _ => true;
        await vm.DeleteAccountCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Accounts, a => a.Id == account.Id);
    }

    [Fact]
    public async Task ManageAccounts_DeleteFailsClosed_WhenNothingCanAsk()
    {
        var vm = NewManagerVm();
        var account = SelectAccount(vm, AuthType.Password);
        // ConfirmAccountRemoval deliberately unset.

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.Contains(vm.Accounts, a => a.Id == account.Id);
    }

    // ── 3. A settings lookup never moves focus ───────────────────────────────────────────────────

    private sealed class FindsNothing : IAutoDiscoverService
    {
        public Task<DiscoveredSettings?> DiscoverAsync(string emailAddress, CancellationToken ct) =>
            Task.FromResult<DiscoveredSettings?>(null);
    }

    /// <summary>
    /// Needs real keyboard focus in an active window, so it is an input test: on an inactive window
    /// the old code's <c>ImapHostBox.Focus()</c> could not take focus from Password either, and the
    /// test would pass against the bug it is here to catch.
    /// </summary>
    [StaFact(Skip = InputTests.SkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public async Task AddAccount_ALookupThatFindsNothing_LeavesKeyboardFocusInPassword()
    {
        var vm = NewAddVm(new FindsNothing());
        vm.SelectedProvider = Catalog.Other;
        var window = new AddAccountDialog(vm) { ShowInTaskbar = false };
        window.Show();
        try
        {
            window.Activate();
            vm.Username = "kelly@nowhere.example";
            var password = Named(window, "PasswordBox");
            Keyboard.Focus(password);
            Drain();
            Assert.Same(password, Keyboard.FocusedElement);   // setup: the window really has focus

            await vm.DiscoverSettingsCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Drain();
            Drain();

            Assert.True(vm.IsAdvancedExpanded);   // the fields to fill in are still opened
            Assert.True(Named(window, "ImapHostBox").IsVisible);
            Assert.Same(password, Keyboard.FocusedElement);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task ALookupThatFindsNothing_IsAnnouncedAsAResult()
    {
        // It is the outcome of leaving the address field and the user has to act on it, so it must
        // be heard even with background-status announcements turned off.
        var vm = NewAddVm(new FindsNothing());
        vm.Username = "kelly@nowhere.example";
        var recorder = StatusAnnouncementRecorder.Watch(vm);

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Contains("No settings found", recorder.Last.Text, StringComparison.Ordinal);
        Assert.Equal(AnnouncementCategory.Result, recorder.Last.Category);
    }
}
