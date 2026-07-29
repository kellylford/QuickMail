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
/// Regression guards for defects an independent review found in the connection diagnostics.
///
/// The most serious was that "off by default" was not true: the reachability probe opened real
/// authenticated connections regardless of the setting. That matters more than a normal bug,
/// because those connections were added to a host at exactly the moment it was already refusing
/// them — the feature could aggravate the very failure it exists to investigate.
/// </summary>
[Collection("ConnectionDiagnostics")]
public class ConnectionDiagnosticsReviewFixTests : IDisposable
{
    private readonly Guid _account = Guid.NewGuid();

    public ConnectionDiagnosticsReviewFixTests() => ConnectionJournal.ResetForTests();

    public void Dispose()
    {
        ConnectionJournal.ResetForTests();
        GC.SuppressFinalize(this);
    }

    private sealed class CountingProbe : IConnectionProbe
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<ProbeResult> ProbeAccountAsync(Guid accountId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ProbeResult(ProbeOutcome.Reachable, 1, "ok"));
        }
    }

    [Fact]
    public async Task ProbeNeverConnectsWhileDiagnosticsAreOff()
    {
        // The defect: NoteDisconnected started an unbounded verification loop that opened an
        // authenticated IMAP connection every 60s, outside the pool cap, on a default install.
        ConnectionJournal.Enabled = false;

        var backend = new CountingProbe();
        using var probe = new ConnectionTruthProbe(backend, _ => "Kelly");

        probe.NoteDisconnected(_account, "reachability-event");
        await Task.Delay(300);   // the loop fired within milliseconds when this was broken

        Assert.Equal(0, backend.Calls);
        Assert.False(probe.IsMarkedDisconnected(_account));
    }

    [Fact]
    public async Task VerificationStopsWhenDiagnosticsAreSwitchedOffMidFlight()
    {
        ConnectionJournal.Enabled = true;

        var backend = new CountingProbe();
        using var probe = new ConnectionTruthProbe(backend, _ => "Kelly");

        probe.NoteDisconnected(_account, "reachability-event");
        await Task.Delay(200);
        var afterStart = backend.Calls;

        ConnectionJournal.Enabled = false;
        await Task.Delay(200);

        // No further probes once recording is off. (The loop waits a full minute between rounds,
        // so the meaningful assertion is that the count has not grown.)
        Assert.Equal(afterStart, backend.Calls);
    }

    [Fact]
    public void AbandoningARemovedAccountStopsItsVerification()
    {
        ConnectionJournal.Enabled = true;
        using var probe = new ConnectionTruthProbe(new CountingProbe(), _ => "Kelly");

        probe.NoteDisconnected(_account, "reachability-event");
        Assert.True(probe.IsMarkedDisconnected(_account));

        // The account is deleted; it is simply absent from the next reload.
        probe.RetainOnly(new[] { Guid.NewGuid() });

        Assert.False(probe.IsMarkedDisconnected(_account));
        Assert.Contains(ConnectionJournal.Snapshot(), e => e.Phase == "verification-abandoned");
    }

    [Fact]
    public void RetainOnlyKeepsAccountsThatStillExist()
    {
        ConnectionJournal.Enabled = true;
        using var probe = new ConnectionTruthProbe(new CountingProbe(), _ => "Kelly");

        probe.NoteDisconnected(_account, "reachability-event");
        probe.RetainOnly(new[] { _account });

        Assert.True(probe.IsMarkedDisconnected(_account));
    }

    [Fact]
    public void LazyRecordOverloadDoesNotBuildItsDetailWhileOff()
    {
        // Arguments to the eager overload are evaluated before it can short-circuit, and several
        // call sites pass a host census that performs a blocking DNS lookup.
        ConnectionJournal.Enabled = false;

        var built = 0;
        ConnectionJournal.Record(
            ConnectionEventKind.Pool, "Kelly", "h", "reuse-hot",
            () => { built++; return "expensive"; });

        Assert.Equal(0, built);
    }

    [Fact]
    public void LazyRecordOverloadBuildsItsDetailWhileOn()
    {
        ConnectionJournal.Enabled = true;

        ConnectionJournal.Record(
            ConnectionEventKind.Pool, "Kelly", "h", "reuse-hot", () => "expensive");

        Assert.Contains(ConnectionJournal.Snapshot(), e => e.Detail == "expensive");
    }

    [Fact]
    public void ADetailFactoryThatThrowsStillRecordsTheEvent()
    {
        ConnectionJournal.Enabled = true;

        ConnectionJournal.Record(
            ConnectionEventKind.Pool, "Kelly", "h", "reuse-hot",
            () => throw new InvalidOperationException("boom"));

        // Losing the event entirely would be worse than losing its detail.
        Assert.Contains(ConnectionJournal.Snapshot(), e => e.Phase == "reuse-hot");
    }
}

/// <summary>
/// The account-list carry-over must not vouch for connections that no longer match the account.
/// </summary>
public class AccountListCarryOverGuardTests
{
    private sealed class MutableAccountService : IAccountService
    {
        public System.Collections.Generic.List<AccountModel> Stored { get; } = new();

        // Fresh objects each load, exactly as reading from disk produces.
        public System.Collections.Generic.List<AccountModel> LoadAccounts() =>
            Stored.Select(a => new AccountModel
            {
                Id = a.Id, AccountName = a.AccountName, Username = a.Username,
                ImapHost = a.ImapHost, ImapPort = a.ImapPort, ImapUseSsl = a.ImapUseSsl,
                LoginUsername = a.LoginUsername, AuthType = a.AuthType,
            }).ToList();

        public void SaveAccounts(System.Collections.Generic.List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MainViewModel CreateVm(IAccountService accounts) =>
        new(new StubImapMailService(), accounts, new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

    [Fact]
    public void ChangingTheServerDropsTheCarriedStatus()
    {
        // Editing the host in Manage Accounts leaves the pool holding clients authenticated against
        // the OLD server. Reporting "connected" would vouch for a connection that no longer belongs
        // to this account, and would keep it out of the reconnect pass that should re-establish it.
        var svc = new MutableAccountService();
        svc.Stored.Add(new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "Kelly",
            Username = "kelly@example.com", ImapHost = "old.example.com", ImapPort = 993,
        });

        var vm = CreateVm(svc);
        vm.LoadAccountList();
        vm.Accounts[0].IsConnected = true;

        svc.Stored[0].ImapHost = "new.example.com";
        vm.LoadAccountList();

        Assert.False(vm.Accounts[0].IsConnected);
    }

    [Fact]
    public void ChangingTheLoginUsernameDropsTheCarriedStatus()
    {
        var svc = new MutableAccountService();
        svc.Stored.Add(new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "Kelly",
            Username = "kelly@example.com", ImapHost = "mail.example.com", ImapPort = 993,
        });

        var vm = CreateVm(svc);
        vm.LoadAccountList();
        vm.Accounts[0].IsConnected = true;

        svc.Stored[0].LoginUsername = "kelly";
        vm.LoadAccountList();

        Assert.False(vm.Accounts[0].IsConnected);
    }

    [Fact]
    public void AnUnchangedAccountStillKeepsItsStatus()
    {
        var svc = new MutableAccountService();
        svc.Stored.Add(new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "Kelly",
            Username = "kelly@example.com", ImapHost = "mail.example.com", ImapPort = 993,
        });

        var vm = CreateVm(svc);
        vm.LoadAccountList();
        vm.Accounts[0].IsConnected = true;

        vm.LoadAccountList();

        Assert.True(vm.Accounts[0].IsConnected);
    }

    [Fact]
    public void ADuplicateAccountIdDoesNotCrashTheReload()
    {
        // accounts.json is user-editable and has been hand-edited before. A duplicated id used to be
        // tolerated; it must not become a UI-thread crash on every Manage Accounts close.
        var shared = Guid.NewGuid();
        var svc = new MutableAccountService();
        svc.Stored.Add(new AccountModel { Id = shared, AccountName = "One", ImapHost = "h" });
        svc.Stored.Add(new AccountModel { Id = shared, AccountName = "Two", ImapHost = "h" });

        var vm = CreateVm(svc);
        vm.LoadAccountList();

        var ex = Record.Exception(() => vm.LoadAccountList());

        Assert.Null(ex);
        Assert.Equal(2, vm.Accounts.Count);
    }
}
