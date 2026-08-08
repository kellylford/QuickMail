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

        var sources = vm.FolderScopedAggregateSources(MainViewModel.AllInboxesFolder.FullName).ToList();

        Assert.Contains(sources, s => s.Account.Id == NormalId);        // normal account contributes
        Assert.DoesNotContain(sources, s => s.Account.Id == SharedId);  // shared is excluded from the aggregate

        // The exclusion is by IsShared, not by the folder flag the sweep filters on — so the shared
        // Inbox is NOT marked ExcludeFromAllMail and the #456 sweep still covers it.
        Assert.False(sharedInbox.ExcludeFromAllMail);
        Assert.True(vm.IsSharedAccountId(SharedId));
        Assert.False(vm.IsSharedAccountId(NormalId));
    }

    [Fact]
    public async Task SharedAccount_HasOwnTopLevelTreeNode_WithSharedAccessibleName()
    {
        // PR 1 reality: a shared account has no backend access yet, so it renders as a placeholder
        // top-level node (no folders) — which must still carry the account id and the shared qualifier.
        var vm = await MakeVmAsync([Shared(SharedId, ParentId, "Support")], new());

        var node = vm.FolderTree.FirstOrDefault(n => n.IsHeader && n.AccountId == SharedId);
        Assert.NotNull(node);
        Assert.True(node!.IsSharedAccount);
        Assert.Equal("Support, shared mailbox", node.AutomationName);
    }
}
