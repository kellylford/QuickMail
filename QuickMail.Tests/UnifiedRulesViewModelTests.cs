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
    private static AccountModel PersonalGraph(Guid id) => new() { Id = id, BackendKind = BackendKind.MicrosoftGraph, IsPersonalMicrosoftAccount = true, Username = "me@outlook.com", AccountName = "Personal" };
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
    public async Task Refresh_PersonalGraphAccount_LoadsOnlyClientRules() // #541
    {
        // Personal Microsoft (Graph) accounts don't get MailboxSettings.ReadWrite, so server rules
        // aren't possible — the rules window must not offer them (it would 403 into a meaningless
        // "ask your administrator" for a mailbox with no admin). They use client rules only.
        var a = Guid.NewGuid();
        var server = new FakeServerRules { Stored = [Server("S1")] };
        var client = new StubRuleService { LoadedRules = [Client("C1", a)] };
        var vm = new UnifiedRulesViewModel(client, server, [PersonalGraph(a)], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.AccountSupportsServerRules);   // personal Graph → no server rules, despite Graph backend
        Assert.Single(vm.Rules);                       // the server rule is NOT loaded
        Assert.Equal(RuleRunsWhere.Client, vm.Rules[0].RunsWhere);
    }

    [Fact]
    public async Task Refresh_UndetectedPersonalGraphAccount_CaughtByDomainGuess_LoadsOnlyClientRules() // #541
    {
        // The tenant flag hasn't been detected yet (null), but the address is a consumer domain, so the
        // domain-guess fallback (same as scope selection) resolves it as personal → still no server rules.
        var a = Guid.NewGuid();
        var acct = new AccountModel { Id = a, BackendKind = BackendKind.MicrosoftGraph, Username = "me@outlook.com", AccountName = "Undetected" };
        var server = new FakeServerRules { Stored = [Server("S1")] };
        var client = new StubRuleService { LoadedRules = [Client("C1", a)] };
        var vm = new UnifiedRulesViewModel(client, server, [acct], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.AccountSupportsServerRules);
        Assert.Single(vm.Rules);
    }

    [Fact]
    public async Task NewRule_NoServerService_SavesClient_NoNotice_NoNullRef() // #550
    {
        // The VM can be built with no server-rule service (serverRules: null) — a defensive path the
        // _serverRules != null guards exist for. Exercise a full new-rule save through it: it must persist
        // a client rule, treat the account as client-only (no server rules, no save announcement — the
        // status line covers it), and never touch the absent server service.
        var a = Guid.NewGuid();
        var client = new StubRuleService();
        var vm = new UnifiedRulesViewModel(client, serverRules: null, [Graph(a)], preferredAccountId: a);
        var announcements = new List<string>();
        vm.AnnouncementRequested += (t, _) => announcements.Add(t);

        Assert.False(vm.AccountSupportsServerRules);   // no service → client-only even for a Graph account
        var editor = await OpenNewEditorAsync(vm);
        editor.Name = "File it"; editor.SubjectContains = "later"; editor.MarkAsUnread = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Single(client.LoadedRules);
        Assert.Equal(RuleRunsWhere.Client, vm.Rules[0].RunsWhere);
        Assert.DoesNotContain(announcements, t => t.Contains("Saving as a client-side rule"));
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

    // ── Shared server-rules predicate (#550) ────────────────────────────────
    // SupportsServerRules is the one named per-account capability test; AccountSupportsServerRules is it
    // plus a live server-rule service. Pinning that they agree keeps the two from drifting apart again.

    [Fact]
    public void SupportsServerRules_WorkGraph_True()
        => Assert.True(UnifiedRulesViewModel.SupportsServerRules(Graph(Guid.NewGuid())));

    [Fact]
    public void SupportsServerRules_PersonalGraph_False() // #541/#550: personal Graph has no MailboxSettings.ReadWrite
        => Assert.False(UnifiedRulesViewModel.SupportsServerRules(PersonalGraph(Guid.NewGuid())));

    [Fact]
    public void SupportsServerRules_UndetectedPersonalGraph_CaughtByDomainGuess_False()
        => Assert.False(UnifiedRulesViewModel.SupportsServerRules(
            new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph, Username = "me@outlook.com", AccountName = "Undetected" }));

    [Fact]
    public void SupportsServerRules_Imap_False()
        => Assert.False(UnifiedRulesViewModel.SupportsServerRules(Imap(Guid.NewGuid())));

    [Theory]
    [InlineData("work")]      // work/school Graph
    [InlineData("personal")]  // personal Graph
    [InlineData("imap")]      // IMAP
    public async Task SupportsServerRules_AgreesWith_AccountSupportsServerRules(string kind)
    {
        // The whole point of #550: the standalone capability predicate and the VM's own gate must
        // return the same answer for the same account, or which server-rule features the window offers
        // disagrees with whether it should.
        var a = Guid.NewGuid();
        var acct = kind switch { "work" => Graph(a), "personal" => PersonalGraph(a), _ => Imap(a) };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(), [acct], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(vm.AccountSupportsServerRules, UnifiedRulesViewModel.SupportsServerRules(acct));
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
    public async Task NewRule_MarkAsUnread_OnGraph_RoutesToClient_AndAnnounces() // #550
    {
        // On a server-capable (work/school Graph) account, a rule that uses a client-only action falls
        // back to a client rule — a surprise the status line (which says the account supports server
        // rules) doesn't cover, so the user is told, via a non-blocking Result announcement (no modal).
        var a = Guid.NewGuid();
        var server = new FakeServerRules();
        var client = new StubRuleService();
        var vm = new UnifiedRulesViewModel(client, server, [Graph(a)], preferredAccountId: a);
        var announcements = new List<(string Text, AnnouncementCategory Category)>();
        vm.AnnouncementRequested += (t, c) => announcements.Add((t, c));

        var editor = await OpenNewEditorAsync(vm);
        editor.Name = "Keep unread"; editor.SubjectContains = "later"; editor.MarkAsUnread = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain("create", server.Calls);      // not a server rule
        Assert.Single(client.LoadedRules);                  // persisted as a client rule
        Assert.Equal(a, client.LoadedRules[0].AccountId);
        var notice = announcements.LastOrDefault(x => x.Text.Contains("client-side rule"));
        Assert.Equal(AnnouncementCategory.Result, notice.Category);
        Assert.Equal("Saving as a client-side rule.", notice.Text);
        Assert.Single(vm.Rules);
        Assert.Equal(RuleRunsWhere.Client, vm.Rules[0].RunsWhere);
    }

    [Fact]
    public async Task NewRule_OnClientOnlyAccount_RoutesToClient_WithoutSavedNotice() // #550
    {
        // On an IMAP (client-only) account every rule is a client-side rule and the status line already
        // says so, so a per-save "saved as a client-side rule" notice would be chatter — it must not fire.
        var a = Guid.NewGuid();
        var client = new StubRuleService();
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Imap(a)], preferredAccountId: a);
        var announcements = new List<string>();
        vm.AnnouncementRequested += (t, _) => announcements.Add(t);

        var editor = await OpenNewEditorAsync(vm);
        editor.Name = "File it"; editor.SubjectContains = "later"; editor.MarkAsUnread = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Single(client.LoadedRules);                  // persisted as a client rule
        Assert.Equal(RuleRunsWhere.Client, vm.Rules[0].RunsWhere);
        Assert.DoesNotContain(announcements, t => t.Contains("Saving as a client-side rule"));
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
        Assert.DoesNotContain("No rules yet", vm.StatusText);              // … not overwritten by BuildStatus
    }

    // ── The account's rule mode lives in the status line, and is not spoken ────
    // An empty rule list can't imply whether the account runs rules on the server or only on the
    // client, so the status line says it outright. It used to be a spoken Hint on every
    // account-context load, which meant arrowing through the account picker spoke a sentence per
    // account it passed through (#550). The status line is an F6 stop, read on demand.

    [Fact]
    public void NoRulesStatus_DistinguishesServerCapableFromClientOnly()
    {
        var both = UnifiedRulesViewModel.NoRulesStatus(supportsServerRules: true);
        Assert.Contains("server-side", both);
        Assert.Contains("client-side", both);

        var clientOnly = UnifiedRulesViewModel.NoRulesStatus(supportsServerRules: false);
        Assert.Contains("client-side", clientOnly);
        Assert.DoesNotContain("server-side", clientOnly);   // must not imply a capability it hasn't got
    }

    [Fact]
    public async Task Refresh_WorkSchoolGraph_WithNoRules_StatusStatesBothModes()
    {
        var a = Guid.NewGuid();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(), [Graph(a)], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Rules);
        Assert.Equal(UnifiedRulesViewModel.NoRulesStatus(true), vm.StatusText);
    }

    [Fact]
    public async Task Refresh_PersonalGraph_WithNoRules_StatusStatesClientOnly()
    {
        var a = Guid.NewGuid();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(), [PersonalGraph(a)], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(UnifiedRulesViewModel.NoRulesStatus(false), vm.StatusText);   // personal → client-only
    }

    [Fact]
    public async Task SwitchingAccount_MovesTheStatusLineToTheNewAccountsMode()
    {
        var work = Guid.NewGuid();
        var personal = Guid.NewGuid();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(),
            [Graph(work), PersonalGraph(personal)], preferredAccountId: work);
        await vm.RefreshCommand.ExecuteAsync(null);          // initial: work (server-capable)
        Assert.Equal(UnifiedRulesViewModel.NoRulesStatus(true), vm.StatusText);

        // Selecting a new account must itself re-run the load — no manual refresh. The client-only load
        // path is synchronous, so it has completed by the time the setter returns; this pins the
        // auto-refresh-on-switch wiring that the status line depends on.
        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == personal);

        Assert.Equal(UnifiedRulesViewModel.NoRulesStatus(false), vm.StatusText);
    }

    [Fact]
    public async Task MovingThroughTheAccountPicker_SaysNothing()   // #550
    {
        // Arrowing down the account list changes the selection once per account, and each change
        // reloads. When the mode was spoken, passing a client-only account on the way to a
        // server-capable one produced "supports only client-side" then "supports both" — two correct
        // announcements that read as one wrong one. Landing on an account must now say nothing at all.
        var work = Guid.NewGuid();
        var personal = Guid.NewGuid();
        var vm = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(),
            [Graph(work), PersonalGraph(personal)], preferredAccountId: work);
        await vm.RefreshCommand.ExecuteAsync(null);
        var announces = new List<string>();
        vm.AnnouncementRequested += (t, _) => announces.Add(t);

        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == personal);
        // Anchor on the FIRST switch, where the expected status differs from the one the initial refresh
        // left behind. OnSelectedAccountChanged is fire-and-forget, so asserting the work account's own
        // status after switching away and back proves nothing — deleting the auto-refresh wiring outright
        // would leave that same value in place and pass. This value can only be here if the switch ran.
        Assert.Equal(UnifiedRulesViewModel.NoRulesStatus(false), vm.StatusText);

        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == work);
        Assert.Equal(UnifiedRulesViewModel.NoRulesStatus(true), vm.StatusText);

        Assert.Empty(announces);
    }

    [Fact]
    public async Task LoadFailureWithNoRules_StillStatesTheMode()
    {
        // Nothing loaded means nothing to count, so without the mode clause a failed load on a
        // client-only account and one on a server-capable account read identically — and no other
        // surface in the window tells them apart.
        var a = Guid.NewGuid();
        var server = new FakeServerRules { ThrowOnList = new Exception("Graph unreachable") };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], preferredAccountId: a);

        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        // The WHOLE line, not two Contains: the defect this replaced was a missing full stop between
        // the failure and the clause, which every substring assertion passed straight over.
        Assert.Equal("Couldn't load server rules: Graph unreachable. " + UnifiedRulesViewModel.ModeClause(true),
                     vm.StatusText);
    }

    [Fact]
    public void FailureText_IsPunctuatedBeforeTheClauseIsAppended()
    {
        // Exception messages rarely end in a full stop, so appending a sentence to one produced
        // "…Graph unreachable It supports…" — one run-on with no break to read. Terminated() adds the
        // stop; a message that already has one must not gain a second.
        Assert.Equal("Ends already. " + UnifiedRulesViewModel.ModeClause(false),
                     UnifiedRulesViewModel.BuildStatus([], ["Ends already."], supportsServerRules: false));
        Assert.Equal("No full stop. " + UnifiedRulesViewModel.ModeClause(false),
                     UnifiedRulesViewModel.BuildStatus([], ["No full stop"], supportsServerRules: false));
        // Two failures: each is a sentence of its own, not one run-on with the next.
        Assert.Equal("First. Second. " + UnifiedRulesViewModel.ModeClause(true),
                     UnifiedRulesViewModel.BuildStatus([], ["First", "Second."], supportsServerRules: true));
    }

    [Fact]
    public async Task EveryStatusLine_SaysWhatKindsTheAccountCanHold()
    {
        // The guide promises this unconditionally, and the mode used to be spoken on every load. All four
        // cells of (account kind x has rules), because an earlier pass covered three and the missing one —
        // server-capable WITH rules — was the only one where the capability was merely implied, by the
        // presence of "0 on server". Asserting the whole clause, not a substring of it, so a reworded
        // clause cannot half-satisfy this.
        var work = Guid.NewGuid();
        var personal = Guid.NewGuid();

        // Server-capable and client-only, no rules.
        var empty = new UnifiedRulesViewModel(new StubRuleService(), new FakeServerRules(),
            [Graph(work), PersonalGraph(personal)], preferredAccountId: work);
        await empty.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Contains(UnifiedRulesViewModel.ModeClause(true), empty.StatusText);
        empty.SelectedAccount = empty.AccountOptions.First(o => o.Id == personal);
        Assert.Contains(UnifiedRulesViewModel.ModeClause(false), empty.StatusText);

        // Client-only, WITH rules.
        var clientRules = new UnifiedRulesViewModel(
            new StubRuleService { LoadedRules = [Client("C1", personal)] }, new FakeServerRules(),
            [PersonalGraph(personal)], preferredAccountId: personal);
        await clientRules.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Contains(UnifiedRulesViewModel.ModeClause(false), clientRules.StatusText);

        // Server-capable, WITH rules — and specifically with NO server rule among them, the shape where
        // the count alone reads as though server rules were not available here.
        var serverCapable = new UnifiedRulesViewModel(
            new StubRuleService { LoadedRules = [Client("C1", work)] }, new FakeServerRules(),
            [Graph(work)], preferredAccountId: work);
        await serverCapable.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Contains("0 on server", serverCapable.StatusText);
        Assert.Contains(UnifiedRulesViewModel.ModeClause(true), serverCapable.StatusText);
    }

    [Fact]
    public async Task WriteReload_StaysSilentAboutTheMode()
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules { Stored = [Server("S1")] };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedRule = vm.Rules.Single();
        var announces = new List<string>();
        vm.AnnouncementRequested += (t, _) => announces.Add(t);

        await vm.ToggleEnabledCommand.ExecuteAsync(null);   // a write → reload, but the account is unchanged

        // Assert the exact set: probing for a word like "supports" would let any reworded mode
        // announcement straight through, and the point is that the reload announces the WRITE and
        // nothing else.
        Assert.Equal(["Rule disabled."], announces);
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

    // ── Field labels (#493 Gap 1: honor RuleListShowFieldLabels in the unified list) ──────────

    [Fact]
    public async Task ClientRuleSummary_ResolvesGraphFolderIdToName() // #550: raw Graph folder id → "Deleted Items"
    {
        var a = Guid.NewGuid();
        const string folderId = "AQMkAD-opaque-graph-folder-id";
        var rule = new MailRule
        {
            Name = "Security", AccountId = a,
            UseFromCondition = true, FromContains = "account-security-noreply@accountprotection.microsoft.com",
            Action = RuleAction.MoveToFolder, TargetFolder = folderId,
        };
        var client = new StubRuleService { LoadedRules = [rule] };
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [a] = [new MailFolderModel { FullName = folderId, DisplayName = "Deleted Items", Kind = SpecialFolderKind.Trash }],
        };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Graph(a)], folders, preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var row = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client);
        Assert.Contains("move to Deleted Items", row.RowText);
        Assert.DoesNotContain(folderId, row.RowText);      // the opaque id never reaches the user
    }

    [Fact]
    public async Task ClientRuleSummary_FallsBackToRawTarget_WhenFolderUnknown() // IMAP path reads fine as-is
    {
        var a = Guid.NewGuid();
        var rule = new MailRule
        {
            Name = "File", AccountId = a, UseSubjectCondition = true, SubjectContains = "x",
            Action = RuleAction.MoveToFolder, TargetFolder = "Archive",
        };
        var client = new StubRuleService { LoadedRules = [rule] };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Imap(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var row = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client);
        Assert.Contains("move to Archive", row.RowText);
    }

    [Fact]
    public async Task ClientRuleSummary_ImapAccount_KeepsFullPath_NotLeafName() // #550 review: no leaf collapse for IMAP
    {
        // Even with the folder in the cache, an IMAP target keeps its readable path. Resolving it to the
        // leaf DisplayName would make "Work/Archive" and "Personal/Archive" both read "Archive".
        var a = Guid.NewGuid();
        var rule = new MailRule
        {
            Name = "File", AccountId = a, UseSubjectCondition = true, SubjectContains = "x",
            Action = RuleAction.MoveToFolder, TargetFolder = "Work/Archive",
        };
        var client = new StubRuleService { LoadedRules = [rule] };
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [a] = [new MailFolderModel { FullName = "Work/Archive", DisplayName = "Archive" }],
        };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Imap(a)], folders, preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var row = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client);
        Assert.Contains("move to Work/Archive", row.RowText);   // full path kept, not collapsed to "Archive"
    }

    [Fact]
    public async Task ClientRuleSummary_GraphTargetNotCached_ReadsAnotherFolder_NotTheOpaqueId() // #550 review L3
    {
        // A Graph client rule whose target folder isn't in the cache — not yet synced, or the id drifted
        // (#366) — must not print the raw "AQMkAD…" id; it reads "another folder", as a server rule does.
        var a = Guid.NewGuid();
        const string folderId = "AQMkAD-not-in-cache";
        var rule = new MailRule
        {
            Name = "File", AccountId = a, UseSubjectCondition = true, SubjectContains = "x",
            Action = RuleAction.MoveToFolder, TargetFolder = folderId,
        };
        var client = new StubRuleService { LoadedRules = [rule] };
        var folders = new Dictionary<Guid, List<MailFolderModel>> { [a] = [] };   // Graph account, id not cached
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Graph(a)], folders, preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var row = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client);
        Assert.Contains("move to another folder", row.RowText);
        Assert.DoesNotContain(folderId, row.RowText);            // the opaque id never reaches the user
    }

    [Fact]
    public async Task ServerRuleDetail_ResolvesCopyFolderIdToName_WhenNameMissing() // #550 review: copy-to path
    {
        var a = Guid.NewGuid();
        const string folderId = "AQMkAD-copy-target";
        var serverRule = new ServerRuleModel { Id = "s1", DisplayName = "Archive copies", SenderContains = "x@y.com", CopyToFolderId = folderId };
        var server = new FakeServerRules { Stored = [serverRule] };
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [a] = [new MailFolderModel { FullName = folderId, DisplayName = "Backups" }],
        };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], folders, preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var detail = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Server).DetailText;
        Assert.Contains("copy to Backups", detail);
        Assert.DoesNotContain("another folder", detail);
        Assert.DoesNotContain(folderId, detail);
    }

    [Fact]
    public async Task ServerRuleDetail_DoesNotOverwrite_AnExistingFolderName() // #550 review: only fill an empty name
    {
        // The rule already carries a resolved name (e.g. set by the editor); the cache holds a DIFFERENT
        // name for the same id. The existing name must win — the resolve pass only fills an empty one.
        var a = Guid.NewGuid();
        const string folderId = "AQMkAD-target";
        var serverRule = new ServerRuleModel
        {
            Id = "s1", DisplayName = "R", SenderContains = "x@y.com",
            MoveToFolderId = folderId, MoveToFolderName = "Editor Name",
        };
        var server = new FakeServerRules { Stored = [serverRule] };
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [a] = [new MailFolderModel { FullName = folderId, DisplayName = "Cache Name" }],
        };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], folders, preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var detail = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Server).DetailText;
        Assert.Contains("move to Editor Name", detail);
        Assert.DoesNotContain("Cache Name", detail);
    }

    [Fact]
    public async Task ServerRuleDetail_ResolvesMoveFolderIdToName_WhenNameMissing() // #550
    {
        // Graph returns a folder id but no name on a server rule's move action, so the prose fell back to
        // "another folder". Resolve it from the folder cache, same as client rules.
        var a = Guid.NewGuid();
        const string folderId = "AQMkAD-server-target";
        var serverRule = new ServerRuleModel { Id = "s1", DisplayName = "Rocket", SenderContains = "rocket@x.com", MoveToFolderId = folderId };
        var server = new FakeServerRules { Stored = [serverRule] };
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [a] = [new MailFolderModel { FullName = folderId, DisplayName = "Statements" }],
        };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), server, [Graph(a)], folders, preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var detail = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Server).DetailText;
        Assert.Contains("move to Statements", detail);
        Assert.DoesNotContain("another folder", detail);
        Assert.DoesNotContain(folderId, detail);
    }

    [Fact]
    public async Task ClientRuleDetail_MirrorsServerStructure_NoRunsLine_WithFolderName() // #550
    {
        var a = Guid.NewGuid();
        const string folderId = "AQMkAD-opaque";
        var rule = new MailRule
        {
            Name = "Security", AccountId = a,
            UseFromCondition = true, FromContains = "security@x.com",
            UseSubjectCondition = true, SubjectContains = "alert",
            Action = RuleAction.MoveToFolder, TargetFolder = folderId,
        };
        var client = new StubRuleService { LoadedRules = [rule] };
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [a] = [new MailFolderModel { FullName = folderId, DisplayName = "Deleted Items", Kind = SpecialFolderKind.Trash }],
        };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Graph(a)], folders, preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var detail = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client).DetailText;

        Assert.StartsWith("Security (enabled)", detail);
        Assert.Contains("Applies when:", detail);           // same section headers as a server rule
        Assert.Contains("Does:", detail);
        Assert.Contains("from contains 'security@x.com';", detail);   // items are ";"-separated, one per line
        Assert.DoesNotContain("alert';", detail);                     // …with no trailing ";" on the last item
        Assert.Contains("move to Deleted Items", detail);    // resolved, not the raw id
        Assert.DoesNotContain(folderId, detail);
        Assert.DoesNotContain("client-side", detail);        // no "runs client-side" line — spoken elsewhere already
        Assert.DoesNotContain("only while QuickMail is open", detail);
    }

    [Fact]
    public async Task RowText_NoFieldLabels_ByDefault()
    {
        var a = Guid.NewGuid();
        var client = new StubRuleService { LoadedRules = [Client("Newsletters", a)] };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Graph(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var row = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client);
        Assert.StartsWith("Newsletters, on client, enabled", row.RowText);
        Assert.DoesNotContain("Rule Newsletters", row.RowText);
    }

    [Fact]
    public async Task RowText_LabelsFields_WhenShowFieldLabelsOn()
    {
        var a = Guid.NewGuid();
        var cfg = new StubConfigService();
        cfg.Save(new ConfigModel { RuleListShowFieldLabels = true });
        var client = new StubRuleService { LoadedRules = [Client("Newsletters", a)] };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Graph(a)],
            preferredAccountId: a, configService: cfg);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        var row = vm.Rules.First(r => r.RunsWhere == RuleRunsWhere.Client);
        Assert.StartsWith("Rule Newsletters, runs on client, status enabled", row.RowText);
    }

    [Fact]
    public async Task StatusText_ClientOnlyAccount_DropsServerBreakdown() // #550 wording
    {
        // A client-only account can't have server rules, so "0 on server" is noise — but dropping the
        // split must not drop the capability with it. The absence of "N on server" is not something a
        // reader can be asked to notice, so the mode clause says it outright, in the same words the
        // server-capable branch uses.
        var a = Guid.NewGuid();
        var client = new StubRuleService { LoadedRules = [Client("C1", a), Client("C2", a)] };
        var vm = new UnifiedRulesViewModel(client, new FakeServerRules(), [Imap(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal("2 rules. " + UnifiedRulesViewModel.ModeClause(false), vm.StatusText);
        Assert.DoesNotContain("on server", vm.StatusText);
    }

    [Fact]
    public async Task StatusText_ServerCapableAccount_ShowsServerClientBreakdown() // #550 wording
    {
        var a = Guid.NewGuid();
        var server = new FakeServerRules { Stored = [Server("S1")] };
        var client = new StubRuleService { LoadedRules = [Client("C1", a)] };
        var vm = new UnifiedRulesViewModel(client, server, [Graph(a)], preferredAccountId: a);
        await vm.RefreshCommand.ExecuteAsync(TestContext.Current.CancellationToken);

        // The split, then the capability in the same words the client-only branch uses — the two
        // account kinds are described in one register, neither left to be inferred from the
        // other's shape.
        Assert.Equal("2 rules: 1 on server, 1 on client. " + UnifiedRulesViewModel.ModeClause(true),
                     vm.StatusText);
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
