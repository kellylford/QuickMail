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
