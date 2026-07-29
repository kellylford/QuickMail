// Address book account filter: the "Filter" button next to the search box narrows the
// contact list to one account, to the local address book, or back to all accounts.
//
// The rules the tests below pin down:
//   - "All accounts" is the default, and it shows everything.
//   - The menu always offers All accounts and Local address book, plus one entry per
//     configured account. Accounts we cannot name are not offered at all; their contacts
//     stay reachable under All accounts.
//   - The list collapses duplicate addresses to one row, so the filter matches on every
//     account that contributed a copy — not just the surviving row's own owner.
//   - The account filter and the search box compose: both must match.
//   - The active filter survives a reload (sync, add, edit) and falls back to
//     All accounts when its account disappears.
//   - Adding a contact the active filter would hide resets the filter, with one message.
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
    public async Task UnnameableAccounts_AreNotOffered_AndTheirContactsStayUnderAllAccounts()
    {
        // Only accounts we have a name for become menu entries. An account we cannot name
        // would read as a second, indistinguishable "Synced contact" row — unusable by ear.
        // Its contacts are still reachable under "All accounts".
        var (contacts, dir) = MakeContacts();
        var known   = Guid.NewGuid();
        var orphan  = Guid.NewGuid();
        try
        {
            await contacts.ReplaceSyncedContactsAsync(known, ContactSource.Microsoft,
                [new ContactModel { SourceId = "m1", DisplayName = "Known Person", EmailAddress = "known@x.test" }]);
            await contacts.ReplaceSyncedContactsAsync(orphan, ContactSource.Google,
                [new ContactModel { SourceId = "g1", DisplayName = "Orphan Person", EmailAddress = "orphan@x.test" }]);
            var vm = new AddressBookViewModel(contacts, null,
                new FakeAccountService(new AccountModel { Id = known, AccountName = "Work" }));

            await vm.LoadAsync();

            Assert.Equal(
                ["All accounts", "Local address book", "Work"],
                vm.AccountFilterOptions.Select(o => o.Name).ToArray());
            Assert.Contains(vm.FilteredContacts, c => c.DisplayName == "Orphan Person");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AccountFilter_ShowsAPersonWhoseAddressIsAlsoSavedLocally()
    {
        // The list collapses duplicate addresses to one row, preferring the local copy. The
        // filter must still answer "does this account have this person", not "is this row's
        // owning account this one" — otherwise syncing an address you already saved locally
        // makes that person vanish from their own account's filter.
        var (contacts, dir) = MakeContacts();
        var work = Guid.NewGuid();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Alice", EmailAddress = "alice@corp.test" });
            await contacts.ReplaceSyncedContactsAsync(work, ContactSource.Microsoft,
                [new ContactModel { SourceId = "w1", DisplayName = "Alice Anderson", EmailAddress = "alice@corp.test" }]);
            var vm = new AddressBookViewModel(contacts, null,
                new FakeAccountService(new AccountModel { Id = work, AccountName = "Work" }));
            await vm.LoadAsync();

            // One row, the local copy, as before.
            Assert.Equal(["Alice"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());

            vm.SelectAccountFilter(Option(vm, "Work"));
            Assert.Equal(["Alice"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());

            // And she is still in the local address book too.
            vm.SelectAccountFilter(Option(vm, "Local address book"));
            Assert.Equal(["Alice"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task APersonSyncedFromTwoAccounts_ShowsUnderBoth()
    {
        var (contacts, dir) = MakeContacts();
        var work = Guid.NewGuid();
        var home = Guid.NewGuid();
        try
        {
            await contacts.ReplaceSyncedContactsAsync(work, ContactSource.Microsoft,
                [new ContactModel { SourceId = "w1", DisplayName = "Bob", EmailAddress = "bob@x.test" }]);
            await contacts.ReplaceSyncedContactsAsync(home, ContactSource.Google,
                [new ContactModel { SourceId = "g1", DisplayName = "Bob", EmailAddress = "bob@x.test" }]);
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(
                new AccountModel { Id = work, AccountName = "Work" },
                new AccountModel { Id = home, AccountName = "Home" }));
            await vm.LoadAsync();

            vm.SelectAccountFilter(Option(vm, "Work"));
            Assert.Single(vm.FilteredContacts);

            vm.SelectAccountFilter(Option(vm, "Home"));
            Assert.Single(vm.FilteredContacts);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AddingAContactUnderAnAccountFilter_ResetsToAllAccounts_AndSaysSo()
    {
        // A new contact is local, so an account filter set earlier would hide it. The user
        // must not be told "added" and then see nothing appear.
        var (contacts, dir) = MakeContacts();
        var work = Guid.NewGuid();
        try
        {
            await contacts.ReplaceSyncedContactsAsync(work, ContactSource.Microsoft,
                [new ContactModel { SourceId = "w1", DisplayName = "Work Person", EmailAddress = "work@x.test" }]);
            var vm = new AddressBookViewModel(contacts, null,
                new FakeAccountService(new AccountModel { Id = work, AccountName = "Work" }));
            await vm.LoadAsync();
            vm.SelectAccountFilter(Option(vm, "Work"));

            var heard = new List<(string Text, AnnouncementCategory Category)>();
            vm.AnnouncementRequested += (text, category) => heard.Add((text, category));

            vm.BeginAddContactCommand.Execute(null);
            vm.EditName  = "New Person";
            vm.EditEmail = "new@x.test";
            await vm.SaveContactCommand.ExecuteAsync(null);

            Assert.Equal(AccountFilterKind.All, vm.SelectedAccountFilter.Kind);
            Assert.Contains(vm.FilteredContacts, c => c.DisplayName == "New Person");
            Assert.Same(vm.FilteredContacts.Single(c => c.DisplayName == "New Person"), vm.SelectedContact);
            // One message, not a filter announcement followed by an add announcement.
            Assert.Equal(
                ("New Person added. Filter reset to all accounts.", AnnouncementCategory.Result),
                heard.Last());
            Assert.DoesNotContain(heard, h => h.Text.StartsWith("All accounts,", StringComparison.Ordinal));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AddingAContactVisibleUnderTheActiveFilter_LeavesTheFilterAlone()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var vm = new AddressBookViewModel(contacts);
            await vm.LoadAsync();
            vm.SelectAccountFilter(Option(vm, "Local address book"));

            var heard = new List<string>();
            vm.AnnouncementRequested += (text, _) => heard.Add(text);

            vm.BeginAddContactCommand.Execute(null);
            vm.EditName  = "New Person";
            vm.EditEmail = "new@x.test";
            await vm.SaveContactCommand.ExecuteAsync(null);

            Assert.Equal(AccountFilterKind.Local, vm.SelectedAccountFilter.Kind);
            Assert.Equal("New Person added", heard.Last());
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
    public async Task ButtonAccessibleName_ReportsTheActiveFilter_Verbatim()
    {
        // Plain text only — the access-key underscore is added by the View, so the
        // accessible name never carries markup even when the account name has an underscore.
        var (contacts, dir) = MakeContacts();
        var acct = Guid.NewGuid();
        try
        {
            var vm = new AddressBookViewModel(contacts, null, new FakeAccountService(new AccountModel { Id = acct, AccountName = "work_mail" }));
            await vm.LoadAsync();

            Assert.Equal("Filter: All accounts", vm.AccountFilterButtonName);

            vm.SelectAccountFilter(Option(vm, "work_mail"));

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

/// <summary>
/// The View-side converter that turns the ViewModel's plain filter name into a button label
/// carrying an access key. The doubling rule matters: an account whose name contains an
/// underscore would otherwise swallow it and claim a second access key.
/// </summary>
public class AccessKeyLabelConverterTests
{
    [Theory]
    [InlineData("All accounts", "_Filter: All accounts")]
    [InlineData("work_mail",    "_Filter: work__mail")]
    [InlineData("a_b_c",        "_Filter: a__b__c")]
    [InlineData("",             "_Filter: ")]
    public void Convert_AddsTheAccessKey_AndDoublesUnderscoresInTheValue(string value, string expected)
    {
        var actual = QuickMail.Views.AccessKeyLabelConverter.Instance.Convert(
            value, typeof(string), "_Filter: {0}", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Convert_WithNoTemplate_ReturnsTheEscapedValue()
    {
        var actual = QuickMail.Views.AccessKeyLabelConverter.Instance.Convert(
            "work_mail", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("work__mail", actual);
    }
}
