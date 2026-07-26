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
/// Server-rules ViewModels: command availability (especially the edit gating that protects against
/// Graph PATCH replacing predicates we don't model), the admin-directed permission path, reorder
/// rollback, and editor validation/assembly. No View types are touched — confirmations and the
/// permission message are raised as events, per the MVVM rules.
/// </summary>
public class ServerRulesViewModelTests
{
    private readonly Guid _accountId = Guid.NewGuid();

    // ── Fakes ────────────────────────────────────────────────────────────────────

    private sealed class FakeServerRuleService : IServerRuleService
    {
        public List<ServerRuleModel> Stored { get; set; } = [];
        public Exception? ThrowOnWrite { get; set; }
        public Exception? ThrowOnList { get; set; }

        public List<string> Calls { get; } = [];
        public IReadOnlyList<string>? LastReorder { get; private set; }
        public (string Id, bool Enabled)? LastToggle { get; private set; }

        public Task<IReadOnlyList<ServerRuleModel>> ListAsync(Guid accountId, CancellationToken ct = default)
        {
            Calls.Add("list");
            if (ThrowOnList is not null) throw ThrowOnList;
            return Task.FromResult<IReadOnlyList<ServerRuleModel>>(Stored);
        }

        public Task<ServerRuleModel> CreateAsync(Guid accountId, ServerRuleModel rule, CancellationToken ct = default)
        {
            Calls.Add("create");
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            rule.Id = "created-id";
            return Task.FromResult(rule);
        }

        public Task UpdateAsync(Guid accountId, ServerRuleModel rule, CancellationToken ct = default)
        {
            Calls.Add("update");
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            return Task.CompletedTask;
        }

        public Task SetEnabledAsync(Guid accountId, string ruleId, bool enabled, CancellationToken ct = default)
        {
            Calls.Add("setEnabled");
            LastToggle = (ruleId, enabled);
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            return Task.CompletedTask;
        }

        public Task ReorderAsync(Guid accountId, IReadOnlyList<ServerRuleModel> rulesInOrder, CancellationToken ct = default)
        {
            Calls.Add("reorder");
            LastReorder = rulesInOrder.Select(r => r.Id).ToList();
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid accountId, string ruleId, CancellationToken ct = default)
        {
            Calls.Add("delete");
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            return Task.CompletedTask;
        }
    }

    private AccountModel GraphAccount() => new()
    {
        Id = _accountId,
        BackendKind = BackendKind.MicrosoftGraph,
        Username = "user@contoso.com",
        AccountName = "Work",
    };

    private ServerRulesViewModel Vm(FakeServerRuleService svc, params AccountModel[] accounts)
        => new(svc, accounts.Length > 0 ? accounts : [GraphAccount()]);

    [Fact]
    public void Ctor_DefaultsToPreferredAccount_WhenProvided()
    {
        var first = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph, Username = "a@x.com", AccountName = "A" };
        var second = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph, Username = "b@x.com", AccountName = "B" };

        // Opening from the second account's inbox should land on the second account, not the first.
        var vm = new ServerRulesViewModel(new FakeServerRuleService(), [first, second], null, second.Id);
        Assert.Equal(second.Id, vm.SelectedAccount?.Id);

        // No current-account context (e.g. an aggregate view) → fall back to the first account.
        var fallback = new ServerRulesViewModel(new FakeServerRuleService(), [first, second], null, null);
        Assert.Equal(first.Id, fallback.SelectedAccount?.Id);
    }

    private static ServerRuleModel Rule(string id, string name, bool enabled = true,
        bool editable = true, bool readOnly = false) => new()
    {
        Id = id,
        DisplayName = name,
        IsEnabled = enabled,
        IsFullyEditable = editable,
        IsReadOnly = readOnly,
        SubjectContains = "x",
        MarkAsRead = true,
    };

    // ── Listing ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_LoadsRulesAndSelectsFirst()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha"), Rule("b", "Beta", enabled: false)] };
        var vm = Vm(svc);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Rules.Count);
        Assert.Equal("a", vm.SelectedRule!.Id);
        Assert.Contains("2 rules", vm.StatusText);
        Assert.Contains("1 disabled", vm.StatusText);
    }

    [Fact]
    public async Task Refresh_Failure_ShowsMessage_NeverSilentlyEmpty()
    {
        var svc = new FakeServerRuleService { ThrowOnList = new InvalidOperationException("network down") };
        var vm = Vm(svc);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("network down", vm.StatusText);
    }

    [Fact]
    public async Task Refresh_PermissionRefused_RaisesAdminDirectedEvent()
    {
        var svc = new FakeServerRuleService
        {
            ThrowOnList = new ServerRuleConsentRequiredException("Ask your administrator to grant it."),
        };
        var vm = Vm(svc);

        string? blocked = null;
        vm.WriteBlockedByPermission += m => blocked = m;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(blocked);
        Assert.Contains("administrator", blocked);
    }

    // ── Edit gating (the §16 protection, surfaced in the UI) ─────────────────────

    [Theory]
    [InlineData(true, false, true)]    // fully editable, writable  → can edit
    [InlineData(false, false, false)]  // not representable         → cannot edit
    [InlineData(true, true, false)]    // read-only on the server   → cannot edit
    public async Task CanEditSelected_ReflectsEditabilityAndReadOnly(bool editable, bool readOnly, bool expected)
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha", editable: editable, readOnly: readOnly)] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.CanEditSelected);
    }

    [Fact]
    public async Task EditRule_OnNonEditableRule_IsDisabled_AndDoesNotOpenEditor()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Complex", editable: false)] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        // Edit is disabled (not pressable) for a rule we can't fully represent — the user runs with
        // announcements off, so a disabled control is clearer than a pressable one that silently fails.
        Assert.False(vm.EditRuleCommand.CanExecute(null));

        var opened = false;
        vm.EditorRequested += _ => opened = true;
        vm.EditRuleCommand.Execute(null);   // force-invoke: the defensive guard still blocks it
        Assert.False(opened);
    }

    [Fact]
    public async Task ToggleAndDelete_AreDisabled_ForServerReadOnlyRule()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("ro", "Protected", readOnly: true)] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.ToggleEnabledCommand.CanExecute(null));
        Assert.False(vm.DeleteRuleCommand.CanExecute(null));
        Assert.False(vm.EditRuleCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditRule_OnEditableRule_OpensPrefilledEditor()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha")] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        ServerRuleEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;

        vm.EditRuleCommand.Execute(null);

        Assert.NotNull(editor);
        Assert.False(editor!.IsNew);
        Assert.Equal("Alpha", editor.Name);
    }

    [Fact]
    public void CreateRule_OpensEmptyEditor()
    {
        var vm = Vm(new FakeServerRuleService());

        ServerRuleEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;

        vm.CreateRuleCommand.Execute(null);

        Assert.NotNull(editor);
        Assert.True(editor!.IsNew);
        Assert.Equal(string.Empty, editor.Name);
    }

    [Fact]
    public async Task CreateRule_OnEmptyAccount_AssignsSequence1_NotZero()
    {
        // Graph rejects sequence 0 with MessageRuleValidationError; a new rule must get a 1-based
        // sequence. On an account with no rules yet, the first new rule is sequence 1.
        var svc = new FakeServerRuleService();
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        ServerRuleEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.CreateRuleCommand.Execute(null);

        editor!.Name = "First rule";
        editor.MarkAsRead = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Contains("create", svc.Calls);
        Assert.Single(vm.Rules);
        Assert.Equal(1, vm.Rules[0].Sequence);
    }

    // ── Toggle / delete / reorder ───────────────────────────────────────────────

    [Fact]
    public async Task ToggleEnabled_FlipsStateAndCallsService()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha", enabled: true)] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.Equal(("a", false), svc.LastToggle);
        Assert.False(vm.SelectedRule!.IsEnabled);
    }

    [Fact]
    public async Task ToggleEnabled_UpdatesRowText_InPlace_ViaNotification()
    {
        // The row's accessible name is bound to ServerRuleModel.RowText, which change-notifies when
        // IsEnabled flips. So a toggle re-announces the new state WITHOUT re-inserting the row (which
        // would disturb a screen reader's focus). Re-assigning the same object into the collection
        // was a no-op for the WPF generator, which is why the row didn't refresh before.
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha", enabled: true)] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        var rule = vm.Rules[0];
        var rowTextChanged = false;
        rule.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ServerRuleModel.RowText)) rowTextChanged = true; };

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.False(rule.IsEnabled);
        Assert.True(rowTextChanged);                     // UIA gets the change the screen reader needs
        Assert.Contains("disabled", rule.RowText);
        Assert.Same(vm.Rules[0], vm.SelectedRule);       // no re-insert; selection undisturbed
        Assert.Equal("Enable", vm.ToggleEnabledLabel);   // button now offers the opposite action
    }

    [Fact]
    public async Task ToggleEnabled_IsAllowedOnRulesWeCannotEdit()
    {
        // Enable/disable only PATCHes isEnabled, so it stays safe for rules outside the subset.
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Complex", editable: false)] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.Contains("setEnabled", svc.Calls);
    }

    [Fact]
    public async Task DeleteRule_WithoutConfirmation_DoesNothing()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha")] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ConfirmDeleteRequested += (_, _) => false;

        await vm.DeleteRuleCommand.ExecuteAsync(null);

        Assert.DoesNotContain("delete", svc.Calls);
        Assert.Single(vm.Rules);
    }

    [Fact]
    public async Task DeleteRule_Confirmed_RemovesAndMovesSelection()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha"), Rule("b", "Beta")] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ConfirmDeleteRequested += (_, _) => true;

        await vm.DeleteRuleCommand.ExecuteAsync(null);

        Assert.Contains("delete", svc.Calls);
        Assert.Equal("b", Assert.Single(vm.Rules).Id);
        Assert.Equal("b", vm.SelectedRule!.Id);
    }

    [Fact]
    public async Task MoveUpDown_AreDisabled_AtTheEnds()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha"), Rule("b", "Beta"), Rule("c", "Gamma")] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        // First rule selected: can't move up.
        Assert.False(vm.CanMoveUp);
        Assert.True(vm.CanMoveDown);
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        Assert.True(vm.MoveDownCommand.CanExecute(null));

        // Last rule selected: can't move down.
        vm.SelectedRule = vm.Rules[2];
        Assert.True(vm.CanMoveUp);
        Assert.False(vm.CanMoveDown);
        Assert.False(vm.MoveDownCommand.CanExecute(null));

        // Middle: both.
        vm.SelectedRule = vm.Rules[1];
        Assert.True(vm.CanMoveUp);
        Assert.True(vm.CanMoveDown);
    }

    [Fact]
    public async Task MoveUpDown_AreDisabled_ForServerReadOnlyRule()
    {
        // A read-only rule (e.g. "Delete Pokémon messages") can't be re-sequenced by the API; Move
        // must be gated like Edit/Delete even when it's in the middle of the list.
        var svc = new FakeServerRuleService
        {
            Stored = [Rule("a", "Alpha"), Rule("ro", "Protected", readOnly: true), Rule("c", "Gamma")],
        };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SelectedRule = vm.Rules[1];   // the read-only rule, mid-list
        Assert.False(vm.CanMoveUp);
        Assert.False(vm.CanMoveDown);
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        Assert.False(vm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public async Task MoveDown_ReordersAndSendsNewOrder()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha"), Rule("b", "Beta")] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.MoveDownCommand.ExecuteAsync(null);

        Assert.Equal(["b", "a"], vm.Rules.Select(r => r.Id));
        Assert.Equal(["b", "a"], svc.LastReorder);
        // Sequence-value reassignment is the service's job (verified in GraphServerRuleServiceTests);
        // the VM only owns the visible order and the service call.
    }

    [Fact]
    public async Task Move_WhenServerRefuses_RollsBackLocalOrder()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha"), Rule("b", "Beta")] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);
        svc.ThrowOnWrite = new ServerRuleConsentRequiredException("Ask your administrator.");

        await vm.MoveDownCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b"], vm.Rules.Select(r => r.Id));  // order restored
    }

    [Fact]
    public async Task Refresh_ResolvesMoveAndCopyFolderNames_FromCachedFolders()
    {
        var rule = new ServerRuleModel
        {
            Id = "r1", DisplayName = "Filer",
            MoveToFolderId = "graph-id-move",
            CopyToFolderId = "graph-id-copy",
            SubjectContains = "x",
        };
        var svc = new FakeServerRuleService { Stored = [rule] };
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [_accountId] =
            [
                new MailFolderModel { FullName = "graph-id-move", DisplayName = "Archive" },
                new MailFolderModel { FullName = "graph-id-copy", DisplayName = "Backups" },
            ],
        };
        var vm = new ServerRulesViewModel(svc, [GraphAccount()], folders);

        await vm.RefreshCommand.ExecuteAsync(null);

        var loaded = vm.Rules.Single();
        Assert.Equal("Archive", loaded.MoveToFolderName);
        Assert.Equal("Backups", loaded.CopyToFolderName);
        Assert.Contains("move to Archive", loaded.OneLineSummary());
        Assert.Contains("copy to Backups", loaded.OneLineSummary());
    }

    [Fact]
    public void HasGraphAccount_FalseWithoutAGraphAccount()
    {
        var imap = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp, Username = "u@e.com" };
        var vm = Vm(new FakeServerRuleService(), imap);

        Assert.False(vm.HasGraphAccount);
        Assert.False(vm.ShowAccountSelector);
    }

    // ── Editor ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Editor_RequiresName()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.MarkAsRead = true;

        Assert.False(editor.Validate());
        Assert.Contains("name", editor.NameError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Editor_RequiresAtLeastOneAction()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "No actions";
        editor.SubjectContains = "x";

        Assert.False(editor.Validate());
        Assert.Contains("action", editor.ActionsError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Editor_MoveToFolderWithoutAFolder_IsInvalid()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "Filer";
        editor.MoveToFolder = true;   // checked, but no folder picked

        Assert.False(editor.Validate());
        Assert.Contains("folder", editor.FolderError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Editor_Save_RaisesSavedWithAssembledRule_AndCloses()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "  Newsletters  ";
        editor.SubjectContains = "digest";
        editor.ForwardTo = "a@b.com, c@d.com; e@f.com";
        editor.MarkAsRead = true;

        ServerRuleModel? saved = null;
        var closed = false;
        editor.Saved += r => { saved = r; return Task.FromResult<string?>(null); };   // null = success
        editor.CloseRequested += () => closed = true;

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal("Newsletters", saved!.DisplayName);      // trimmed
        Assert.Equal("digest", saved.SubjectContains);
        Assert.Equal(["a@b.com", "c@d.com", "e@f.com"], saved.ForwardTo);
        Assert.True(saved.IsFullyEditable);
        Assert.True(closed);
    }

    [Fact]
    public async Task Editor_Save_WhenOwnerReportsError_StaysOpen_AndShowsError()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "Nope";
        editor.MarkAsRead = true;

        var closed = false;
        editor.Saved += _ => Task.FromResult<string?>("Graph rejected the rule.");   // non-null = failure
        editor.CloseRequested += () => closed = true;

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);                                   // editor stays open on failure
        Assert.Equal("Graph rejected the rule.", editor.SaveError);
    }

    [Fact]
    public void Editor_ForEdit_RoundTripsFields_IncludingBodyContainsSentToAndCopy()
    {
        // Covers the fields added in #333 (bodyContains, sentToAddresses, copyToFolder) — an edit
        // must carry them through, not drop them (the §16 data-loss trap).
        var original = new ServerRuleModel
        {
            Id = "r1",
            Sequence = 3,
            DisplayName = "Alpha",
            IsEnabled = false,
            SenderContains = "boss",
            SubjectContains = "urgent",
            BodyContains = "invoice",
            SentToAddresses = ["team@contoso.com", "ops@contoso.com"],
            SentOnlyToMe = true,
            Importance = "high",
            MoveToFolderId = "folder-1",
            MoveToFolderName = "Priority",
            CopyToFolderId = "folder-2",
            CopyToFolderName = "Backups",
            StopProcessingRules = true,
            IsFullyEditable = true,
        };

        var result = ServerRuleEditorViewModel.ForEdit(original).ToModel();

        Assert.Equal("r1", result.Id);
        Assert.Equal(3, result.Sequence);
        Assert.Equal("Alpha", result.DisplayName);
        Assert.False(result.IsEnabled);
        Assert.Equal("boss", result.SenderContains);
        Assert.Equal("urgent", result.SubjectContains);
        Assert.Equal("invoice", result.BodyContains);
        Assert.Equal(["team@contoso.com", "ops@contoso.com"], result.SentToAddresses);
        Assert.True(result.SentOnlyToMe);
        Assert.Equal("high", result.Importance);
        Assert.Equal("folder-1", result.MoveToFolderId);
        Assert.Equal("folder-2", result.CopyToFolderId);
        Assert.True(result.StopProcessingRules);
    }

    [Fact]
    public void Editor_ForNew_LeavesAdvancedCollapsed()
        => Assert.False(ServerRuleEditorViewModel.ForNew().IsAdvancedExpanded);

    [Fact]
    public void Editor_ForEdit_ExpandsAdvanced_WhenRuleUsesAnAdvancedField()
    {
        // "Sender contains" lives in the Advanced section; editing a rule that uses it must open
        // Advanced so the populated field isn't hidden.
        var withAdvanced = ServerRuleEditorViewModel.ForEdit(new ServerRuleModel
        {
            Id = "r1", DisplayName = "Alpha", SenderContains = "boss", MarkAsRead = true,
        });
        Assert.True(withAdvanced.IsAdvancedExpanded);

        // A rule using only common fields keeps Advanced collapsed.
        var commonOnly = ServerRuleEditorViewModel.ForEdit(new ServerRuleModel
        {
            Id = "r2", DisplayName = "Beta", SubjectContains = "invoice", MarkAsRead = true,
        });
        Assert.False(commonOnly.IsAdvancedExpanded);
    }

    [Fact]
    public void Editor_CopyToFolderWithoutAFolder_IsInvalid()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "Copier";
        editor.CopyToFolder = true;   // checked, but no folder picked

        Assert.False(editor.Validate());
        Assert.Contains("folder", editor.FolderError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportanceOption_AnnouncesDisplayName_NotTypeName()
    {
        // Screen readers read a Selector item's name from ToString(), not DisplayMemberPath.
        var option = ServerRuleEditorViewModel.ImportanceOptions.First(o => o.Value == "high");

        Assert.Equal("High", option.ToString());
    }
}
