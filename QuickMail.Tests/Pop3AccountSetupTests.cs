using System;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Adding and editing a POP3 account (#128): the feature gate that decides whether POP3 is offered
/// at all, what selecting it does to the form, and what reaches <see cref="AccountModel"/>.
/// </summary>
public class Pop3AddAccountTests
{
    private static AddAccountViewModel Make(bool pop3Enabled, bool graphEnabled = true)
    {
        var gate = new StubFeatureGate
        {
            [FeatureFlag.GraphBackend] = graphEnabled,
            [FeatureFlag.Pop3Backend]  = pop3Enabled,
        };
        return new AddAccountViewModel(gate, new StubImapMailService(), new StubOAuthService(), new ProviderCatalog());
    }

    private static BackendKindOption Pop3Option(AddAccountViewModel vm) =>
        vm.AvailableBackends.First(b => b.Kind == BackendKind.Pop3Smtp);

    [Fact]
    public void GateOff_Pop3IsNotOffered()
    {
        var vm = Make(pop3Enabled: false);
        Assert.DoesNotContain(vm.AvailableBackends, b => b.Kind == BackendKind.Pop3Smtp);
    }

    [Fact]
    public void GateOn_Pop3IsOffered()
    {
        var vm = Make(pop3Enabled: true);
        Assert.Contains(vm.AvailableBackends, b => b.Kind == BackendKind.Pop3Smtp);
        // Still IMAP by default — enabling the option must not move new accounts onto POP3.
        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    [Fact]
    public void GateOn_TheConnectionMethodComboIsReachableForAnyProvider()
    {
        // IMAP-versus-Graph is a Microsoft-only question, so the combo used to appear only there.
        // POP3 is offered for every provider — and is usually wanted by someone whose provider
        // QuickMail has never heard of — so hiding the combo would make it unreachable.
        var vm = Make(pop3Enabled: true);
        var catalog = new ProviderCatalog();

        vm.SelectedProvider = catalog.Other;
        Assert.True(vm.ShowConnectionMethod);

        vm.SelectedProvider = catalog.ById(ProviderCatalog.GmailId);
        Assert.True(vm.ShowConnectionMethod);
    }

    [Fact]
    public void GateOff_TheComboIsStillMicrosoftOnly()
    {
        var vm = Make(pop3Enabled: false);
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.GmailId);
        Assert.False(vm.ShowConnectionMethod);
    }

    [Fact]
    public void SelectingPop3_ForcesPasswordAuthAndShowsTheServerFields()
    {
        var vm = Make(pop3Enabled: true);
        vm.SelectedBackend = Pop3Option(vm);

        Assert.Equal(BackendKind.Pop3Smtp, vm.BackendKind);
        Assert.True(vm.IsPop3Backend);
        Assert.False(vm.IsImapBackend);
        Assert.False(vm.IsGraphBackend);
        // There is no OAuth path through the POP3 backend.
        Assert.Equal(AuthType.Password, vm.AuthType);
        // Both host-based backends need the server block: POP3 to receive, SMTP to send.
        Assert.True(vm.ShowServerSettings);
        Assert.Equal("POP3", vm.IncomingHostLabel);
    }

    [Fact]
    public void SelectingPop3_FillsInAKnownProvidersPop3Host()
    {
        var vm = Make(pop3Enabled: true);
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.GmailId);
        vm.SelectedBackend = Pop3Option(vm);

        Assert.Equal("pop.gmail.com", vm.Pop3Host);
        Assert.Equal(995, vm.Pop3Port);
        Assert.True(vm.Pop3UseSsl);
        // Sending is unchanged — POP3 accounts still send over the provider's SMTP host.
        Assert.Equal("smtp.gmail.com", vm.SmtpHost);
    }

    [Fact]
    public void MovingFromGraphToPop3_PutsTheHostsBack()
    {
        // Graph clears the host fields, and the POP3 branch has to restore them or the account is
        // saved with no way to send and no way to receive.
        var vm = Make(pop3Enabled: true);
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.MicrosoftId);
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.MicrosoftGraph);
        Assert.Equal(string.Empty, vm.SmtpHost);

        vm.SelectedBackend = Pop3Option(vm);

        Assert.Equal("outlook.office365.com", vm.Pop3Host);
        Assert.Equal("smtp-mail.outlook.com", vm.SmtpHost);
    }

    [Fact]
    public void AProviderWithNoPop3Service_LeavesTheHostForTheUserToType()
    {
        // iCloud is IMAP-only. Pre-filling a guessed host would hand the user a server that never
        // answers; an empty box plus the IsReadyToSave message is the honest outcome.
        var vm = Make(pop3Enabled: true);
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.ICloudId);
        vm.SelectedBackend = Pop3Option(vm);

        Assert.Equal(string.Empty, vm.Pop3Host);

        vm.Username = "someone@icloud.com";
        vm.Password = "app-specific";
        Assert.False(vm.IsReadyToSave(out var error));
        Assert.Contains("POP3", error);
    }

    [Fact]
    public void TypingTheAddressDoesNotUndoAHandPickedPop3()
    {
        // Typing an address matches a provider and re-applies its default connection method. That
        // used to throw away a POP3 choice made moments earlier — invisibly, because the combo is
        // inside the collapsed Advanced expander.
        var vm = Make(pop3Enabled: true);
        vm.SelectedBackend = Pop3Option(vm);

        vm.Username = "kelly@gmail.com";
        vm.CommitUsername();

        Assert.Equal(BackendKind.Pop3Smtp, vm.BackendKind);
        Assert.Equal(AuthType.Password, vm.AuthType);
        // The provider still applies everything else about itself, including its POP3 host.
        Assert.Equal(ProviderCatalog.GmailId, vm.SelectedProvider?.Id);
        Assert.Equal("pop.gmail.com", vm.Pop3Host);
    }

    [Fact]
    public void AProviderChangeStillMovesAnAccountOffGraph()
    {
        // The mirror of the test above, and the reason only POP3 is preserved: Graph belongs to the
        // Microsoft provider, so picking a different provider must be free to move off it.
        var vm = Make(pop3Enabled: true);
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.MicrosoftId);
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.MicrosoftGraph);
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);

        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.GmailId);

        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    [Fact]
    public void PickingAnOAuthProviderWhileOnPop3_KeepsPasswordAuth()
    {
        // POP3 has no OAuth path here, so taking the provider's OAuth default would leave the
        // account unable to authenticate at all.
        var vm = Make(pop3Enabled: true);
        vm.SelectedBackend = Pop3Option(vm);

        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.MicrosoftId);

        Assert.Equal(BackendKind.Pop3Smtp, vm.BackendKind);
        Assert.Equal(AuthType.Password, vm.AuthType);
    }

    [Fact]
    public void SaveIsRefusedUntilThePop3HostIsFilledIn_AndTheImapHostIsIrrelevant()
    {
        var vm = Make(pop3Enabled: true);
        vm.SelectedBackend = Pop3Option(vm);
        vm.Username = "kelly@example.com";
        vm.Password = "secret";
        vm.SmtpHost = "smtp.example.com";
        vm.ImapHost = "imap.example.com";   // must not stand in for the POP3 host

        Assert.False(vm.IsReadyToSave(out var error));
        Assert.Contains("POP3", error);

        vm.Pop3Host = "pop.example.com";
        Assert.True(vm.IsReadyToSave(out _));
    }

    [Fact]
    public void ToAccountModel_CarriesEveryPop3Setting()
    {
        var vm = Make(pop3Enabled: true);
        vm.SelectedBackend = Pop3Option(vm);
        vm.Username = "kelly@example.com";
        vm.Pop3Host = "pop.example.com";
        vm.Pop3Port = 110;
        vm.Pop3UseSsl = false;
        vm.Pop3AcceptInvalidCert = true;
        vm.Pop3LeaveMailOnServer = false;

        var account = vm.ToAccountModel();

        Assert.Equal(BackendKind.Pop3Smtp, account.BackendKind);
        Assert.Equal("pop.example.com", account.Pop3Host);
        Assert.Equal(110, account.Pop3Port);
        Assert.False(account.Pop3UseSsl);
        Assert.True(account.Pop3AcceptInvalidCert);
        Assert.False(account.Pop3LeaveMailOnServer);
    }

    [Fact]
    public void LeaveMailOnServer_DefaultsToKeepingIt()
    {
        // The local store becomes the only copy the moment this is off, so the safe setting is the
        // default and turning it off is a deliberate act.
        Assert.True(Make(pop3Enabled: true).Pop3LeaveMailOnServer);
        Assert.True(new AccountModel().Pop3LeaveMailOnServer);
    }
}

/// <summary>Editing an existing POP3 account in the Account Manager round-trips its settings.</summary>
public class Pop3AccountManagerTests
{
    private static AccountManagerViewModel NewVm() => new(
        new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
        new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
        new StubFeatureGate(), new ProviderCatalog());

    private static AccountModel Pop3Account() => new()
    {
        Id = Guid.NewGuid(),
        AccountName = "POP account",
        Username = "kelly@example.com",
        AuthType = AuthType.Password,
        BackendKind = BackendKind.Pop3Smtp,
        Pop3Host = "pop.example.com",
        Pop3Port = 995,
        Pop3UseSsl = true,
        Pop3LeaveMailOnServer = true,
        SmtpHost = "smtp.example.com",
    };

    [Fact]
    public void SelectingAPop3Account_LoadsItsSettingsAndShowsTheRightSection()
    {
        var vm = NewVm();
        var account = Pop3Account();
        vm.Accounts.Add(account);
        vm.SelectedAccount = account;

        Assert.True(vm.IsPop3Backend);
        Assert.False(vm.IsImapBackend);
        Assert.True(vm.ShowServerSettings);
        Assert.Equal("pop.example.com", vm.Pop3Host);
        Assert.Equal(995, vm.Pop3Port);
        Assert.True(vm.Pop3LeaveMailOnServer);
    }

    [Fact]
    public void EditingLeaveMailOnServer_IsSaved()
    {
        var vm = NewVm();
        var account = Pop3Account();
        vm.Accounts.Add(account);
        vm.SelectedAccount = account;

        vm.Pop3LeaveMailOnServer = false;
        vm.Pop3Host = "pop3.example.net";
        vm.SaveAccountCommand.Execute(null);

        Assert.False(account.Pop3LeaveMailOnServer);
        Assert.Equal("pop3.example.net", account.Pop3Host);
    }
}

/// <summary>
/// The incoming leg reported per backend (#128). Code that asks "which server does this account
/// receive from" used to read the IMAP fields directly, which describes a POP3 account as the empty
/// host on port 993 — the same root cause as the blank server the Account Properties window showed.
/// </summary>
public class AccountIncomingLegTests
{
    [Fact]
    public void APop3AccountReportsItsPop3Server()
    {
        var account = new AccountModel
        {
            BackendKind = BackendKind.Pop3Smtp,
            Pop3Host = "mail.example.com", Pop3Port = 995, Pop3UseSsl = true, Pop3AcceptInvalidCert = true,
            ImapHost = string.Empty, ImapPort = 993, ImapUseSsl = true,   // untouched defaults
        };

        Assert.Equal("mail.example.com", account.IncomingHost);
        Assert.Equal(995, account.IncomingPort);
        Assert.True(account.IncomingUseSsl);
        Assert.True(account.IncomingAcceptInvalidCert);
    }

    [Fact]
    public void AnImapAccountIsUnchanged()
    {
        var account = new AccountModel
        {
            BackendKind = BackendKind.ImapSmtp,
            ImapHost = "imap.example.com", ImapPort = 143, ImapUseSsl = false,
        };

        Assert.Equal("imap.example.com", account.IncomingHost);
        Assert.Equal(143, account.IncomingPort);
        Assert.False(account.IncomingUseSsl);
    }

    [Fact]
    public void AGraphAccountHasNoIncomingServer()
    {
        var account = new AccountModel { BackendKind = BackendKind.MicrosoftGraph, ImapPort = 993 };

        Assert.Equal(string.Empty, account.IncomingHost);
        Assert.Equal(0, account.IncomingPort);
    }

    [Fact]
    public void TwoPop3AccountsOnDifferentServersAreNotTheSameHost()
    {
        // Connect-time grouping serializes accounts that share a host, which matters more for POP3
        // than IMAP: RFC 1939 locks the maildrop for the session. Grouping on the IMAP field put
        // every POP3 account in one bucket — both of these looked like the same empty host.
        var first  = new AccountModel { BackendKind = BackendKind.Pop3Smtp, Pop3Host = "pop.one.example" };
        var second = new AccountModel { BackendKind = BackendKind.Pop3Smtp, Pop3Host = "pop.two.example" };

        Assert.NotEqual(first.IncomingHost, second.IncomingHost);
    }

    [Fact]
    public void APop3AccountAndAnImapAccountOnOneHostShareIt()
    {
        var pop3 = new AccountModel { BackendKind = BackendKind.Pop3Smtp, Pop3Host = "mail.example.com" };
        var imap = new AccountModel { BackendKind = BackendKind.ImapSmtp, ImapHost = "mail.example.com" };

        Assert.Equal(pop3.IncomingHost, imap.IncomingHost);
    }
}

/// <summary>The POP3 half of the provider catalog.</summary>
public class Pop3ProviderCatalogTests
{
    private readonly ProviderCatalog _catalog = new();

    [Theory]
    [InlineData(ProviderCatalog.GmailId,     "pop.gmail.com")]
    [InlineData(ProviderCatalog.MicrosoftId, "outlook.office365.com")]
    [InlineData(ProviderCatalog.YahooId,     "pop.mail.yahoo.com")]
    public void KnownProvidersCarryTheirPop3Host(string providerId, string expectedHost)
    {
        var provider = _catalog.ById(providerId);
        Assert.NotNull(provider);
        Assert.True(provider!.SupportsPop3);
        Assert.Equal(expectedHost, provider.Pop3Host);
        Assert.Equal(995, provider.Pop3Port);
    }

    [Fact]
    public void ICloudDoesNotClaimPop3() // iCloud Mail serves IMAP only
    {
        var provider = _catalog.ById(ProviderCatalog.ICloudId);
        Assert.NotNull(provider);
        Assert.False(provider!.SupportsPop3);
    }

    [Fact]
    public void OtherDoesNotClaimPop3()
        => Assert.False(_catalog.Other.SupportsPop3);
}

/// <summary>The <c>Pop3Backend</c> gate itself.</summary>
public class Pop3FeatureGateTests
{
    [Fact]
    public void DefaultsOff() // opt-in until it has run against real servers
        => Assert.False(new ConfigFeatureGate(new ConfigModel(), Array.Empty<string>())
            .IsEnabled(FeatureFlag.Pop3Backend));

    [Fact]
    public void ConfigTurnsItOn()
    {
        var config = new ConfigModel();
        config.Features["Pop3Backend"] = "true";
        Assert.True(new ConfigFeatureGate(config, Array.Empty<string>()).IsEnabled(FeatureFlag.Pop3Backend));
    }

    [Fact]
    public void CommandLineTurnsItOn()
        => Assert.True(new ConfigFeatureGate(new ConfigModel(), new[] { "Pop3Backend" })
            .IsEnabled(FeatureFlag.Pop3Backend));
}

/// <summary>
/// A shared mailbox reads through a parent account's mailbox access. POP3 has no such concept — one
/// maildrop, no folders, no delegation — so a POP3 account must not be offered as a parent (#31/#128).
/// </summary>
public class Pop3SharedMailboxTests
{
    [Fact]
    public void APop3AccountIsNotOfferedAsAParent()
    {
        var imap = new AccountModel { Id = Guid.NewGuid(), AccountName = "IMAP", BackendKind = BackendKind.ImapSmtp };
        var pop3 = new AccountModel { Id = Guid.NewGuid(), AccountName = "POP3", BackendKind = BackendKind.Pop3Smtp };

        var vm = new AddSharedMailboxViewModel([imap, pop3]);

        Assert.Contains(vm.ParentOptions, a => a.Id == imap.Id);
        Assert.DoesNotContain(vm.ParentOptions, a => a.Id == pop3.Id);
    }
}
