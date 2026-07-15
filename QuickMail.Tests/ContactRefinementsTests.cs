using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Address-book contact-list display (issue #256 refinements): the Account column's source label
/// and the composed accessible name, concise vs. field-labeled per the ContactListShowFieldLabels
/// setting.
/// </summary>
public class ContactListDisplayTests
{
    private sealed class OneAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public OneAccountService(params AccountModel[] accounts) => _accounts = accounts.ToList();
        public List<AccountModel> LoadAccounts() => _accounts;
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static (ContactService svc, string dir) MakeContacts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QM-ContactListDisplay-{Guid.NewGuid():N}");
        return (new ContactService(new ProfileContext(dir)), dir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static IConfigService Config(bool showLabels)
    {
        var cfg = new StubConfigService();
        cfg.Save(new ConfigModel { ContactListShowFieldLabels = showLabels });
        return cfg;
    }

    [Fact]
    public async Task LocalContact_ReadsAsLocalAddressBook_Concise()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Kelly Ford", EmailAddress = "kelly@calford.com" });
            var vm = new AddressBookViewModel(contacts, null, new OneAccountService(), Config(showLabels: false));

            await vm.LoadAsync();

            var row = vm.FilteredContacts.Single();
            Assert.Equal("Local address book", row.SourceLabel);
            Assert.Equal("Kelly Ford, kelly@calford.com, Local address book", row.AccessibleName);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncedContact_ReadsAsOwningAccountName()
    {
        var (contacts, dir) = MakeContacts();
        var acctId = Guid.NewGuid();
        try
        {
            await contacts.ReplaceSyncedContactsAsync(acctId, ContactSource.Google,
                [new ContactModel { SourceId = "g1", DisplayName = "Kelly Ford", EmailAddress = "kelly@gmail.test" }]);
            var accounts = new OneAccountService(new AccountModel { Id = acctId, AccountName = "My Gmail" });
            var vm = new AddressBookViewModel(contacts, null, accounts, Config(showLabels: false));

            await vm.LoadAsync();

            var row = vm.FilteredContacts.Single();
            Assert.Equal("My Gmail", row.SourceLabel);
            Assert.Equal("Kelly Ford, kelly@gmail.test, My Gmail", row.AccessibleName);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task ShowFieldLabels_On_UsesLabeledAccessibleName()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Kelly Ford", EmailAddress = "kelly@calford.com" });
            var vm = new AddressBookViewModel(contacts, null, new OneAccountService(), Config(showLabels: true));

            await vm.LoadAsync();

            Assert.Equal("Name Kelly Ford, email kelly@calford.com, account Local address book",
                vm.FilteredContacts.Single().AccessibleName);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task PriorRecipientWithNoName_OmitsNameFromAccessibleName()
    {
        var (contacts, dir) = MakeContacts();
        var acctId = Guid.NewGuid();
        try
        {
            await contacts.ReplaceSyncedContactsAsync(acctId, ContactSource.Microsoft,
                [new ContactModel { SourceId = "p1", DisplayName = "", EmailAddress = "noname@x.test", IsPriorRecipient = true }]);
            var accounts = new OneAccountService(new AccountModel { Id = acctId, AccountName = "Work" });
            var vm = new AddressBookViewModel(contacts, null, accounts, Config(showLabels: false));

            await vm.LoadAsync();

            Assert.Equal("noname@x.test, Work", vm.FilteredContacts.Single().AccessibleName);
        }
        finally { Cleanup(dir); }
    }
}

/// <summary>
/// Add Account contact-sync opt-in (issue #256 refinement): the checkbox flows to the account model,
/// is only offered for OAuth, and the router folds contacts into the initial Google sign-in.
/// </summary>
public class AddAccountContactSyncTests
{
    [Fact]
    public void ShowContactSyncOption_TrueForOAuth_FalseForPassword()
    {
        var vm = new AddAccountViewModel(new StubFeatureGate(), new StubImapMailService(), new StubOAuthService())
        {
            AuthType = AuthType.OAuth2Google,
        };
        Assert.True(vm.ShowContactSyncOption);

        vm.AuthType = AuthType.Password;
        Assert.False(vm.ShowContactSyncOption);
    }

    [Fact]
    public void ToAccountModel_CarriesSyncContacts_OnlyWhenOAuth()
    {
        var vm = new AddAccountViewModel(new StubFeatureGate(), new StubImapMailService(), new StubOAuthService())
        {
            Username = "u@gmail.test",
            AuthType = AuthType.OAuth2Google,
            SyncContacts = true,
        };
        Assert.True(vm.ToAccountModel().SyncContacts);

        // Checkbox state is ignored for a non-OAuth account.
        vm.AuthType = AuthType.Password;
        Assert.False(vm.ToAccountModel().SyncContacts);
    }
}

/// <summary>
/// Router folds contacts into the Google sign-in (one consent), and defers Microsoft's contact
/// consent to after account creation (issue #256 refinement).
/// </summary>
public class OAuthRouterContactSignInTests
{
    private sealed class RecordingGoogle : IGoogleOAuthService
    {
        public bool AuthorizeContactsCalled;
        public Task<string> GetAccessTokenAsync(string username, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<OAuthResult> SignInInteractiveAsync(string loginHint, CancellationToken ct = default) => Task.FromResult(new OAuthResult(string.Empty, loginHint));
        public Task<OAuthResult> AuthorizeContactsAsync(string loginHint, CancellationToken ct = default)
        {
            AuthorizeContactsCalled = true;
            return Task.FromResult(new OAuthResult(string.Empty, loginHint));
        }
        public Task SignOutAsync(string username) => Task.CompletedTask;
    }

    [Fact]
    public async Task GoogleAccount_UsesCombinedContactsConsent()
    {
        var google = new RecordingGoogle();
        var router = new OAuthRouter(new StubOAuthService(), google);

        await router.SignInInteractiveWithContactsAsync(new AccountModel { AuthType = AuthType.OAuth2Google, Username = "u@gmail.test" });

        Assert.True(google.AuthorizeContactsCalled);
    }

    [Fact]
    public async Task MicrosoftAccount_DoesNotTouchGoogle()
    {
        var google = new RecordingGoogle();
        var router = new OAuthRouter(new StubOAuthService(), google);

        await router.SignInInteractiveWithContactsAsync(new AccountModel { AuthType = AuthType.OAuth2Microsoft, Username = "u@work.test" });

        Assert.False(google.AuthorizeContactsCalled);
    }
}
