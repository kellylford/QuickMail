using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests ContactSyncService orchestration (issue #256): source selection by auth type, the
/// enabled/supported filter in SyncAllAsync, and best-effort failure isolation.
/// </summary>
public class ContactSyncServiceTests
{
    private sealed class FakeSource : IProviderContactSource
    {
        public ContactSource Source { get; init; }
        public Func<AccountModel, IReadOnlyList<ContactModel>> OnFetch = _ => Array.Empty<ContactModel>();
        public int FetchCount;

        public Task<IReadOnlyList<ContactModel>> FetchAsync(AccountModel account, CancellationToken ct = default)
        {
            FetchCount++;
            return Task.FromResult(OnFetch(account));
        }
    }

    private sealed class FakeAccountService : IAccountService
    {
        public List<AccountModel> Accounts = [];
        public List<AccountModel> LoadAccounts() => Accounts;
        public void SaveAccounts(List<AccountModel> accounts) => Accounts = accounts;
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static (ContactService svc, string dir) MakeContacts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QM-ContactSyncSvc-{Guid.NewGuid():N}");
        return (new ContactService(new ProfileContext(dir)), dir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static ContactModel Server(string id, string email) =>
        new() { SourceId = id, DisplayName = id, EmailAddress = email };

    [Fact]
    public async Task SyncAccount_Microsoft_ReplacesContactsAndReportsCount()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft, OnFetch = _ => [Server("m1", "a@x.test"), Server("m2", "b@x.test")] };
            var google = new FakeSource { Source = ContactSource.Google };
            var sut = new ContactSyncService(new FakeAccountService(), contacts, ms, google);
            var account = new AccountModel { AuthType = AuthType.OAuth2Microsoft };

            var result = await sut.SyncAccountAsync(account);

            Assert.Equal(1, result.AccountsSynced);
            Assert.Equal(2, result.ContactsFetched);
            Assert.Null(result.Error);
            Assert.Equal(1, ms.FetchCount);
            Assert.Equal(2, (await contacts.LoadAllContactsAsync()).Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncAccount_PasswordAccount_IsNoOp()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft };
            var google = new FakeSource { Source = ContactSource.Google };
            var sut = new ContactSyncService(new FakeAccountService(), contacts, ms, google);

            var result = await sut.SyncAccountAsync(new AccountModel { AuthType = AuthType.Password });

            Assert.Equal(0, result.AccountsSynced);
            Assert.Equal(0, ms.FetchCount);
            Assert.Equal(0, google.FetchCount);
            Assert.False(sut.CanSync(new AccountModel { AuthType = AuthType.Password }));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncAccount_ICloud_RoutesToICloudSourceByImapHost()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft };
            var google = new FakeSource { Source = ContactSource.Google };
            var icloud = new FakeSource { Source = ContactSource.ICloud, OnFetch = _ => [Server("i1", "a@icloud.test")] };
            var sut = new ContactSyncService(new FakeAccountService(), contacts, ms, google, icloud);
            // An iCloud account is a plain Password/IMAP account — routed by IMAP host, not auth type.
            var account = new AccountModel { AuthType = AuthType.Password, ImapHost = "imap.mail.me.com" };

            Assert.True(sut.CanSync(account));
            var result = await sut.SyncAccountAsync(account);

            Assert.Equal(1, result.AccountsSynced);
            Assert.Equal(1, result.ContactsFetched);
            Assert.Equal(1, icloud.FetchCount);
            Assert.Equal(0, ms.FetchCount);
            var stored = await contacts.LoadAllContactsAsync();
            Assert.Equal(ContactSource.ICloud, Assert.Single(stored).Source);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncAccount_PlainPasswordOtherHost_IsStillNoOp_EvenWithICloudSource()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft };
            var google = new FakeSource { Source = ContactSource.Google };
            var icloud = new FakeSource { Source = ContactSource.ICloud, OnFetch = _ => [Server("i1", "a@icloud.test")] };
            var sut = new ContactSyncService(new FakeAccountService(), contacts, ms, google, icloud);
            var account = new AccountModel { AuthType = AuthType.Password, ImapHost = "imap.example.com" };

            Assert.False(sut.CanSync(account));
            var result = await sut.SyncAccountAsync(account);

            Assert.Equal(0, result.AccountsSynced);
            Assert.Equal(0, icloud.FetchCount);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncAccount_SourceThrows_ReturnsErrorAndDoesNotThrow()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft, OnFetch = _ => throw new InvalidOperationException("boom") };
            var google = new FakeSource { Source = ContactSource.Google };
            var sut = new ContactSyncService(new FakeAccountService(), contacts, ms, google);

            var result = await sut.SyncAccountAsync(new AccountModel { AuthType = AuthType.OAuth2Microsoft });

            Assert.Equal(0, result.AccountsSynced);
            Assert.Contains("boom", result.Error);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncAccount_SignInRequired_ReturnsFriendlyMessage()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft, OnFetch = _ => throw new InteractiveSignInRequiredException("grant lapsed") };
            var sut = new ContactSyncService(new FakeAccountService(), contacts, ms, new FakeSource { Source = ContactSource.Google });

            var result = await sut.SyncAccountAsync(new AccountModel { AuthType = AuthType.OAuth2Microsoft, AccountName = "Work" });

            Assert.Equal(0, result.AccountsSynced);
            Assert.Contains("sign-in needed", result.Error);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncAll_OnlySyncsEnabledAndSupportedAccounts()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft, OnFetch = _ => [Server("m1", "a@x.test")] };
            var google = new FakeSource { Source = ContactSource.Google, OnFetch = _ => [Server("g1", "g@x.test")] };
            var accts = new FakeAccountService
            {
                Accounts =
                [
                    new AccountModel { AuthType = AuthType.OAuth2Microsoft, SyncContacts = true  }, // sync
                    new AccountModel { AuthType = AuthType.OAuth2Google,    SyncContacts = false }, // skip: disabled
                    new AccountModel { AuthType = AuthType.Password,        SyncContacts = true  }, // skip: unsupported
                ],
            };
            var sut = new ContactSyncService(accts, contacts, ms, google);

            var result = await sut.SyncAllAsync();

            Assert.Equal(1, result.AccountsSynced);
            Assert.Equal(1, ms.FetchCount);
            Assert.Equal(0, google.FetchCount);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncAllDue_ThrottlesRepeatCallsWithinInterval()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft, OnFetch = _ => [Server("m1", "a@x.test")] };
            var accts = new FakeAccountService
            {
                Accounts = [new AccountModel { AuthType = AuthType.OAuth2Microsoft, SyncContacts = true }],
            };
            var sut = new ContactSyncService(accts, contacts, ms, new FakeSource { Source = ContactSource.Google });

            await sut.SyncAllDueAsync(TimeSpan.FromHours(12));
            await sut.SyncAllDueAsync(TimeSpan.FromHours(12)); // within window → skipped

            Assert.Equal(1, ms.FetchCount);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SyncAllDue_ZeroInterval_AlwaysRuns()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var ms = new FakeSource { Source = ContactSource.Microsoft, OnFetch = _ => [Server("m1", "a@x.test")] };
            var accts = new FakeAccountService
            {
                Accounts = [new AccountModel { AuthType = AuthType.OAuth2Microsoft, SyncContacts = true }],
            };
            var sut = new ContactSyncService(accts, contacts, ms, new FakeSource { Source = ContactSource.Google });

            await sut.SyncAllDueAsync(TimeSpan.Zero);
            await sut.SyncAllDueAsync(TimeSpan.Zero);

            Assert.Equal(2, ms.FetchCount);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RemoveAccountContacts_PurgesThatAccount()
    {
        var (contacts, dir) = MakeContacts();
        try
        {
            var acct = Guid.NewGuid();
            await contacts.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft, [Server("m1", "a@x.test")]);
            var sut = new ContactSyncService(new FakeAccountService(), contacts,
                new FakeSource { Source = ContactSource.Microsoft }, new FakeSource { Source = ContactSource.Google });

            await sut.RemoveAccountContactsAsync(acct);

            Assert.Empty(await contacts.LoadAllContactsAsync());
        }
        finally { Cleanup(dir); }
    }
}
