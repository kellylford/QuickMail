using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Locks the fix for the long-running "adding an account disconnects all the others" report.
///
/// Nothing ever disconnected. <c>AccountModel.IsConnected</c> and <c>TotalUnread</c> are runtime
/// state that is deliberately not persisted to accounts.json, so reloading the account list built
/// fresh objects that all defaulted to disconnected, and replacing the Accounts collection made
/// every account read as disconnected at once. <c>RefreshAccountList</c> then skips reconnecting
/// accounts that are already healthy, so nothing set the flag back — the false status stuck until
/// the next app restart.
///
/// This was invisible to the connection instrumentation because the state is lost by object
/// replacement rather than by assignment, so no write to IsConnected is ever observed. That is why
/// the test lives at the ViewModel level: the guarantee that matters is "a reload does not change
/// what the account list reports", and only a reload can violate it.
/// </summary>
public class AccountListReloadStatusTests
{
    private sealed class FixedAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public FixedAccountService(List<AccountModel> accounts) => _accounts = accounts;

        // Mirrors the real service: every load returns FRESH objects parsed from disk, so runtime
        // state cannot survive inside them. A stub that returned the same instances would hide the
        // entire bug.
        public List<AccountModel> LoadAccounts() =>
            _accounts.Select(a => new AccountModel
            {
                Id          = a.Id,
                AccountName = a.AccountName,
                Username    = a.Username,
                ImapHost    = a.ImapHost,
            }).ToList();

        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MainViewModel CreateVm(IAccountService accountService) =>
        new(new StubImapMailService(), accountService, new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

    private static AccountModel Account(string name) =>
        new() { Id = Guid.NewGuid(), AccountName = name, Username = $"{name}@example.com", ImapHost = "h" };

    [Fact]
    public void ReloadingTheAccountList_KeepsConnectedAccountsConnected()
    {
        var stored = new List<AccountModel> { Account("Kelly"), Account("Idea") };
        var vm = CreateVm(new FixedAccountService(stored));

        vm.LoadAccountList();
        foreach (var a in vm.Accounts) { a.IsConnected = true; a.TotalUnread = 7; }

        // What adding an account does: reload the list from disk.
        vm.LoadAccountList();

        Assert.Equal(2, vm.Accounts.Count);
        Assert.All(vm.Accounts, a => Assert.True(
            a.IsConnected,
            $"'{a.AccountLabel}' reported disconnected after a reload even though nothing disconnected."));
        Assert.All(vm.Accounts, a => Assert.Equal(7, a.TotalUnread));
    }

    [Fact]
    public void AddingAnAccount_DoesNotDisturbTheExistingOnes()
    {
        // The exact reported scenario: several healthy accounts, then a new one is added.
        var stored = new List<AccountModel> { Account("Kelly"), Account("Idea"), Account("Google") };
        var accountService = new FixedAccountService(stored);
        var vm = CreateVm(accountService);

        vm.LoadAccountList();
        foreach (var a in vm.Accounts) a.IsConnected = true;

        stored.Add(Account("Yahoo"));
        vm.LoadAccountList();

        Assert.Equal(4, vm.Accounts.Count);

        var existing = vm.Accounts.Where(a => a.AccountLabel != "Yahoo").ToList();
        Assert.Equal(3, existing.Count);
        Assert.All(existing, a => Assert.True(
            a.IsConnected, $"adding an account made '{a.AccountLabel}' report disconnected."));

        // The genuinely new account has no prior state, so it correctly starts disconnected and is
        // left for the reconnect pass to bring up.
        Assert.False(vm.Accounts.Single(a => a.AccountLabel == "Yahoo").IsConnected);
    }

    [Fact]
    public void ReloadingKeepsDisconnectedAccountsDisconnected()
    {
        // The carry-over must preserve state, not assume the best. An account that really was
        // disconnected must not be laundered into "connected" by a reload.
        var stored = new List<AccountModel> { Account("Kelly"), Account("Idea") };
        var vm = CreateVm(new FixedAccountService(stored));

        vm.LoadAccountList();
        vm.Accounts[0].IsConnected = true;
        vm.Accounts[1].IsConnected = false;

        vm.LoadAccountList();

        Assert.True(vm.Accounts.Single(a => a.AccountLabel == "Kelly").IsConnected);
        Assert.False(vm.Accounts.Single(a => a.AccountLabel == "Idea").IsConnected);
    }

    [Fact]
    public void RemovedAccountsSimplyDisappear()
    {
        var stored = new List<AccountModel> { Account("Kelly"), Account("Idea") };
        var vm = CreateVm(new FixedAccountService(stored));

        vm.LoadAccountList();
        foreach (var a in vm.Accounts) a.IsConnected = true;

        stored.RemoveAt(1);
        vm.LoadAccountList();

        var remaining = Assert.Single(vm.Accounts);
        Assert.Equal("Kelly", remaining.AccountLabel);
        Assert.True(remaining.IsConnected);
    }

    [Fact]
    public void FirstLoadIsUnaffected()
    {
        // Startup passes a preloaded list into an empty VM; there is no prior state to carry.
        var vm = CreateVm(new FixedAccountService(new List<AccountModel>()));

        vm.LoadAccountList(new List<AccountModel> { Account("Kelly") });

        var only = Assert.Single(vm.Accounts);
        Assert.False(only.IsConnected);
    }
}
