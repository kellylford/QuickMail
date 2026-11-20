using System;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #31 PR 5: the shared-mailbox new-mail notification opt-in on <see cref="AccountManagerViewModel"/>.
/// The checkbox's Click handler calls <see cref="AccountManagerViewModel.SetNotifyOnNewMail"/>, which
/// applies to the SELECTED account only, is a no-op for a normal or null selection, and writes to the
/// account currently selected (not a previously selected one).
/// </summary>
public class SharedMailboxNotifyOptInTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AccountManagerViewModel Manager(params AccountModel[] accounts)
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), Catalog);
        foreach (var a in accounts) vm.Accounts.Add(a);
        return vm;
    }

    private static AccountModel Parent(string name) => new()
    {
        Id = Guid.NewGuid(), AccountName = name, Username = name.ToLowerInvariant() + "@work.com",
        BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft,
    };
    private static AccountModel SharedOf(AccountModel parent, string addr) => new()
    {
        Id = Guid.NewGuid(), AccountName = addr, Username = addr, SharedAddress = addr,
        IsShared = true, ParentAccountId = parent.Id, BackendKind = parent.BackendKind,
    };

    [Fact]
    public void SetNotifyOnNewMail_OnSharedAccount_AppliesToThatAccount()
    {
        var parent = Parent("Work");
        var shared = SharedOf(parent, "support@work.com");
        var vm = Manager(parent, shared);
        vm.SelectedAccount = shared;

        vm.SetNotifyOnNewMail(true);

        Assert.True(shared.NotifyOnNewMail);
        vm.SetNotifyOnNewMail(false);
        Assert.False(shared.NotifyOnNewMail);
    }

    [Fact]
    public void SetNotifyOnNewMail_OnNormalAccount_IsNoOp()
    {
        var normal = Parent("Work");
        var vm = Manager(normal);
        vm.SelectedAccount = normal;

        vm.SetNotifyOnNewMail(true);

        Assert.False(normal.NotifyOnNewMail);   // the opt-in is meaningless for a normal account
    }

    [Fact]
    public void SetNotifyOnNewMail_WritesToCurrentSelection_NotAPreviousOne()
    {
        var parent = Parent("Work");
        var sharedA = SharedOf(parent, "a@work.com");
        var sharedB = SharedOf(parent, "b@work.com");
        var vm = Manager(parent, sharedA, sharedB);

        vm.SelectedAccount = sharedA;   // select A, then switch to B before toggling
        vm.SelectedAccount = sharedB;
        vm.SetNotifyOnNewMail(true);

        Assert.False(sharedA.NotifyOnNewMail);   // A untouched
        Assert.True(sharedB.NotifyOnNewMail);    // only the current selection changes
    }

    [Fact]
    public void SelectingSharedAccount_ReflectsItsNotifyState_InTheCheckbox()
    {
        var parent = Parent("Work");
        var shared = SharedOf(parent, "support@work.com");
        shared.NotifyOnNewMail = true;
        var vm = Manager(parent, shared);

        vm.SelectedAccount = shared;

        Assert.True(vm.NotifyOnNewMail);   // OnSelectedAccountChanged mirrors the account's saved value
    }
}
