using System;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #31 PR 2.5: adding a shared mailbox drives the one-time shared-mailbox consent on the PARENT account
/// (Mail.ReadWrite.Shared + Mail.Send.Shared) — silent if already granted, otherwise the interactive
/// Microsoft consent window. A declined / admin-approval-pending consent must NOT roll back the add nor
/// throw: the mailbox is kept (added, and disconnected until consent lands), never a silent failure.
/// </summary>
public class SharedMailboxConsentTests
{
    private static (AccountManagerViewModel Vm, StubOAuthService Oauth, AccountModel Parent) Setup()
    {
        var oauth = new StubOAuthService();
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            oauth, new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), new ProviderCatalog());
        var parent = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "Work", Username = "user@work.com",
            BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft,
        };
        vm.Accounts.Add(parent);
        return (vm, oauth, parent);
    }

    private static AccountModel SharedUnder(AccountModel parent, string addr) => new()
    {
        Id = Guid.NewGuid(), AccountName = addr, Username = addr, SharedAddress = addr,
        IsShared = true, ParentAccountId = parent.Id, BackendKind = parent.BackendKind,
        AuthType = parent.AuthType,
    };

    [Fact]
    public async Task AddingSharedMailbox_DrivesConsentOnTheParent()
    {
        var (vm, oauth, parent) = Setup();
        var shared = SharedUnder(parent, "support@work.com");

        await vm.CommitNewSharedMailboxAsync(shared);

        Assert.Contains(parent.Id, oauth.SharedConsentRequestedFor);   // consent driven on the parent
        Assert.Contains(vm.Accounts, a => a.Id == shared.Id);          // and the mailbox is added
    }

    [Fact]
    public async Task ConsentDeclinedOrPending_KeepsTheMailbox_NoRollback_NoThrow()
    {
        var (vm, oauth, parent) = Setup();
        // Simulate a decline / admin-approval-pending consent.
        oauth.ThrowOnSharedMailboxConsent = new InvalidOperationException("admin approval required");
        var shared = SharedUnder(parent, "support@work.com");

        // Must not throw — a failed consent leaves the mailbox added (disconnected until consent lands),
        // never rolls it back and never bubbles an exception out of the add.
        await vm.CommitNewSharedMailboxAsync(shared);

        Assert.Contains(parent.Id, oauth.SharedConsentRequestedFor);
        Assert.Contains(vm.Accounts, a => a.Id == shared.Id);   // kept, not rolled back
    }
}
