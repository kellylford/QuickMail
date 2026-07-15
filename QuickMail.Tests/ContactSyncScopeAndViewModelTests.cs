using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Guards the OAuth scope sets: contact scopes must be their own set, never folded into the default
/// mail scopes (which would force every existing user to re-consent on next connect — spec §13.1).
/// </summary>
public class OAuthContactScopeTests
{
    [Fact]
    public void GraphContactScopes_AreTheTwoReadOnlyContactScopes()
    {
        Assert.Contains("https://graph.microsoft.com/Contacts.Read", OAuthService.GraphContactScopes);
        Assert.Contains("https://graph.microsoft.com/People.Read", OAuthService.GraphContactScopes);
    }

    [Fact]
    public void DefaultMailScopes_DoNotContainContactScopes()
    {
        foreach (var scope in OAuthService.ImapSmtpScopes.Concat(OAuthService.GraphMailScopes).Concat(OAuthService.GraphMailScopesPersonal))
        {
            Assert.DoesNotContain("Contacts.Read", scope);
            Assert.DoesNotContain("People.Read", scope);
        }
    }

    [Fact]
    public void ContactScopes_AreNotRequestedByDefaultScopeSelection()
    {
        // A Graph account's default (mail) scopes must not include the contact scopes.
        var graphAccount = new AccountModel { BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft };
        var defaults = OAuthService.DefaultScopesFor(graphAccount);
        Assert.DoesNotContain(defaults, s => s.Contains("Contacts.Read") || s.Contains("People.Read"));
    }
}

/// <summary>
/// AddressBookViewModel behaviour for synced contacts (issue #256): synced rows are read-only, and
/// the Sync Contacts Now command delegates to the sync service.
/// </summary>
public class AddressBookViewModelSyncTests
{
    private sealed class RecordingSyncService : IContactSyncService
    {
        public int SyncAllCount;
        public bool CanSync(AccountModel account) => true;
        public Task<ContactSyncResult> SyncAccountAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(ContactSyncResult.None);
        public Task<ContactSyncResult> SyncAllAsync(CancellationToken ct = default) { SyncAllCount++; return Task.FromResult(new ContactSyncResult(1, 3, null)); }
        public Task<ContactSyncResult> SyncAllDueAsync(TimeSpan minInterval, CancellationToken ct = default) => SyncAllAsync(ct);
        public Task RemoveAccountContactsAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ContactModel Local()  => new() { Id = 1, DisplayName = "Local",  EmailAddress = "l@x.test", Source = ContactSource.Local };
    private static ContactModel Synced() => new() { Id = 2, DisplayName = "Synced", EmailAddress = "s@x.test", Source = ContactSource.Microsoft, SourceId = "m1" };

    [Fact]
    public void LocalContact_IsEditableAndDeletable()
    {
        var vm = new AddressBookViewModel(new StubContactService()) { SelectedContact = Local() };
        Assert.True(vm.CanEditContact);
        Assert.True(vm.CanDeleteContact);
    }

    [Fact]
    public void SyncedContact_IsNotEditableOrDeletable()
    {
        var vm = new AddressBookViewModel(new StubContactService()) { SelectedContact = Synced() };
        Assert.False(vm.CanEditContact);
        Assert.False(vm.CanDeleteContact);
    }

    [Fact]
    public void CanSyncContacts_ReflectsWhetherSyncServiceIsWired()
    {
        Assert.False(new AddressBookViewModel(new StubContactService()).CanSyncContacts);
        Assert.True(new AddressBookViewModel(new StubContactService(), new RecordingSyncService()).CanSyncContacts);
    }

    [Fact]
    public async Task SyncContactsNowCommand_InvokesSyncService()
    {
        var recorder = new RecordingSyncService();
        var vm = new AddressBookViewModel(new StubContactService(), recorder);
        await vm.SyncContactsNowCommand.ExecuteAsync(null);
        Assert.Equal(1, recorder.SyncAllCount);
    }
}
