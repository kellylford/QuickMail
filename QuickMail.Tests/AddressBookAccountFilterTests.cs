// Address book account filter: the "Filter" button next to the search box narrows the
// contact list to one account, to the local address book, or back to all accounts.
//
// The rules the tests below pin down:
//   - "All accounts" is the default, and it shows everything.
//   - The menu always offers All accounts and Local address book, plus one entry per
//     account — configured accounts when an IAccountService is supplied, and accounts
//     inferred from the contacts themselves when it is not (the address book opened
//     from Compose has no account service).
//   - The account filter and the search box compose: both must match.
//   - The active filter survives a reload (sync, add, edit) and falls back to
//     All accounts when its account disappears.
//   - Choosing a filter announces the result count as AnnouncementCategory.Result.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AddressBookAccountFilterTests
{
    private sealed class FakeAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public FakeAccountService(params AccountModel[] accounts) => _accounts = accounts.ToList();
        public List<AccountModel> LoadAccounts() => _accounts;
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static (ContactService svc, string dir) MakeContacts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QM-AddrFilter-{Guid.NewGuid():N}");
        return (new ContactService(new ProfileContext(dir)), dir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static AccountFilterOption Option(AddressBookViewModel vm, string name) =>
        vm.AccountFilterOptions.Single(o => o.Name == name);

    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultFilter_IsAllAccounts_AndShowsEveryContact()
    {
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Local Person", EmailAddress = "local@x.test" });
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Google,
                [new ContactModel { SourceId = "g1", DisplayName = "Synced Person", EmailAddress = "synced@x.test" }]);
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "Gmail" }));

            await vm.LoadAsync();

            Assert.Equal(AccountFilterKind.All, vm.SelectedAccountFilter.Kind);
            Assert.Equal("All accounts", vm.SelectedAccountFilter.Name);
            Assert.Equal(2, vm.FilteredContacts.Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Options_AreAllAccountsThenLocalThenEachAccount()
    {
        var (contacts, dir) = MakeContacts();
        var work = Guid.NewGuid();
        var home = Guid.NewGuid();
        try
        {
            var accounts = new FakeAccountService(
                new AccountModel { Id = work, AccountName = "Work" },
                new AccountModel { Id = home, AccountName = "Home" });
            var vm = new AddressBookViewModel(contacts, null, accounts);

            await vm.LoadAsync();

            Assert.Equal(
                ["All accounts", "Local address book", "Home", "Work"],
                vm.AccountFilterOptions.Select(o => o.Name).ToArray());
            Assert.True(vm.AccountFilterOptions[0].IsSelected);
            Assert.All(vm.AccountFilterOptions.Skip(1), o => Assert.False(o.IsSelected));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Options_AreInferredFromContacts_WhenNoAccountServiceIsSupplied()
    {
        // The address book opened from Compose is constructed without an IAccountService.
        // The accounts that own contacts are still offered so the filter is usable there.
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft,
                [new ContactModel { SourceId = "m1", DisplayName = "Synced Person", EmailAddress = "synced@x.test" }]);
            var vm = new AddressBookViewModel(contacts);

            await vm.LoadAsync();

            var inferred = vm.AccountFilterOptions.Single(o => o.Kind == AccountFilterKind.Account);
            Assert.Equal(acct, inferred.AccountId);
            Assert.Equal("Synced contact", inferred.Name);   // no account label available
        }
        finally { Cleanup(dir); }
    }

    // ── Filtering ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectingAnAccount_ShowsOnlyThatAccountsContacts()
    {
        var (contacts, dir) = MakeContacts();
        var work = Guid.NewGuid();
        var home = Guid.NewGuid();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Local Person", EmailAddress = "local@x.test" });
            await contacts.ReplaceSyncedContactsAsync(work, ContactSource.Microsoft,
                [new ContactModel { SourceId = "w1", DisplayName = "Work Person", EmailAddress = "work@x.test" }]);
            await contacts.ReplaceSyncedContactsAsync(home, ContactSource.Google,
                [new ContactModel { SourceId = "h1", DisplayName = "Home Person", EmailAddress = "home@x.test" }]);
            var accounts = new FakeAccountService(
                new AccountModel { Id = work, AccountName = "Work" },
                new AccountModel { Id = home, AccountName = "Home" });
            var vm = new AddressBookViewModel(contacts, null, accounts);
            await vm.LoadAsync();

            vm.SelectAccountFilter(Option(vm, "Work"));

            Assert.Equal(["Work Person"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SelectingLocalAddressBook_ExcludesSyncedContacts()
    {
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Local Person", EmailAddress = "local@x.test" });
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Google,
                [new ContactModel { SourceId = "g1", DisplayName = "Synced Person", EmailAddress = "synced@x.test" }]);
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "Gmail" }));
            await vm.LoadAsync();

            vm.SelectAccountFilter(Option(vm, "Local address book"));

            Assert.Equal(["Local Person"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AccountFilterAndSearchText_BothApply()
    {
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Alice Local", EmailAddress = "alice@local.test" });
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Google,
            [
                new ContactModel { SourceId = "g1", DisplayName = "Alice Synced", EmailAddress = "alice@synced.test" },
                new ContactModel { SourceId = "g2", DisplayName = "Bob Synced",   EmailAddress = "bob@synced.test" },
            ]);
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "Gmail" }));
            await vm.LoadAsync();

            vm.SelectAccountFilter(Option(vm, "Gmail"));
            vm.SearchText = "alice";

            Assert.Equal(["Alice Synced"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task FilteringOutTheSelectedContact_ClearsTheSelection()
    {
        // Edit and Delete act on SelectedContact; leaving a filtered-out row selected
        // would let them act on a contact the user can no longer see.
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Local Person", EmailAddress = "local@x.test" });
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Google,
                [new ContactModel { SourceId = "g1", DisplayName = "Synced Person", EmailAddress = "synced@x.test" }]);
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "Gmail" }));
            await vm.LoadAsync();
            vm.SelectedContact = vm.FilteredContacts.Single(c => c.DisplayName == "Local Person");

            vm.SelectAccountFilter(Option(vm, "Gmail"));

            Assert.Null(vm.SelectedContact);
        }
        finally { Cleanup(dir); }
    }

    // ── Check state, label, announcement ─────────────────────────────────────

    [Fact]
    public async Task SelectingAFilter_ChecksExactlyOneOption()
    {
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "Gmail" }));
            await vm.LoadAsync();

            vm.SelectAccountFilter(Option(vm, "Gmail"));

            Assert.Single(vm.AccountFilterOptions, o => o.IsSelected);
            Assert.True(Option(vm, "Gmail").IsSelected);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task ReSelectingTheActiveFilter_LeavesItChecked()
    {
        // Activating a menu item clears its own check mark first (MenuItem toggles
        // IsChecked on click), so the command has to push the value back.
        var (contacts, dir) = MakeContacts();
        try
        {
            var vm = new AddressBookViewModel(contacts);
            await vm.LoadAsync();
            var all = Option(vm, "All accounts");
            all.IsSelected = false;   // stand in for the menu item's own toggle

            vm.SelectAccountFilter(all);

            Assert.True(all.IsSelected);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task ButtonLabel_ReportsTheActiveFilter_AndDoublesUnderscores()
    {
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "work_mail" }));
            await vm.LoadAsync();

            Assert.Equal("_Filter: All accounts", vm.AccountFilterButtonContent);
            Assert.Equal("Filter: All accounts", vm.AccountFilterButtonName);

            vm.SelectAccountFilter(Option(vm, "work_mail"));

            // The underscore in the account name is doubled so it renders literally
            // instead of claiming a second access key.
            Assert.Equal("_Filter: work__mail", vm.AccountFilterButtonContent);
            Assert.Equal("Filter: work_mail", vm.AccountFilterButtonName);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SelectingAFilter_AnnouncesTheResultCount()
    {
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Google,
            [
                new ContactModel { SourceId = "g1", DisplayName = "One", EmailAddress = "one@x.test" },
                new ContactModel { SourceId = "g2", DisplayName = "Two", EmailAddress = "two@x.test" },
            ]);
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "Gmail" }));
            await vm.LoadAsync();
            var heard = new List<(string Text, AnnouncementCategory Category)>();
            vm.AnnouncementRequested += (text, category) => heard.Add((text, category));

            vm.SelectAccountFilter(Option(vm, "Gmail"));
            vm.SelectAccountFilter(Option(vm, "Local address book"));

            Assert.Equal(("Gmail, 2 contacts", AnnouncementCategory.Result), heard[0]);
            Assert.Equal(("Local address book, 0 contacts", AnnouncementCategory.Result), heard[1]);
        }
        finally { Cleanup(dir); }
    }

    // ── Reload behavior ──────────────────────────────────────────────────────

    [Fact]
    public async Task ActiveFilter_SurvivesAReload()
    {
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Google,
                [new ContactModel { SourceId = "g1", DisplayName = "Synced Person", EmailAddress = "synced@x.test" }]);
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "Gmail" }));
            await vm.LoadAsync();
            vm.SelectAccountFilter(Option(vm, "Gmail"));

            await vm.LoadAsync();

            Assert.Equal("Gmail", vm.SelectedAccountFilter.Name);
            Assert.Equal(acct, vm.SelectedAccountFilter.AccountId);
            Assert.True(Option(vm, "Gmail").IsSelected);
            Assert.Single(vm.FilteredContacts);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task FilterFallsBackToAllAccounts_WhenItsAccountGoesAway()
    {
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Local Person", EmailAddress = "local@x.test" });
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Google,
                [new ContactModel { SourceId = "g1", DisplayName = "Synced Person", EmailAddress = "synced@x.test" }]);
            var accounts = new FakeAccountService(new AccountModel { Id = acct, AccountName = "Gmail" });
            var vm = new AddressBookViewModel(contacts, null, accounts);
            await vm.LoadAsync();
            vm.SelectAccountFilter(Option(vm, "Gmail"));

            // The account is removed and its synced contacts go with it.
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Google, []);
            accounts.LoadAccounts().Clear();
            await vm.LoadAsync();

            Assert.Equal(AccountFilterKind.All, vm.SelectedAccountFilter.Kind);
            Assert.Equal(["Local Person"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());
        }
        finally { Cleanup(dir); }
    }
}
