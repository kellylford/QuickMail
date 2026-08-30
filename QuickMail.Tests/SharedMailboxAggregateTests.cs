using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pins the shared-mailbox aggregate rule (#31, spec §5.1/§5.4 — the "item-2 trap"): a shared mailbox
/// is excluded from the All-* aggregates via the <c>IsShared</c> predicate, NOT via
/// <c>ExcludeFromAllMail</c> — so it stays out of All Inboxes / All Mail yet is still covered by the
/// #456 sweep (whose filter is the folder flag, which shared folders never set). Also pins that a
/// shared account gets its own top-level tree node with the "shared mailbox" accessible name.
/// </summary>
public class SharedMailboxAggregateTests
{
    private static readonly Guid NormalId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private static readonly Guid ParentId = Guid.Parse("a2222222-2222-2222-2222-222222222222");
    private static readonly Guid SharedId = Guid.Parse("a3333333-3333-3333-3333-333333333333");

    private static AccountModel Normal(Guid id, string label) => new()
    {
        Id = id, AccountName = label, Username = label.ToLowerInvariant() + "@example.com",
        AuthType = AuthType.OAuth2Microsoft,
    };

    private static AccountModel Shared(Guid id, Guid parent, string label) => new()
    {
        Id = id, AccountName = label, Username = label.ToLowerInvariant() + "@example.com",
        AuthType = AuthType.OAuth2Microsoft, BackendKind = BackendKind.MicrosoftGraph,
        IsShared = true, ParentAccountId = parent, SharedAddress = label.ToLowerInvariant() + "@example.com",
    };

    private static MailFolderModel Inbox(Guid accountId) => new()
    {
        AccountId = accountId, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox,
    };

    private static async Task<MainViewModel> MakeVmAsync(
        IEnumerable<AccountModel> accounts, Dictionary<Guid, List<MailFolderModel>> folders)
    {
        var vm = new MainViewModel(
            new FolderedMailService(folders, new()), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        foreach (var a in accounts) vm.Accounts.Add(a);
        await vm.ConnectAllAccountsAsync();
        return vm;
    }

    [Fact]
    public async Task SharedAccount_ExcludedFromAllInboxes_ButStillSwept()
    {
        var sharedInbox = Inbox(SharedId);
        var vm = await MakeVmAsync(
            [Normal(NormalId, "Work"), Shared(SharedId, ParentId, "Support")],
            new() { [NormalId] = [Inbox(NormalId)], [SharedId] = [sharedInbox] });

        // Ensure the shared account's folder cache is populated. From PR 2 a Graph-parent shared account
        // is connected by ConnectAllAccountsAsync (above), which already caches its folders; this refresh
        // is belt-and-suspenders. With the cache populated, the exclusion can only come from
        // `if (account.IsShared) continue;` — the line this test exists to pin — not the TryGetValue guard.
        await vm.RefreshFolderListAsync(SharedId);
        Assert.NotEmpty(vm.FolderScopedAggregateSources(MainViewModel.AllInboxesFolder.FullName)); // sanity: cache is live

        var sources = vm.FolderScopedAggregateSources(MainViewModel.AllInboxesFolder.FullName).ToList();

        Assert.Contains(sources, s => s.Account.Id == NormalId);        // normal account contributes
        Assert.DoesNotContain(sources, s => s.Account.Id == SharedId);  // shared excluded despite cached folders

        // The exclusion is by IsShared, not by the folder flag the sweep filters on — so the shared
        // Inbox is NOT marked ExcludeFromAllMail and the #456 sweep still covers it.
        Assert.False(sharedInbox.ExcludeFromAllMail);
        Assert.True(vm.IsSharedAccountId(SharedId));
        Assert.False(vm.IsSharedAccountId(NormalId));
    }

    [Fact]
    public async Task ResolveAccountById_TracksTheLiveAccountList() // #31 PR 2 — thread-safe token-resolver snapshot
    {
        // The shared-mailbox token resolver (App wires OAuthService.ResolveAccount to this) reads a
        // snapshot rebuilt on every add/remove, so it never enumerates the UI-thread-owned collection off
        // a background sweep thread. Pin that the snapshot actually tracks the list.
        var vm = await MakeVmAsync(
            [Normal(NormalId, "Work"), Shared(SharedId, ParentId, "Support")],
            new() { [NormalId] = [Inbox(NormalId)], [SharedId] = [Inbox(SharedId)] });

        Assert.Equal(NormalId, vm.ResolveAccountById(NormalId)?.Id);
        Assert.Equal(SharedId, vm.ResolveAccountById(SharedId)?.Id);
        Assert.Null(vm.ResolveAccountById(Guid.NewGuid()));   // unknown id → null, not a throw

        // In-place removal updates the snapshot — the resolver must not resolve a removed account.
        vm.Accounts.Remove(vm.Accounts.First(a => a.Id == NormalId));
        Assert.Null(vm.ResolveAccountById(NormalId));
    }

    [Fact]
    public async Task MainWindowDelete_OfParent_CascadesSharedChildren()
    {
        // The main-window account context menu delete (MainViewModel) must cascade a parent's shared
        // mailboxes exactly as the Account Manager does — otherwise the shared account is orphaned with a
        // ParentAccountId pointing at nothing (PR review finding 1). Shared child's parent = the Work account.
        var vm = await MakeVmAsync(
            [Normal(NormalId, "Work"), Shared(SharedId, NormalId, "Support")],
            new() { [NormalId] = [Inbox(NormalId)] });
        vm.ConfirmationRequested = (_, _, _) => true;

        await vm.DeleteAccountCommand.ExecuteAsync(vm.Accounts.First(a => a.Id == NormalId));

        Assert.DoesNotContain(vm.Accounts, a => a.Id == NormalId);   // parent removed
        Assert.DoesNotContain(vm.Accounts, a => a.Id == SharedId);   // shared child cascaded away — no orphan
    }

    [Fact]
    public async Task MainWindowDelete_AsksTheViewToStartOnNo()
    {
        // This prompt destroys drafts that exist nowhere else, and it started on Yes -- so Enter on
        // it destroyed them, while the release note claimed it no longer did. The Account Manager's
        // twin was fixed and this one, the other route to the same purge, was not (#637).
        var vm = await MakeVmAsync(
            [Normal(NormalId, "Work")],
            new() { [NormalId] = [Inbox(NormalId)] });
        bool? startOnNo = null;
        vm.ConfirmationRequested = (_, _, onNo) => { startOnNo = onNo; return false; };

        await vm.DeleteAccountCommand.ExecuteAsync(vm.Accounts.First(a => a.Id == NormalId));

        Assert.True(startOnNo);
    }

    [Fact]
    public async Task MainWindowDelete_FailsClosed_WhenConfirmationDeclined()
    {
        var vm = await MakeVmAsync(
            [Normal(NormalId, "Work"), Shared(SharedId, NormalId, "Support")],
            new() { [NormalId] = [Inbox(NormalId)] });
        vm.ConfirmationRequested = (_, _, _) => false;   // user declines

        await vm.DeleteAccountCommand.ExecuteAsync(vm.Accounts.First(a => a.Id == NormalId));

        Assert.Contains(vm.Accounts, a => a.Id == NormalId);   // nothing removed
        Assert.Contains(vm.Accounts, a => a.Id == SharedId);
    }

    [Fact]
    public async Task SharedAccount_HasOwnTopLevelTreeNode_WithSharedAccessibleName()
    {
        // No folders are seeded for the shared account here, so even though PR 2 now connects it,
        // GetFoldersAsync returns nothing and it renders as a placeholder top-level node — which must
        // still carry the account id and the shared qualifier.
        var vm = await MakeVmAsync([Shared(SharedId, ParentId, "Support")], new());

        var node = vm.FolderTree.FirstOrDefault(n => n.IsHeader && n.AccountId == SharedId);
        Assert.NotNull(node);
        Assert.True(node!.IsSharedAccount);
        Assert.Equal("Support, shared mailbox", node.AutomationName);
    }
}
