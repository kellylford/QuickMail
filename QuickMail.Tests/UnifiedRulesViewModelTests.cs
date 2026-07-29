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
    // Records calls and mutates Stored so a reload reflects a write.
    private sealed class FakeServerRules : IServerRuleService
    {
        public List<ServerRuleModel> Stored { get; init; } = [];
        public List<string> Calls { get; } = [];

        // Set any of these to make the matching call throw, so the failure paths can be exercised.
        public Exception? ThrowOnList { get; set; }
        public Exception? ThrowOnCreate { get; set; }
        public Exception? ThrowOnSetEnabled { get; set; }
        public Exception? ThrowOnDelete { get; set; }

        public Task<IReadOnlyList<ServerRuleModel>> ListAsync(Guid a, CancellationToken ct = default)
        { if (ThrowOnList != null) throw ThrowOnList; return Task.FromResult<IReadOnlyList<ServerRuleModel>>(Stored.ToList()); }
        public Task<ServerRuleModel> CreateAsync(Guid a, ServerRuleModel r, CancellationToken ct = default)
        { Calls.Add("create"); if (ThrowOnCreate != null) throw ThrowOnCreate; r.Id = "srv-" + Stored.Count; Stored.Add(r); return Task.FromResult(r); }
        public Task UpdateAsync(Guid a, ServerRuleModel r, CancellationToken ct = default)
        { Calls.Add("update"); var i = Stored.FindIndex(x => x.Id == r.Id); if (i >= 0) Stored[i] = r; return Task.CompletedTask; }
        public Task SetEnabledAsync(Guid a, string id, bool e, CancellationToken ct = default)
        { Calls.Add("setEnabled"); if (ThrowOnSetEnabled != null) throw ThrowOnSetEnabled; var x = Stored.FirstOrDefault(s => s.Id == id); if (x != null) x.IsEnabled = e; return Task.CompletedTask; }
        public Task ReorderAsync(Guid a, IReadOnlyList<ServerRuleModel> rules, CancellationToken ct = default)
        { Calls.Add("reorder"); Stored.Clear(); Stored.AddRange(rules); return Task.CompletedTask; }
        public Task DeleteAsync(Guid a, string id, CancellationToken ct = default)
        { Calls.Add("delete"); if (ThrowOnDelete != null) throw ThrowOnDelete; Stored.RemoveAll(s => s.Id == id); return Task.CompletedTask; }
    }

    private static async Task<ServerRuleEditorViewModel> OpenNewEditorAsync(UnifiedRulesViewModel vm)
    {
        ServerRuleEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.NewRuleCommand.Execute(null);
        return editor!;
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

    // ── New-rule classification & routing (spec §20.3) ──────────────────────

    [Fact]
    public async Task NewRule_ServerRepresentable_OnGraph_RoutesToServer()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules();
        var client = new StubRuleService();
        var vm = new UnifiedRulesViewModel(client, server, [Graph(a)], preferredAccountId: a);

        var editor = await OpenNewEditorAsync(vm);
        editor.Name = "Move digests"; editor.SubjectContains = "digest"; editor.MarkAsRead = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Contains("create", server.Calls);
        Assert.Empty(client.LoadedRules);
        Assert.Single(vm.Rules);
        Assert.Equal(RuleRunsWhere.Server, vm.Rules[0].RunsWhere);
    }

    [Fact]
    public async Task NewRule_MarkAsUnread_OnGraph_RoutesToClient_AndNotifies()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules();
        var client = new StubRuleService();
        var vm = new UnifiedRulesViewModel(client, server, [Graph(a)], preferredAccountId: a);
        string? notice = null;
        vm.ClientRuleNoticeRequested += n => notice = n;

        var editor = await OpenNewEditorAsync(vm);
        editor.Name = "Keep unread"; editor.SubjectContains = "later"; editor.MarkAsUnread = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain("create", server.Calls);      // not a server rule
        Assert.Single(client.LoadedRules);                  // persisted as a client rule
        Assert.Equal(a, client.LoadedRules[0].AccountId);
        Assert.NotNull(notice);
        Assert.Contains("Mark as unread", notice);
        Assert.Single(vm.Rules);
        Assert.Equal(RuleRunsWhere.Client, vm.Rules[0].RunsWhere);
    }

    [Fact]
    public async Task NewRule_ConflictingMix_BlocksSave()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules();
        var client = new StubRuleService();
        var vm = new UnifiedRulesViewModel(client, server, [Graph(a)], preferredAccountId: a);

        var editor = await OpenNewEditorAsync(vm);
        var closed = false;
        editor.CloseRequested += () => closed = true;
        editor.Name = "Impossible";
        editor.MarkAsUnread = true;   // client-only action
        editor.SelectedImportance = ServerRuleEditorViewModel.ImportanceOptions.First(o => o.Value == "high"); // server-only condition
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);                       // editor stays open on conflict
        Assert.False(string.IsNullOrEmpty(editor.SaveError));
        Assert.DoesNotContain("create", server.Calls);
        Assert.Empty(client.LoadedRules);
    }

    [Fact]
    public async Task EditClientRule_UpdatesInPlace_PreservingId()
    {
        var a = Guid.NewGuid();
        var original = Client("C1", a);
        var client = new StubRuleService { LoadedRules = [original] };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Graph(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(null);

        ServerRuleEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.SelectedRule = vm.Rules.Single();
        vm.EditRuleCommand.Execute(null);
        editor!.Name = "C1 renamed";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Single(client.LoadedRules);
        Assert.Equal("C1 renamed", client.LoadedRules[0].Name);
        Assert.Equal(original.Id, client.LoadedRules[0].Id);   // same rule, not a new one
    }

    // ── Failure paths (review: writes must not announce success on failure; a load failure must
    //    survive to the status line; create must re-select the new rule) ──────────────────────

    [Fact]
    public async Task NewRule_ServerCreate_SelectsTheNewRule()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], preferredAccountId: a);

        var editor = await OpenNewEditorAsync(vm);
        editor.Name = "Move digests"; editor.SubjectContains = "digest"; editor.MarkAsRead = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedRule);                                 // not stranded after create
        Assert.Equal(RuleRunsWhere.Server, vm.SelectedRule!.RunsWhere);
        Assert.Equal("Move digests", vm.SelectedRule.Name);
    }

    [Fact]
    public async Task ToggleEnabled_ServerFails_AnnouncesTheError_NotSuccess()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules { Stored = [Server("S1")], ThrowOnSetEnabled = new Exception("boom") };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedRule = vm.Rules.Single();
        string? announced = null;
        vm.AnnouncementRequested += (t, _) => announced = t;

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.Equal("boom", announced);            // the failure, not "Rule disabled."
    }

    [Fact]
    public async Task DeleteRule_ServerFails_AnnouncesTheError_AndKeepsTheRule()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules { Stored = [Server("S1")], ThrowOnDelete = new Exception("nope") };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], preferredAccountId: a);
        vm.ConfirmDeleteRequested += (_, _) => true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedRule = vm.Rules.Single();
        string? announced = null;
        vm.AnnouncementRequested += (t, _) => announced = t;

        await vm.DeleteRuleCommand.ExecuteAsync(null);

        Assert.Equal("nope", announced);            // not "Rule deleted."
        Assert.Single(vm.Rules);                    // rule stays — the reload is skipped on failure
    }

    [Fact]
    public async Task Refresh_ServerListFails_StatusReportsFailure_NotNoRules()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules { ThrowOnList = new Exception("Graph unreachable") };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("Couldn't load server rules", vm.StatusText);   // the evidence survives …
        Assert.Contains("Graph unreachable", vm.StatusText);
        Assert.DoesNotContain("No rules for this account", vm.StatusText); // … not overwritten by BuildStatus
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
