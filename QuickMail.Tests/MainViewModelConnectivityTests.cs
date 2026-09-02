using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// How the main view model feeds and reacts to <see cref="IConnectivityService"/> (#637): every
/// connect outcome is reported, a launch with no network does not spend three minutes on timed
/// attempts, the network coming back reconnects without an F5, and an account that goes
/// unreachable leaves the connected set so sweeps and watchers stop being aimed at it.
/// </summary>
public class MainViewModelConnectivityTests
{
    private sealed class CountingMailService : StubImapMailServiceBase
    {
        public int Connects;
        public override Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Connects);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public StubConnectivityService Connectivity { get; } = new();
        public CountingMailService Mail { get; } = new();
        public AccountModel Account { get; } = new()
        {
            AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft,
        };
        public MainViewModel Vm { get; }

        public Fixture()
        {
            Vm = new MainViewModel(
                Mail, new StubAccountService(), new StubCredentialService(),
                new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
                new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
                new StubRuleService(), new StubSmtpService(),
                connectivity: Connectivity);
            Vm.Accounts.Add(Account);
        }

        public async Task WaitForConnectsAsync(int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref Mail.Connects) < expected && DateTime.UtcNow < deadline)
                await Task.Delay(25);
        }
    }

    [Fact]
    public async Task NoNetwork_ConnectGivesUpAtOnceAndReportsIt()
    {
        var f = new Fixture();
        f.Connectivity.IsNetworkAvailable = false;
        var started = DateTime.UtcNow;

        await f.Vm.ConnectAllAccountsAsync();

        Assert.Equal(0, f.Mail.Connects);
        Assert.False(f.Account.IsConnected);
        Assert.False(f.Vm.IsAccountReady(f.Account.Id));
        Assert.Contains((f.Account.Id, "initial-connect", false), f.Connectivity.Notes);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ASuccessfulConnectReportsTheAccountReachable()
    {
        var f = new Fixture();

        await f.Vm.ConnectAllAccountsAsync();

        Assert.Equal(1, f.Mail.Connects);
        Assert.True(f.Account.IsConnected);
        Assert.Contains((f.Account.Id, "initial-connect", true), f.Connectivity.Notes);
    }

    [Fact]
    public async Task TheNetworkReturningReconnectsWithoutAnF5()
    {
        var f = new Fixture();
        f.Connectivity.IsNetworkAvailable = false;
        await f.Vm.ConnectAllAccountsAsync();
        Assert.Equal(0, f.Mail.Connects);

        f.Connectivity.RaiseNetworkAvailabilityChanged(true);
        await f.WaitForConnectsAsync(1);

        Assert.Equal(1, f.Mail.Connects);
        // The reconnect reports its outcome under its own source tag.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!f.Connectivity.Notes.Contains((f.Account.Id, "network-returned", true)) && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Contains((f.Account.Id, "network-returned", true), f.Connectivity.Notes);
    }

    [Fact]
    public async Task AnAccountGoingUnreachableLeavesTheConnectedSet()
    {
        var f = new Fixture();
        await f.Vm.ConnectAllAccountsAsync();
        Assert.True(f.Vm.IsAccountReady(f.Account.Id));

        f.Connectivity.RaiseAccountOnlineChanged(f.Account.Id, false);

        Assert.False(f.Vm.IsAccountReady(f.Account.Id));
        Assert.False(f.Account.IsConnected);
    }

    [Fact]
    public async Task RemovingAnAccountForgetsIt()
    {
        var f = new Fixture();
        f.Connectivity.SetAccount(f.Account.Id, false);
        f.Vm.ConfirmationRequested = (_, _) => true;

        await f.Vm.DeleteAccountCommand.ExecuteAsync(f.Account);

        Assert.Equal(AccountConnectivity.Unknown, f.Connectivity.AccountState(f.Account.Id));
    }

    [Fact]
    public void DisposeUnsubscribes()
    {
        var f = new Fixture();
        Assert.Equal(1, f.Connectivity.OnlineChangedSubscribers);

        f.Vm.Dispose();

        Assert.Equal(0, f.Connectivity.OnlineChangedSubscribers);
    }
}
