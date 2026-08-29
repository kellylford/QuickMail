// Removing an account that still holds drafts of its own — issue #637.
//
// Removing an account purges its local store. Before drafts were saved locally first, everything in
// there was a cached copy of something the server still had, so the purge cost nothing and asking
// would have been noise. That is no longer true: a draft written offline exists on this computer
// and nowhere else, and so does one the server refused to store. Removing the account destroys
// them, permanently, and they cannot be downloaded again.
//
// The confirmation fails CLOSED, like the shared-mailbox cascade it sits beside: with no way to
// obtain a yes, nothing is removed. That includes the case where the store cannot be read at all —
// "we could not check" is not "there is nothing to lose".

using System;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AccountRemovalUnsentDraftsTests
{
    private static readonly Guid AccountId = Guid.Parse("4a4a4a4a-4a4a-4a4a-4a4a-4a4a4a4a4a4a");

    private static AccountModel Account() => new()
    {
        Id = AccountId, Username = "me@example.com", AccountName = "Work",
    };

    private static MailMessageSummary Draft(bool pending = true, string? refused = null) => new()
    {
        MessageId  = "local-1",
        AccountId  = AccountId,
        FolderName = "Drafts",
        Subject    = "Airport thoughts",
        IsPendingUpload  = pending,
        SendFailedReason = refused,
    };

    private static (AccountManagerViewModel vm, StubLocalStoreService store) MakeVm()
    {
        var store = new StubLocalStoreService();
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), store, new StubConfigService(),
            new StubFeatureGate(), new ProviderCatalog(),
            contactSync: new StubContactSyncService(), graphCalendarSync: new StubGraphCalendarSyncService());
        vm.Accounts.Add(Account());
        vm.SelectedAccount = vm.Accounts[0];
        return (vm, store);
    }

    [Fact]
    public async Task AnAccountHoldingAnUnsentDraft_AsksBeforeDestroyingIt()
    {
        var (vm, store) = MakeVm();
        store.SeededSummaries[(AccountId, "Drafts")] = [Draft()];
        string? asked = null;
        vm.ConfirmUnsentMailLoss = m => { asked = m; return false; };

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Contains("1 draft", asked, StringComparison.Ordinal);
        // Declined, so the account is still there.
        Assert.Single(vm.Accounts);
    }

    [Fact]
    public async Task ARefusedDraftCountsToo()
    {
        var (vm, store) = MakeVm();
        store.SeededSummaries[(AccountId, "Drafts")] = [Draft(pending: true, refused: "mailbox does not exist")];
        var asked = false;
        vm.ConfirmUnsentMailLoss = _ => { asked = true; return false; };

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        // It is the case that most needs saying: nothing will ever upload it, so the local copy is
        // the only copy there will ever be.
        Assert.True(asked);
    }

    [Fact]
    public async Task AnAccountWithNothingHeldLocally_IsRemovedWithoutAsking()
    {
        var (vm, _) = MakeVm();
        var asked = false;
        vm.ConfirmUnsentMailLoss = _ => { asked = true; return true; };

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.Empty(vm.Accounts);
    }

    [Fact]
    public async Task AStoreThatWillNotAnswer_AsksAnyway()
    {
        var (vm, store) = MakeVm();
        store.CountUnsentMailFailure = new InvalidOperationException("no store");
        string? asked = null;
        vm.ConfirmUnsentMailLoss = m => { asked = m; return false; };

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        // "We could not check" is not "there is nothing to lose". Skipping the prompt on a read
        // failure fails OPEN on the one step that exists to stop unrecoverable loss.
        Assert.NotNull(asked);
        Assert.Contains("could not check", asked, StringComparison.Ordinal);
        Assert.Single(vm.Accounts);
    }

    [Fact]
    public async Task WithNoConfirmationWired_NothingIsRemoved()
    {
        var (vm, store) = MakeVm();
        store.SeededSummaries[(AccountId, "Drafts")] = [Draft()];

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        // Fails closed. The shipped View always wires the callback; a build that does not cannot
        // obtain the yes, and must not proceed as though it had.
        Assert.Single(vm.Accounts);
    }
}
