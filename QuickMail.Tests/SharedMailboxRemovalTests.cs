using System;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pins shared-mailbox removal (#31, spec §5.4): removing a parent cascades to its shared mailboxes
/// (with a naming confirmation the user can decline); removing a shared mailbox on its own is an
/// ordinary single delete that leaves the parent intact.
/// </summary>
public class SharedMailboxRemovalTests
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
    public async Task RemovingParent_CascadesToSharedChildren_LeavesOthers()
    {
        var parent = Parent("Work");
        var shared = SharedOf(parent, "support@work.com");
        var other  = Parent("Home");
        var vm = Manager(parent, shared, other);
        vm.ConfirmCascadeRemoval = _ => true;   // user confirms the cascade
        vm.SelectedAccount = parent;

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.Accounts, a => a.Id == parent.Id);
        Assert.DoesNotContain(vm.Accounts, a => a.Id == shared.Id);   // cascaded away
        Assert.Contains(vm.Accounts, a => a.Id == other.Id);          // untouched
    }

    [Fact]
    public async Task RemovingSharedMailboxOnly_LeavesParent()
    {
        var parent = Parent("Work");
        var shared = SharedOf(parent, "support@work.com");
        var vm = Manager(parent, shared);
        vm.SelectedAccount = shared;

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.Accounts, a => a.Id == shared.Id);
        Assert.Contains(vm.Accounts, a => a.Id == parent.Id);         // parent intact — no reverse cascade
    }

    [Fact]
    public async Task CascadeConfirmationDeclined_RemovesNothing()
    {
        var parent = Parent("Work");
        var shared = SharedOf(parent, "support@work.com");
        var vm = Manager(parent, shared);
        vm.ConfirmCascadeRemoval = _ => false;   // user declines
        vm.SelectedAccount = parent;

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.Contains(vm.Accounts, a => a.Id == parent.Id);
        Assert.Contains(vm.Accounts, a => a.Id == shared.Id);
    }
}

/// <summary>
/// Pins the Account Manager editor gating for a selected shared mailbox (#31): a shared account has no
/// credentials or connection of its own, so the connection/auth/sign-in/Test-Connection surface and the
/// contact/calendar sync (out of scope by spec — mail only) are all hidden, and a read-only summary
/// names the parent. A normal account is unaffected.
/// </summary>
public class SharedMailboxEditorTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AccountManagerViewModel Manager(params AccountModel[] accounts)
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), Catalog,
            contactSync: new StubContactSyncService(), graphCalendarSync: new StubGraphCalendarSyncService());
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
        AuthType = parent.AuthType,
    };

    [Fact]
    public void SharedSelected_HidesConnectionAndSyncSurface()
    {
        var parent = Parent("Work");
        var shared = SharedOf(parent, "support@work.com");
        var vm = Manager(parent, shared);

        vm.SelectedAccount = shared;

        Assert.True(vm.IsSharedSelected);
        Assert.False(vm.ShowConnectionEditing); // password / OAuth sign-in / Advanced servers hidden
        Assert.False(vm.ShowTestConnection);    // nothing of its own to test
        Assert.False(vm.CanSyncContacts);       // mail only, per spec
        Assert.False(vm.CanSyncCalendar);
        Assert.Equal("Work", vm.SharedParentName);
    }

    [Fact]
    public void NormalMicrosoftSelected_KeepsFullSurface()
    {
        var parent = Parent("Work");
        var shared = SharedOf(parent, "support@work.com");
        var vm = Manager(parent, shared);

        vm.SelectedAccount = parent;

        Assert.False(vm.IsSharedSelected);
        Assert.True(vm.ShowConnectionEditing);
        Assert.True(vm.ShowTestConnection);
        Assert.True(vm.CanSyncContacts);        // a normal Microsoft account still offers sync
        Assert.True(vm.CanSyncCalendar);
        Assert.Equal(string.Empty, vm.SharedParentName);
    }

    [Fact]
    public void SwitchingSharedToNormal_RestoresSurface()
    {
        var parent = Parent("Work");
        var shared = SharedOf(parent, "support@work.com");
        var vm = Manager(parent, shared);

        vm.SelectedAccount = shared;
        Assert.False(vm.ShowConnectionEditing);

        // Selecting a normal account must bring the connection surface back — the gate tracks the
        // selection, it is not a one-way latch.
        vm.SelectedAccount = parent;
        Assert.True(vm.ShowConnectionEditing);
        Assert.True(vm.ShowTestConnection);
    }
}
