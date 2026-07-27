using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The behavior that makes Add Account short: picking a provider — or typing an address whose domain
/// the catalog knows — fills every server field, so the Advanced expander stays shut. These tests
/// also lock the guard that stops a provider match from overwriting hosts the user typed themselves.
/// </summary>
public class AddAccountViewModelProviderTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AddAccountViewModel NewVm(IAutoDiscoverService? discover = null, bool graph = true) =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = graph },
            new StubImapMailService(), new StubOAuthService(), Catalog, discover);

    // ── Picking a provider ───────────────────────────────────────────────────────

    [Fact]
    public void ANewDialogStartsOnOtherWithAdvancedCollapsed()
    {
        var vm = NewVm();

        Assert.Equal(ProviderCatalog.OtherId, vm.SelectedProvider!.Id);
        Assert.False(vm.IsAdvancedExpanded);
        Assert.False(vm.HostsUserEdited);
    }

    [Fact]
    public void SelectingGmailFillsServersUsesAppPasswordAndKeepsAdvancedClosed()
    {
        var vm = NewVm();

        vm.SelectedProvider = Catalog.ById(ProviderCatalog.GmailId);

        Assert.Equal("imap.gmail.com", vm.ImapHost);
        Assert.Equal(993, vm.ImapPort);
        Assert.True(vm.ImapUseSsl);
        Assert.Equal("smtp.gmail.com", vm.SmtpHost);
        Assert.Equal(587, vm.SmtpPort);
        Assert.False(vm.SmtpUseSsl);

        // #369: an app password, not Google OAuth, is what works today.
        Assert.Equal(AuthType.Password, vm.AuthType);
        Assert.True(vm.IsPasswordAuth);
        Assert.True(vm.ShowAppPasswordHint);
        Assert.Contains("app password", vm.AppPasswordHint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://myaccount.google.com/apppasswords", vm.AppPasswordUrl);

        // The whole point: the user never has to open Advanced.
        Assert.False(vm.IsAdvancedExpanded);
    }

    [Fact]
    public void GoogleOAuthRemainsReachableForGmail()
    {
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.GmailId);

        // The Authentication combo lives in Advanced settings; switching it must still work.
        vm.AuthType = AuthType.OAuth2Google;

        Assert.True(vm.IsGoogleOAuth);
        // No password box is shown, so the app-password warning must go with it.
        Assert.False(vm.ShowAppPasswordHint);
    }

    [Fact]
    public void SelectingOtherOpensAdvancedAndLeavesTypedFieldsAlone()
    {
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.YahooId);
        Assert.False(vm.IsAdvancedExpanded);

        vm.SelectedProvider = Catalog.Other;

        Assert.True(vm.IsAdvancedExpanded);
        Assert.False(vm.ShowAppPasswordHint);
        // "Other" has no settings to apply, so it must not blank out what is already there.
        Assert.Equal("imap.mail.yahoo.com", vm.ImapHost);
    }

    [Fact]
    public void SelectingAProviderNamesTheAccountButNeverOverwritesATypedName()
    {
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.ICloudId);
        Assert.Equal("iCloud Mail", vm.AccountName);

        vm.AccountName = "Personal";
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.YahooId);
        Assert.Equal("Personal", vm.AccountName);
    }

    // ── Typing an address ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("kelly@gmail.com", ProviderCatalog.GmailId, "imap.gmail.com")]
    [InlineData("kelly@yahoo.com", ProviderCatalog.YahooId, "imap.mail.yahoo.com")]
    [InlineData("kelly@me.com", ProviderCatalog.ICloudId, "imap.mail.me.com")]
    [InlineData("kelly@outlook.com", ProviderCatalog.MicrosoftId, "outlook.office365.com")]
    public void TypingAKnownAddressSelectsTheProviderAndFillsTheHost(
        string email, string expectedProviderId, string expectedImapHost)
    {
        var vm = NewVm();

        vm.Username = email;

        Assert.Equal(expectedProviderId, vm.SelectedProvider!.Id);
        Assert.Equal(expectedImapHost, vm.ImapHost);
        Assert.False(vm.IsAdvancedExpanded);
    }

    [Fact]
    public void TypingAnUnknownAddressChangesNothingOnItsOwn()
    {
        var vm = NewVm();

        vm.Username = "kelly@theideaplace.net";

        // The offline match found nothing; the network lookup is what runs next, on focus loss.
        Assert.Equal(ProviderCatalog.OtherId, vm.SelectedProvider!.Id);
        Assert.Equal(string.Empty, vm.ImapHost);
    }

    [Fact]
    public void AHandEditedHostSurvivesALaterProviderMatch()
    {
        var vm = NewVm();
        vm.ImapHost = "mail.myserver.example";      // user typed this in Advanced settings
        Assert.True(vm.HostsUserEdited);

        vm.Username = "kelly@gmail.com";

        Assert.Equal("mail.myserver.example", vm.ImapHost);
        Assert.Equal(ProviderCatalog.OtherId, vm.SelectedProvider!.Id);
    }

    // ── Connection method ────────────────────────────────────────────────────────

    [Fact]
    public void SwitchingMicrosoftToGraphAndBackRestoresTheImapHosts()
    {
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        Assert.Equal("outlook.office365.com", vm.ImapHost);

        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.MicrosoftGraph);
        Assert.Equal(string.Empty, vm.ImapHost);
        Assert.True(vm.IsGraphBackend);

        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.ImapSmtp);
        Assert.Equal("outlook.office365.com", vm.ImapHost);
        Assert.Equal("smtp-mail.outlook.com", vm.SmtpHost);
    }

    // ── Discovery ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DiscoveryAppliesNetworkFoundSettingsAndCollapsesAdvanced()
    {
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            "mail.example.edu", 993, true, "smtp.example.edu", 587, false,
            ProviderId: null, DisplayName: "Example University", DiscoverySource.Ispdb));
        var vm = NewVm(discover);
        vm.Username = "kelly@example.edu";

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Equal("mail.example.edu", vm.ImapHost);
        Assert.Equal("smtp.example.edu", vm.SmtpHost);
        Assert.False(vm.IsAdvancedExpanded);
        Assert.Contains("Example University", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryOfAnOffice365DomainSelectsTheMicrosoftProvider()
    {
        var ms = Catalog.ById(ProviderCatalog.MicrosoftId)!;
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            ms.ImapHost, ms.ImapPort, ms.ImapUseSsl, ms.SmtpHost, ms.SmtpPort, ms.SmtpUseSsl,
            ms.Id, ms.DisplayName, DiscoverySource.ExchangeAutodiscover));
        var vm = NewVm(discover);
        vm.Username = "kelly@contoso.com";

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        // Carrying the provider through is what turns a bare host list into "sign in with Microsoft".
        Assert.Equal(ProviderCatalog.MicrosoftId, vm.SelectedProvider!.Id);
        Assert.Equal(AuthType.OAuth2Microsoft, vm.AuthType);
        Assert.True(vm.IsOAuth2);
    }

    [Fact]
    public async Task DiscoveryFindingNothingOpensAdvancedAndSaysSo()
    {
        var vm = NewVm(new StubAutoDiscover(null));
        vm.Username = "kelly@nowhere.example";
        var events = new List<(bool Found, string Message)>();
        vm.DiscoveryCompleted += (found, msg) => events.Add((found, msg));

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        // Never a silent empty state — the user must be told, and left where they now have to type.
        Assert.True(vm.IsAdvancedExpanded);
        Assert.Contains("No settings found", vm.StatusText, StringComparison.Ordinal);
        Assert.Single(events);
        Assert.False(events[0].Found);
    }

    [Fact]
    public async Task DiscoveryDoesNotRunForAProviderTheCatalogAlreadyMatched()
    {
        var discover = new StubAutoDiscover(null);
        var vm = NewVm(discover);

        vm.Username = "kelly@gmail.com";
        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, discover.Calls);
    }

    [Fact]
    public async Task DiscoveryDoesNotRunOnceTheUserHasTypedAHost()
    {
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            "discovered.example", 993, true, "smtp.discovered.example", 587, false,
            null, "Discovered", DiscoverySource.Ispdb));
        var vm = NewVm(discover);
        vm.Username = "kelly@theideaplace.net";
        vm.ImapHost = "mail.theideaplace.net";

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, discover.Calls);
        Assert.Equal("mail.theideaplace.net", vm.ImapHost);
    }

    [Fact]
    public async Task DiscoveryIsSkippedForABlankOrMalformedAddress()
    {
        var discover = new StubAutoDiscover(null);
        var vm = NewVm(discover);

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);   // Username still empty
        vm.Username = "not-an-email";
        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, discover.Calls);
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToAccountModelCarriesTheProviderId()
    {
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.YahooId);
        vm.Username = "kelly@yahoo.com";

        var account = vm.ToAccountModel();

        Assert.Equal(ProviderCatalog.YahooId, account.ProviderId);
        Assert.Equal("imap.mail.yahoo.com", account.ImapHost);
        // And it round-trips: resolving the saved account gets the same provider back.
        Assert.Equal(ProviderCatalog.YahooId, Catalog.Resolve(account).Id);
    }

    [Fact]
    public void DisposeIsSafeToCallTwice()
    {
        var vm = NewVm(new StubAutoDiscover(null));
        vm.Dispose();
        vm.Dispose();   // AddAccountDialog.OnClosed can fire after an explicit dispose
    }

    private sealed class StubAutoDiscover(DiscoveredSettings? result) : IAutoDiscoverService
    {
        public int Calls { get; private set; }

        public Task<DiscoveredSettings?> DiscoverAsync(string emailAddress, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
