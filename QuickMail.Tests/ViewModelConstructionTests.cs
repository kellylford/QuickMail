// Startup smoke tests — these are the tests that catch crashes before the app loads.
// They run on the GitHub Actions Windows runner and provide CI-level confidence
// that the app can at least initialise without throwing.

using System;
using QuickMail.Controls;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests that can run without a display (no WPF window shown).
/// These cover ViewModel construction — the most common crash site.
/// </summary>
public class ViewModelConstructionTests
{
    private static (StubImapMailService imap, StubAccountService accounts, StubCredentialService creds,
        StubLocalStoreService store, StubSyncService sync, StubConfigService config,
        StubCommandRegistry registry, StubContactService contacts, StubTemplateService templates)
        MakeServices()
    {
        return (new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubContactService(), new StubTemplateService());
    }

    [Fact]
    public void MainViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());
        Assert.NotNull(vm);
    }

    [Fact]
    public void MainViewModel_LoadAccountList_DoesNotThrow()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList(); // must not throw
    }

    [Fact]
    public void UnifiedRulesViewModel_ConstructsWithoutException()
    {
        var vm = new UnifiedRulesViewModel(new StubRuleService(), serverRules: null, accounts: []);
        Assert.NotNull(vm);
    }

    [Fact]
    public void ComposeViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, _, _, _, _, _, templates) = MakeServices();
        var vm = new ComposeViewModel(new StubSmtpService(), accounts, creds, imap, templates);
        Assert.NotNull(vm);
    }

    [Fact]
    public void TemplatePickerViewModel_ConstructsWithoutException()
    {
        var (_, _, _, _, _, _, _, _, templates) = MakeServices();
        var vm = new TemplatePickerViewModel(templates);
        Assert.NotNull(vm);
    }

    [Fact]
    public void AccountManagerViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, _, _, _, _, _, _) = MakeServices();
        var (_, _, _, store2, _, config2, _, _, _) = MakeServices();
        var vm = new AccountManagerViewModel(accounts, creds, imap, new StubOAuthService(), store2, config2, new StubFeatureGate(), new ProviderCatalog());
        Assert.NotNull(vm);
    }

    [Fact]
    public void GroupManagerViewModel_ConstructsWithoutException()
    {
        var (_, _, _, _, _, _, _, contacts, _) = MakeServices();
        var vm = new GroupManagerViewModel(contacts);
        Assert.NotNull(vm);
    }

    [Fact]
    public void AddressBookViewModel_HasGroupCollections_AfterConstruction()
    {
        var (_, _, _, _, _, _, _, contacts, _) = MakeServices();
        var vm = new AddressBookViewModel(contacts);
        Assert.NotNull(vm.Groups);
        Assert.Empty(vm.Groups);
        Assert.NotNull(vm.SelectedGroupMembers);
        Assert.Empty(vm.SelectedGroupMembers);
        Assert.False(vm.HasSelectedGroup);
        Assert.Equal(string.Empty, vm.NewGroupName);
    }

    [Fact]
    public void TutorialViewModel_ConstructsWithoutException()
    {
        var vm = new TutorialViewModel();
        Assert.NotNull(vm);
        Assert.Equal(6, vm.Steps.Count);
        Assert.False(vm.IsActive);
    }

    // ── Calendar invite tests ───────────────────────────────────────────────────

    [Fact]
    public void MainViewModel_HasCalendarInvite_IsFalseByDefault()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.False(vm.HasCalendarInvite);
    }

    // (The no-invite empty-card case moved to EventCardRenderTests.Card_WithNoInvite_IsEmpty when
    // the card builder left MainViewModel; keeping a VM-constructing copy here only duplicated it.)

    [Fact]
    public void MainViewModel_AcceptInviteCommand_Exists()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.NotNull(vm.AcceptInviteCommand);
        Assert.True(vm.AcceptInviteCommand.CanExecute(null));
    }

    [Fact]
    public void MainViewModel_DeclineInviteCommand_Exists()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.NotNull(vm.DeclineInviteCommand);
        Assert.True(vm.DeclineInviteCommand.CanExecute(null));
    }

    [Fact]
    public void MainViewModel_TentativeInviteCommand_Exists()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.NotNull(vm.TentativeInviteCommand);
        Assert.True(vm.TentativeInviteCommand.CanExecute(null));
    }

    [Fact]
    public void MainViewModel_InviteCommandsRegisteredInRegistry()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        // MainViewModel constructor calls RegisterCommands which registers invite commands
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        var acceptCmd = registry.FindById("mail.acceptInvite");
        var declineCmd = registry.FindById("mail.declineInvite");
        var tentativeCmd = registry.FindById("mail.tentativeInvite");

        Assert.NotNull(acceptCmd);
        Assert.Equal("Accept Invitation", acceptCmd!.Title);
        Assert.Equal("Mail", acceptCmd.Category);

        Assert.NotNull(declineCmd);
        Assert.Equal("Decline Invitation", declineCmd!.Title);
        Assert.Equal("Mail", declineCmd.Category);

        Assert.NotNull(tentativeCmd);
        Assert.Equal("Tentatively Accept Invitation", tentativeCmd!.Title);
        Assert.Equal("Mail", tentativeCmd.Category);
    }
}