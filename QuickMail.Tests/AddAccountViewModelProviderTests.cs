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

    private static AddAccountViewModel NewVm(IOAuthService oauth, IMailService? mail = null) =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = true },
            mail ?? new StubImapMailService(), oauth, Catalog);

    /// <summary>
    /// Types an address the way the dialog does. UsernameBox binds with
    /// UpdateSourceTrigger=PropertyChanged, so the ViewModel sees every PREFIX — "kelly@o" on the
    /// way to "kelly@outlook.com" — not the finished address. Assigning the whole string at once is
    /// what let a mid-typing bug hide from these tests.
    /// </summary>
    private static void TypeAddress(AddAccountViewModel vm, string address)
    {
        for (var i = 1; i <= address.Length; i++) vm.Username = address[..i];
    }

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
    public void ArrowingThroughTheProviderListNeverWritesTheAccountName()
    {
        // Selecting a provider fires for every item arrowed past. Filling the name in on the way
        // through left a Microsoft account called "Gmail", because the second pass saw a non-blank
        // box and declined to correct it. Nothing but the user writes this field now.
        var vm = NewVm();

        vm.SelectedProvider = Catalog.ById(ProviderCatalog.GmailId);
        Assert.Equal(string.Empty, vm.AccountName);

        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        Assert.Equal(string.Empty, vm.AccountName);
    }

    [Fact]
    public void ANameTheUserTypedSurvivesEveryProviderChange()
    {
        var vm = NewVm();
        vm.AccountName = "Personal";

        vm.SelectedProvider = Catalog.ById(ProviderCatalog.ICloudId);
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.YahooId);
        vm.Username = "kelly@gmail.com";

        Assert.Equal("Personal", vm.AccountName);
    }

    [Fact]
    public void AnAccountWithNoNameIsLabelledByItsAddress()
    {
        // Why no placeholder is needed: the model already falls back to the address.
        var vm = NewVm();
        vm.Username = "kelly@gmail.com";

        var account = vm.ToAccountModel();

        Assert.Equal(string.Empty, account.AccountName);
        Assert.Equal("kelly@gmail.com", account.AccountLabel);
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

    // ── Correcting the address (the sticky-provider bug) ─────────────────────────

    [Fact]
    public void CorrectingATypodAddressDropsTheOldProviderAndItsServers()
    {
        // Type a Gmail address, then realise it should have been a work address. Leaving Gmail
        // selected meant the account was saved with imap.gmail.com for an unrelated domain —
        // invisibly, because Advanced stays collapsed — and the settings lookup was skipped
        // entirely, since a provider had already been chosen.
        var vm = NewVm();
        vm.Username = "kelly@gmail.com";
        Assert.Equal(ProviderCatalog.GmailId, vm.SelectedProvider!.Id);

        vm.Username = "kelly@theideaplace.net";

        Assert.Equal(ProviderCatalog.OtherId, vm.SelectedProvider!.Id);
        Assert.Equal(string.Empty, vm.ImapHost);
        Assert.Equal(string.Empty, vm.SmtpHost);
    }

    [Fact]
    public void CorrectingTheAddressKeepsAnAccountNameTheUserTyped()
    {
        var vm = NewVm();
        vm.Username = "kelly@gmail.com";
        vm.AccountName = "Personal";

        vm.Username = "kelly@theideaplace.net";

        Assert.Equal("Personal", vm.AccountName);
    }

    [Fact]
    public void FallingBackToOtherWhileTypingDoesNotPopAdvancedOpen()
    {
        // This fires on every keystroke of an unmatched address. Expanding here would be hostile —
        // and for a screen-reader user, a surprise pile of new fields mid-sentence.
        var vm = NewVm();
        vm.Username = "kelly@gmail.com";

        vm.Username = "kelly@g";

        Assert.False(vm.IsAdvancedExpanded);
    }

    [Fact]
    public async Task AfterCorrectingTheAddressDiscoveryRunsForTheNewDomain()
    {
        var discover = new StubAutoDiscover(null);
        var vm = NewVm(discover);
        vm.Username = "kelly@gmail.com";      // catalog match — no lookup wanted

        vm.Username = "kelly@theideaplace.net";
        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, discover.Calls);
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

    // ── Work-or-school Microsoft accounts must use Graph ─────────────────────────

    [Theory]
    [InlineData("kelly@icanbrew.com")]
    [InlineData("kelly@contoso.co.uk")]
    public void AMicrosoftAccountOnACustomDomainUsesGraph(string email)
    {
        // A work tenant on the IMAP backend asks for outlook.office.com/IMAP.AccessAsUser.All +
        // SMTP.Send, which most tenants have never consented to — sign-in ends at "your
        // administrator needs to make a change". Graph asks for graph.microsoft.com/.default, which
        // is exactly what the admin already approved.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        TypeAddress(vm, email);
        vm.CommitUsername();   // the user leaves the address field: the address is now finished

        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);
        Assert.Equal(AuthType.OAuth2Microsoft, vm.AuthType);
    }

    [Theory]
    [InlineData("kelly@outlook.com")]
    [InlineData("kelly@hotmail.com")]
    [InlineData("kelly@live.com")]
    public void AConsumerMicrosoftAccountKeepsTheImapBackend(string email)
    {
        // The opposite case: personal accounts have no admin-consent model and work fine over
        // IMAP+OAuth, so moving them to Graph would be a behaviour change for no benefit.
        //
        // Typed one character at a time, because that is what the dialog does and it is where this
        // broke: "kelly@outlook.com" passes through "kelly@o", which matches no consumer domain and
        // so read as a work tenant. The form switched to Graph and blanked the hosts, and nothing
        // brought it back — the finished address selects the same provider, so the provider-changed
        // path never ran again.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);

        TypeAddress(vm, email);
        vm.CommitUsername();

        Assert.Equal(ProviderCatalog.MicrosoftId, vm.SelectedProvider!.Id);
        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
        Assert.Equal("outlook.office365.com", vm.ImapHost);
        Assert.Equal("smtp-mail.outlook.com", vm.SmtpHost);
    }

    [Fact]
    public void TypingAnAddressNeverChangesTheConnectionMethodMidWord()
    {
        // The domain only means something once the address is finished. Nothing about a half-typed
        // one should reach the connection method — switching to Graph clears the hosts AND the
        // password, and a user who is still typing has no idea it happened.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);

        const string address = "kelly@outlook.com";
        for (var i = 1; i <= address.Length; i++)
        {
            vm.Username = address[..i];
            Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
            Assert.Equal("outlook.office365.com", vm.ImapHost);
        }
    }

    [Fact]
    public void EditingAWorkAddressBackToAConsumerOneReturnsToImap()
    {
        // Both directions, because the provider does not change between them: re-selecting the same
        // Microsoft entry returns early, so this is the only thing that can put the hosts back.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        TypeAddress(vm, "kelly@icanbrew.com");
        vm.CommitUsername();
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);

        TypeAddress(vm, "kelly@outlook.com");
        vm.CommitUsername();

        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
        Assert.Equal("outlook.office365.com", vm.ImapHost);
    }

    [Fact]
    public void AConnectionMethodTheUserChoseSurvivesEditingTheAddress()
    {
        // The mirror of AUserPickedProviderSurvivesEditingTheAddress. Without it, one more keystroke
        // in the address forced the account back onto Graph — and the user got no feedback, because
        // the connection-method combo lives inside the collapsed Advanced expander.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        TypeAddress(vm, "kelly@contoso.com");
        vm.CommitUsername();
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);

        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.ImapSmtp);

        TypeAddress(vm, "kelly2@contoso.com");
        vm.CommitUsername();

        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
        Assert.Equal("outlook.office365.com", vm.ImapHost);
    }

    // ── Personal Microsoft accounts on a custom domain (#233) ────────────────────

    [Fact]
    public async Task APersonalMicrosoftAccountOnACustomDomainGoesBackToImapAtSignIn()
    {
        // The domain says work tenant; the token says personal. The token wins — it is the signal
        // #233 added precisely because the domain guess fails on a vanity domain. On Graph such an
        // account draws work scopes that under-deliver for it (#217, #239); on IMAP it is the path
        // that worked before any of this existed.
        var oauth = new StubOAuthService
        {
            SignInUsername = "kelly@theideaplace.net",
            SignInIsPersonalAccount = true,
        };
        var vm = NewVm(oauth);
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        TypeAddress(vm, "kelly@theideaplace.net");
        vm.CommitUsername();
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);   // the domain's guess

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
        // And the hosts are restored, not left blank by the trip through Graph.
        Assert.Equal("outlook.office365.com", vm.ImapHost);
        Assert.Equal("smtp-mail.outlook.com", vm.SmtpHost);

        var account = vm.ToAccountModel();
        Assert.Equal(BackendKind.ImapSmtp, account.BackendKind);
        Assert.True(account.IsPersonalMicrosoftAccount);
    }

    [Fact]
    public async Task AWorkMicrosoftAccountStaysOnGraphAfterSignIn()
    {
        // The other half: a real work tenant must not be dragged back to the IMAP backend whose
        // scopes its administrator has never consented to.
        var oauth = new StubOAuthService
        {
            SignInUsername = "kelly@icanbrew.com",
            SignInIsPersonalAccount = false,
        };
        var vm = NewVm(oauth);
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        TypeAddress(vm, "kelly@icanbrew.com");
        vm.CommitUsername();

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);
        Assert.Equal(BackendKind.MicrosoftGraph, vm.ToAccountModel().BackendKind);
    }

    [Fact]
    public async Task AKnownPersonalAccountIsNotPushedBackToGraphByLaterTyping()
    {
        var oauth = new StubOAuthService
        {
            SignInUsername = "kelly@theideaplace.net",
            SignInIsPersonalAccount = true,
        };
        var vm = NewVm(oauth);
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        TypeAddress(vm, "kelly@theideaplace.net");
        vm.CommitUsername();
        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        // The user corrects the local part afterwards. The domain is still a custom one, but the
        // account is still known to be personal.
        TypeAddress(vm, "kelly.ford@theideaplace.net");
        vm.CommitUsername();

        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    [Fact]
    public async Task ADiscoveredMicrosoftTenantEndsUpOnGraph()
    {
        // The reported path end to end: an M365 custom domain identified from DNS must arrive at
        // Graph, not at the IMAP backend that produces the admin-consent error.
        var ms = Catalog.ById(ProviderCatalog.MicrosoftId)!;
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            ms.ImapHost, ms.ImapPort, ms.ImapUseSsl, ms.SmtpHost, ms.SmtpPort, ms.SmtpUseSsl,
            ms.Id, "icanbrew.com", DiscoverySource.DnsMailHost));
        var vm = NewVm(discover);
        vm.Username = "kelly@icanbrew.com";

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Equal(ProviderCatalog.MicrosoftId, vm.SelectedProvider!.Id);
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);
        Assert.Equal(BackendKind.MicrosoftGraph, vm.ToAccountModel().BackendKind);
    }

    [Fact]
    public void WithGraphGatedOffAWorkAccountStaysOnImap()
    {
        // Nothing better is available; the account is still creatable, and the user finds out at
        // sign-in as they did before.
        var vm = NewVm(graph: false);
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        vm.Username = "kelly@icanbrew.com";

        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    [Fact]
    public void ChoosingMicrosoftThenTypingAWorkAddressKeepsTheChoice()
    {
        // The address matches no catalog domain, but the provider was picked deliberately — undoing
        // it would throw the user's choice away mid-typing.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);

        vm.Username = "kelly@icanbrew.com";

        Assert.Equal(ProviderCatalog.MicrosoftId, vm.SelectedProvider!.Id);
    }

    [Fact]
    public void AUserPickedProviderSurvivesEditingTheAddress()
    {
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.YahooId);

        vm.Username = "kelly@somewhere.example";

        Assert.Equal(ProviderCatalog.YahooId, vm.SelectedProvider!.Id);
        Assert.Equal("imap.mail.yahoo.com", vm.ImapHost);
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

    [Fact]
    public async Task AStaleLookupIsDiscardedOnceTheAddressHasChanged()
    {
        // A lookup can be in flight for up to 12 s. If the user fixes their address meanwhile, the
        // first lookup's servers must not land on the corrected account.
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            "mail.old.example", 993, true, "smtp.old.example", 587, false,
            null, "Old", DiscoverySource.Ispdb));
        var vm = NewVm(discover);
        vm.Username = "kelly@old.example";
        discover.OnCall = () => vm.Username = "kelly@new.example";   // user retypes mid-lookup

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.ImapHost);
        Assert.DoesNotContain("old.example", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryStaysInvokableWhileAnEarlierLookupIsStillRunning()
    {
        // AsyncRelayCommand reports CanExecute=false while running by default, which silently
        // dropped the second lookup and let the first one's result win.
        //
        // The stub BLOCKS until it is released, so the first lookup is provably still in flight
        // when CanExecute is asked. A stub that completed synchronously answered for a command that
        // had already finished, and passed whether or not AllowConcurrentExecutions was set.
        var discover = new BlockingAutoDiscover();
        var vm = NewVm(discover);
        vm.Username = "kelly@theideaplace.net";

        var first = vm.DiscoverSettingsCommand.ExecuteAsync(null);
        await discover.Started;                  // inside DiscoverAsync, and not coming back yet
        Assert.False(first.IsCompleted);

        Assert.True(vm.DiscoverSettingsCommand.CanExecute(null));

        discover.Release();
        await first;
    }

    [Fact]
    public async Task ASettingsFoundMessageNamesTheHostsNotJustTheProvider()
    {
        // These servers are about to receive the user's password and they arrived over the network,
        // so they must be stated rather than applied silently behind a collapsed expander.
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            "mail.example.edu", 993, true, "smtp.example.edu", 587, false,
            null, "Example University", DiscoverySource.Ispdb));
        var vm = NewVm(discover);
        vm.Username = "kelly@example.edu";

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.Contains("mail.example.edu", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("smtp.example.edu", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheUserTypedTheirOwnHostsTheMessageSaysTheyWereKept()
    {
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            "mail.example.edu", 993, true, "smtp.example.edu", 587, false,
            null, "Example University", DiscoverySource.Ispdb));
        var vm = NewVm(discover);
        vm.Username = "kelly@example.edu";
        discover.OnCall = () => vm.ImapHost = "mine.example.edu";   // typed while the lookup ran

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        // Claiming "no settings found" here would be a lie — they were found and deliberately not used.
        Assert.DoesNotContain("No settings found", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("mine.example.edu", vm.ImapHost);
    }

    // ── Edits the user makes by hand ─────────────────────────────────────────────

    [Theory]
    [InlineData("port")]
    [InlineData("ssl")]
    public void ChangingOnlyAPortOrSslFlagCountsAsAUserEdit(string field)
    {
        // Was hosts-only, so a user who changed just the port had it silently overwritten by the
        // next provider match.
        var vm = NewVm();
        if (field == "port") vm.ImapPort = 1993; else vm.ImapUseSsl = false;

        Assert.True(vm.HostsUserEdited);

        vm.Username = "kelly@gmail.com";
        Assert.Equal(ProviderCatalog.OtherId, vm.SelectedProvider!.Id);
    }

    [Fact]
    public void SwitchingConnectionMethodBackToImapLeavesAdvancedOpen()
    {
        // The connection-method combo lives INSIDE Advanced settings. Collapsing the expander the
        // user is standing in removes the focused control from the visual tree and strands keyboard
        // focus on the window, with no announcement.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        vm.IsAdvancedExpanded = true;

        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.MicrosoftGraph);
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.ImapSmtp);

        Assert.True(vm.IsAdvancedExpanded);
        Assert.Equal("outlook.office365.com", vm.ImapHost);
    }

    // ── Saving ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAccountWithNoServersIsNotReadyToSave()
    {
        // The server fields are behind a collapsed expander now, so without this an unrecognised
        // address whose lookup found nothing would be saved with a blank IMAP host, unseen.
        var vm = NewVm();
        vm.Username = "kelly@theideaplace.net";
        vm.Password = "hunter2";

        Assert.False(vm.IsReadyToSave(out var error));
        Assert.Contains("Advanced settings", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPasswordNamesTheAppPasswordWhenTheProviderNeedsOne()
    {
        var vm = NewVm();
        vm.Username = "kelly@yahoo.com";

        Assert.False(vm.IsReadyToSave(out var error));
        Assert.Contains("app password", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingAddressIsNotReadyToSave(string username)
    {
        var vm = NewVm();
        vm.Username = username;

        Assert.False(vm.IsReadyToSave(out _));
    }

    [Fact]
    public void ACompleteAccountIsReadyToSave()
    {
        var vm = NewVm();
        vm.Username = "kelly@gmail.com";
        vm.Password = "app-password";

        Assert.True(vm.IsReadyToSave(out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void AGraphAccountNeedsNoHostsToBeReadyToSave()
    {
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.MicrosoftGraph);
        vm.Username = "kelly@contoso.com";

        Assert.True(vm.IsReadyToSave(out _));
    }

    [Fact]
    public void ClearingThePasswordTellsTheViewSoTheBoxCanBeCleared()
    {
        // A PasswordBox cannot be bound, so without this signal it keeps showing dots for a password
        // the VM has dropped — and the account is saved with none.
        var vm = NewVm();
        vm.Username = "kelly@theideaplace.net";
        vm.Password = "typed-before-switching";
        var cleared = 0;
        vm.PasswordCleared += () => cleared++;

        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);   // OAuth: no password

        Assert.Equal(1, cleared);
        Assert.Equal(string.Empty, vm.Password);
    }

    /// <summary>
    /// A provider that defaults to the Graph backend. No catalog entry does today, which is why the
    /// bug below was unreachable rather than absent — the branch is live code either way.
    /// Password auth deliberately, so ApplyProvider's trailing "OAuth needs no password" clear
    /// cannot mask what the Graph branch itself does.
    /// </summary>
    private static readonly MailProvider GraphFirstProvider = new(
        Id: "graph-first-test",
        DisplayName: "Graph-first provider",
        Domains: ["graphfirst.example"],
        ImapHost: string.Empty, ImapPort: 993, ImapUseSsl: true,
        SmtpHost: string.Empty, SmtpPort: 587, SmtpUseSsl: false,
        DefaultAuthType: AuthType.Password,
        SupportsOAuth: true,
        DefaultBackend: BackendKind.MicrosoftGraph,
        AppPasswordHint: null,
        AppPasswordUrl: null);

    [Fact]
    public void AGraphProviderTellsTheViewItDroppedThePasswordAlreadyTyped()
    {
        var vm = NewVm();
        vm.Password = "typed-before-switching";
        var cleared = 0;
        vm.PasswordCleared += () => cleared++;

        vm.SelectedProvider = GraphFirstProvider;

        // A PasswordBox cannot be data-bound, so assigning Password directly dropped it silently:
        // the box kept showing dots for a password that no longer existed, and the account could be
        // saved with none.
        Assert.Equal(1, cleared);
        Assert.Equal(string.Empty, vm.Password);
    }

    // ── Test Connection ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TestConnectionLeavesNothingBehindInTheRouter()
    {
        // Every probe carries a fresh Guid (BuildProbeAccount), and the router binds an account to a
        // backend when it connects so the following Disconnect reaches the same one. Nothing in
        // production ever unbound them, so the table grew by one entry per press of the button, for
        // the life of the process.
        var router = new MailServiceRouter([new StubImapMailService()]);
        var vm = NewVm(new StubOAuthService(), router);
        vm.Username = "kelly@theideaplace.net";
        vm.ImapHost = "mail.theideaplace.net";
        vm.Password = "hunter2";

        await vm.TestConnectionCommand.ExecuteAsync(null);
        await vm.TestConnectionCommand.ExecuteAsync(null);
        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(0, router.BoundAccountCount);
    }

    [Fact]
    public async Task AFailedTestConnectionLeavesNothingBehindEither()
    {
        // The harder half: a probe that never connected skipped the disconnect entirely, so the
        // binding its connect attempt created stayed forever.
        var router = new MailServiceRouter([new RefusingMailService()]);
        var vm = NewVm(new StubOAuthService(), router);
        vm.Username = "kelly@theideaplace.net";
        vm.ImapHost = "mail.theideaplace.net";
        vm.Password = "hunter2";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("failed", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, router.BoundAccountCount);
    }

    /// <summary>A backend whose connect always fails, the way a wrong host or password does.</summary>
    private sealed class RefusingMailService : StubImapMailServiceBase
    {
        public override Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("connection refused"));
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
    public void ToAccountModelCarriesEveryServerField_OnBothIncomingProtocols()
    {
        // One list of connection fields, shared by the probe account, this method and the Account
        // Manager's load and save (AccountEditorViewModel.WriteServerFieldsTo). It used to be four
        // hand-maintained copies, and a field missed in any one of them is silent — the dialog shows
        // it, the account saves without it.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.Other;
        vm.Username = "kelly@example.com";

        vm.ImapHost = "imap.example.com"; vm.ImapPort = 1143; vm.ImapUseSsl = false; vm.ImapAcceptInvalidCert = true;
        vm.Pop3Host = "pop.example.com";  vm.Pop3Port = 1110; vm.Pop3UseSsl = false; vm.Pop3AcceptInvalidCert = true;
        vm.Pop3LeaveMailOnServer = false;
        vm.SmtpHost = "smtp.example.com"; vm.SmtpPort = 1587; vm.SmtpUseSsl = true;  vm.SmtpAcceptInvalidCert = true;

        var account = vm.ToAccountModel();

        Assert.Equal("imap.example.com", account.ImapHost);
        Assert.Equal(1143, account.ImapPort);
        Assert.False(account.ImapUseSsl);
        Assert.True(account.ImapAcceptInvalidCert);

        Assert.Equal("pop.example.com", account.Pop3Host);
        Assert.Equal(1110, account.Pop3Port);
        Assert.False(account.Pop3UseSsl);
        Assert.True(account.Pop3AcceptInvalidCert);
        // The one with consequences: cleared, the next collection is the last chance any other
        // client has to see that mail.
        Assert.False(account.Pop3LeaveMailOnServer);

        Assert.Equal("smtp.example.com", account.SmtpHost);
        Assert.Equal(1587, account.SmtpPort);
        Assert.True(account.SmtpUseSsl);
        Assert.True(account.SmtpAcceptInvalidCert);
    }

    [Fact]
    public void APop3ProvidersTransportSecurityComesFromTheCatalog_NotFromItsPortNumber()
    {
        // Pop3UseSsl is stated by the catalog entry, the shape ImapUseSsl and SmtpUseSsl already
        // have. Inferring it from "is the port 995?" silently downgrades a provider that offers
        // implicit TLS on any other port to STARTTLS, at the moment its settings are filled in.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.GmailId);

        Assert.Equal("pop.gmail.com", vm.Pop3Host);
        Assert.True(vm.Pop3UseSsl);

        var onANonStandardPort = new MailProvider(
            "test", "Test", ["test.example"], "imap.test.example", 993, true,
            "smtp.test.example", 587, false, AuthType.Password, false, BackendKind.ImapSmtp,
            null, null, Pop3Host: "pop.test.example", Pop3Port: 1995, Pop3UseSsl: true);

        Assert.True(onANonStandardPort.Pop3UseSsl);
        Assert.True(onANonStandardPort.SupportsPop3);
    }

    // ── Transport security (who chose the servers decides the fallback) ──────────

    [Fact]
    public async Task DiscoveredServersReachTheSavedAccountRequiringStartTls()
    {
        // The settings arrived over the network on port 587, where SmtpUseSsl is false — which alone
        // meant StartTlsWhenAvailable, i.e. "authenticate in plaintext if the server offers no
        // STARTTLS". Whoever answered for this domain could therefore harvest the password, behind a
        // collapsed Advanced expander that only said "Settings found".
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            "mail.example.edu", 143, false, "smtp.example.edu", 587, false,
            ProviderId: null, DisplayName: "Example University", DiscoverySource.Ispdb));
        var vm = NewVm(discover);
        vm.Username = "kelly@example.edu";

        await vm.DiscoverSettingsCommand.ExecuteAsync(null);

        Assert.True(vm.RequireStartTls);
        Assert.True(vm.ToAccountModel().RequireStartTls);
    }

    [Fact]
    public void AProviderFromTheCatalogAlsoRequiresStartTls()
    {
        // Gmail's SMTP entry is 587/STARTTLS. The catalog is QuickMail's choice, not the user's, and
        // every provider in it genuinely offers STARTTLS or implicit TLS.
        var vm = NewVm();
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.GmailId);

        Assert.False(vm.SmtpUseSsl);
        Assert.True(vm.ToAccountModel().RequireStartTls);
    }

    [Fact]
    public void ServersTypedByHandKeepThePermissiveBehaviour()
    {
        // Someone deliberately pointing QuickMail at their own server may still choose opportunistic
        // STARTTLS. The restriction is on settings QuickMail accepted on their behalf.
        var vm = NewVm();
        vm.Username = "kelly@theideaplace.net";
        vm.ImapHost = "mail.theideaplace.net";
        vm.SmtpHost = "smtp.theideaplace.net";

        Assert.False(vm.ToAccountModel().RequireStartTls);
    }

    [Fact]
    public async Task EditingAServerAfterDiscoveryHandsTheChoiceBackToTheUser()
    {
        var discover = new StubAutoDiscover(new DiscoveredSettings(
            "mail.example.edu", 993, true, "smtp.example.edu", 587, false,
            null, "Example University", DiscoverySource.Ispdb));
        var vm = NewVm(discover);
        vm.Username = "kelly@example.edu";
        await vm.DiscoverSettingsCommand.ExecuteAsync(null);
        Assert.True(vm.RequireStartTls);

        vm.SmtpHost = "relay.mine.example";   // typed in Advanced settings

        Assert.False(vm.ToAccountModel().RequireStartTls);
    }

    [Fact]
    public void CorrectingATypodAddressDropsTheRequirementWithTheServers()
    {
        // ResetToUnknownProvider blanks the hosts; leaving a requirement behind for servers that no
        // longer exist would be state with nothing to apply to.
        var vm = NewVm();
        vm.Username = "kelly@gmail.com";
        Assert.True(vm.RequireStartTls);

        vm.Username = "kelly@theideaplace.net";

        Assert.False(vm.RequireStartTls);
    }

    [Fact]
    public void DisposeIsSafeToCallTwice()
    {
        var vm = NewVm(new StubAutoDiscover(null));
        vm.Dispose();
        vm.Dispose();   // AddAccountDialog.OnClosed can fire after an explicit dispose
    }

    /// <summary>
    /// A lookup that does not return until it is told to. Lets a test hold one call open and ask
    /// questions about the command while it is genuinely running.
    /// </summary>
    private sealed class BlockingAutoDiscover : IAutoDiscoverService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<DiscoveredSettings?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once DiscoverAsync has been entered.</summary>
        public Task Started => _started.Task;

        /// <summary>Lets the blocked lookup finish, finding nothing.</summary>
        public void Release() => _result.TrySetResult(null);

        public Task<DiscoveredSettings?> DiscoverAsync(string emailAddress, CancellationToken ct)
        {
            _started.TrySetResult();
            return _result.Task;
        }
    }

    private sealed class StubAutoDiscover(DiscoveredSettings? result) : IAutoDiscoverService
    {
        public int Calls { get; private set; }

        /// <summary>Runs inside the lookup, to simulate the user typing while it is in flight.</summary>
        public Action? OnCall { get; set; }

        public Task<DiscoveredSettings?> DiscoverAsync(string emailAddress, CancellationToken ct)
        {
            Calls++;
            OnCall?.Invoke();
            return Task.FromResult(result);
        }
    }
}
