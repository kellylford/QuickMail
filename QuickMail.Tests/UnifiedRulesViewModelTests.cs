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

    // ── Prefill-from-message (Ctrl+Shift+T) and Run-on-Existing in the unified window ──────────

    [Fact]
    public async Task NewRuleFromTemplate_PrefillsFromMessage_AndScopesToItsAccount()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(), [Graph(a), Graph(b)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(null);

        ServerRuleEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.NewRuleFromTemplate(new MailRule
        {
            Name = "Rule for x@y.com", FromContains = "x@y.com", SubjectContains = "Invoice", AccountId = b,
        });

        Assert.Equal(b, vm.SelectedAccount?.Id);            // switched to the message's account
        Assert.NotNull(editor);
        Assert.True(editor!.IsNew);                         // a NEW rule, not an edit
        Assert.Equal("Rule for x@y.com", editor.Name);      // prefilled from the message
        Assert.Equal("x@y.com", editor.FromAddresses);
        Assert.Equal("Invoice", editor.SubjectContains);
    }

    [Fact]
    public async Task RunOnExisting_InvokesOwner_ScopedToSelectedAccount_AndAnnouncesTheCount()
    {
        var a = Guid.NewGuid();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(), [Graph(a)]);
        Guid? scope = Guid.Empty;
        vm.RunOnExistingRequested += id => { scope = id; return Task.FromResult(3); };
        string? announced = null;
        vm.AnnouncementRequested += (t, _) => announced = t;

        await vm.RunOnExistingCommand.ExecuteAsync(null);

        Assert.Equal(a, scope);   // #493: runs only the account in the picker, not all accounts
        Assert.Contains("3 messages moved or deleted", announced);
        Assert.Contains("3 messages moved or deleted", vm.StatusText);   // visible too, for announcements-off users
    }

    [Fact]
    public async Task RunOnExisting_DisabledWhenAccountHasNoEnabledClientRules()
    {
        var a = Guid.NewGuid();
        // Graph account whose only rule is server-side → nothing for the client-only run to do.
        var server = new FakeServerRules();
        server.Stored.Add(Server("S1"));
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.CanRunOnExisting);
        Assert.False(vm.RunOnExistingCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunOnExisting_EnabledWhenAccountHasAnEnabledClientRule()
    {
        var a = Guid.NewGuid();
        var client = new StubRuleService { LoadedRules = [Client("C1", a)] };  // enabled by default
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Graph(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.CanRunOnExisting);
        Assert.True(vm.RunOnExistingCommand.CanExecute(null));
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

    // ── Test Rule (#488 review: parity with RulesManagerWindow's Test button) ─────────

    private static MailMessageSummary Msg(string id, string from = "a@b.com") => new() { MessageId = id, Subject = "hello", From = from };

    [Fact]
    public async Task TestRule_ClientRule_ReportsRealMatchCount_AsResultAnnouncement()
    {
        // Real RuleService so condition matching is exercised end-to-end — the count must reflect an
        // actual subset (1 of 2), not the "matches everything" stub.
        var a = Guid.NewGuid();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var rules = new RuleService(new StubImapMailService(), new StubLocalStoreService(), dir);
            rules.SaveRules([new MailRule { Name = "From Alice", AccountId = a, FromContains = "alice", Action = RuleAction.MarkAsRead }]);

            var messages = new[] { Msg("1", "alice@example.com"), Msg("2", "bob@example.com") };
            var vm = new UnifiedRulesViewModel(rules, new FakeServerRules(), [Graph(a)],
                preferredAccountId: a, selectedMessagesForTest: messages);
            await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);
            vm.SelectedRule = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client);

            (string Text, AnnouncementCategory Cat)? announced = null;
            vm.AnnouncementRequested += (t, c) => announced = (t, c);

            vm.TestRuleCommand.Execute(null);

            Assert.Equal("Rule would match 1 of 2 selected messages.", vm.StatusText);
            Assert.NotNull(announced);
            Assert.Equal(AnnouncementCategory.Result, announced!.Value.Cat);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task TestRule_ServerRule_CommandDisabled()
    {
        // Test has no meaning for a server rule (it runs in Exchange), so the command is disabled for a
        // server row — same gating as Edit/Delete/Move, and correct for a user running announcements off.
        var a = Guid.NewGuid();
        var server = new FakeServerRules();
        server.Stored.Add(Server("S1"));
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)],
            preferredAccountId: a, selectedMessagesForTest: new[] { Msg("1") });
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);
        vm.SelectedRule = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Server);

        Assert.False(vm.CanTestSelected);
        Assert.False(vm.TestRuleCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestRule_NoMessagesSelected_SaysNoneSelected()
    {
        var a = Guid.NewGuid();
        var client = new StubRuleService { LoadedRules = [Client("C1", a)] };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Graph(a)],
            preferredAccountId: a);   // opened with no main-window selection
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);
        vm.SelectedRule = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client);

        vm.TestRuleCommand.Execute(null);

        Assert.Equal("No messages selected in the main window.", vm.StatusText);
    }

    [Fact]
    public void TestRule_NothingSelected_CommandDisabled()
    {
        var a = Guid.NewGuid();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(), [Graph(a)],
            preferredAccountId: a, selectedMessagesForTest: new[] { Msg("1") });
        Assert.False(vm.TestRuleCommand.CanExecute(null));   // no rule selected yet
    }
}
