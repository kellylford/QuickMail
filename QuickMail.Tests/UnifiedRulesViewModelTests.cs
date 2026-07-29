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

public class UnifiedRulesViewModelTests
{
    // Minimal IServerRuleService whose ListAsync returns a configured set; write methods aren't
    // exercised by these load tests.
    private sealed class FakeServerRules : IServerRuleService
    {
        public List<ServerRuleModel> Stored { get; init; } = [];
        public Task<IReadOnlyList<ServerRuleModel>> ListAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ServerRuleModel>>(Stored);
        public Task<ServerRuleModel> CreateAsync(Guid a, ServerRuleModel r, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAsync(Guid a, ServerRuleModel r, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetEnabledAsync(Guid a, string id, bool e, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReorderAsync(Guid a, IReadOnlyList<ServerRuleModel> rules, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(Guid a, string id, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static AccountModel Graph(Guid id) => new() { Id = id, BackendKind = BackendKind.MicrosoftGraph, Username = "g@x.com", AccountName = "Work" };
    private static AccountModel Imap(Guid id) => new() { Id = id, BackendKind = BackendKind.ImapSmtp, Username = "i@x.com", AccountName = "Home" };
    private static ServerRuleModel Server(string name) => new() { Id = name, DisplayName = name, SubjectContains = "x", MarkAsRead = true };
    private static MailRule Client(string name, Guid accountId) => new() { Name = name, AccountId = accountId, SubjectContains = "y", Action = RuleAction.MarkAsRead };

    [Fact]
    public async Task Refresh_GraphAccount_MergesServerThenClientRules()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules { Stored = [Server("S1"), Server("S2")] };
        var client = new StubRuleService { LoadedRules = [Client("C1", a), Client("Other", Guid.NewGuid())] };
        var vm = new UnifiedRulesViewModel(client, server, [Graph(a)], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.AccountSupportsServerRules);
        Assert.Equal(3, vm.Rules.Count);                              // 2 server + 1 client (this account)
        Assert.Equal(RuleRunsWhere.Server, vm.Rules[0].RunsWhere);    // server first
        Assert.Equal(RuleRunsWhere.Server, vm.Rules[1].RunsWhere);
        Assert.Equal(RuleRunsWhere.Client, vm.Rules[2].RunsWhere);    // then client
        Assert.Equal("C1", vm.Rules[2].Name);                        // the other account's rule is excluded
    }

    [Fact]
    public async Task Refresh_ImapAccount_LoadsOnlyClientRules()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules { Stored = [Server("S1")] };
        var client = new StubRuleService { LoadedRules = [Client("C1", a)] };
        var vm = new UnifiedRulesViewModel(client, server, [Imap(a)], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.AccountSupportsServerRules);   // IMAP → no server rules
        Assert.Single(vm.Rules);
        Assert.Equal(RuleRunsWhere.Client, vm.Rules[0].RunsWhere);
    }

    [Fact]
    public async Task Refresh_NoServerService_LoadsOnlyClientRules_EvenForGraphAccount()
    {
        var a = Guid.NewGuid();
        var client = new StubRuleService { LoadedRules = [Client("C1", a)] };
        var vm = new UnifiedRulesViewModel(client, serverRules: null, [Graph(a)], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.AccountSupportsServerRules);
        Assert.Single(vm.Rules);
        Assert.Equal(RuleRunsWhere.Client, vm.Rules[0].RunsWhere);
    }

    [Fact]
    public void AccountPicker_SeedsToPreferredAccount_AndHidesWhenSingle()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), null, [Graph(a), Imap(b)], preferredAccountId: b);
        Assert.Equal(b, vm.SelectedAccount?.Id);
        Assert.True(vm.ShowAccountSelector);

        var single = new UnifiedRulesViewModel(new StubRuleService(), null, [Graph(a)]);
        Assert.False(single.ShowAccountSelector);
    }
}
