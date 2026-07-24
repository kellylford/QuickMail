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

        public Task ReorderAsync(Guid accountId, IReadOnlyList<string> ruleIdsInOrder, CancellationToken ct = default)
        {
            Calls.Add("reorder");
            LastReorder = ruleIdsInOrder;
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
    public async Task EditRule_OnNonEditableRule_DoesNotOpenEditor_AndExplainsWhy()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Complex", editable: false)] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        var opened = false;
        vm.EditorRequested += _ => opened = true;

        vm.EditRuleCommand.Execute(null);

        Assert.False(opened);
        Assert.Contains("Outlook", vm.StatusText);
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
    public async Task ToggleEnabled_RaisesCollectionChange_SoTheRowTextRefreshes()
    {
        // A row's announced text comes from ServerRuleModel.ToString(), which a ListView evaluates
        // once per container. The model raises no change notification, so without re-assigning the
        // slot the row would keep announcing "enabled" after the user disabled it.
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha", enabled: true)] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        var replaced = false;
        vm.Rules.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace) replaced = true;
        };

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.True(replaced);
        Assert.Contains("disabled", vm.Rules[0].ToString());
        Assert.Same(vm.Rules[0], vm.SelectedRule);   // selection survives the replace
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
    public async Task MoveDown_ReordersAndSendsNewOrder()
    {
        var svc = new FakeServerRuleService { Stored = [Rule("a", "Alpha"), Rule("b", "Beta")] };
        var vm = Vm(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.MoveDownCommand.ExecuteAsync(null);

        Assert.Equal(["b", "a"], vm.Rules.Select(r => r.Id));
        Assert.Equal(["b", "a"], svc.LastReorder);
        Assert.Equal([1, 2], vm.Rules.Select(r => r.Sequence));
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
    public void Editor_Save_RaisesSavedWithAssembledRule_AndCloses()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "  Newsletters  ";
        editor.SubjectContains = "digest";
        editor.ForwardTo = "a@b.com, c@d.com; e@f.com";
        editor.MarkAsRead = true;

        ServerRuleModel? saved = null;
        var closed = false;
        editor.Saved += r => saved = r;
        editor.CloseRequested += () => closed = true;

        editor.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal("Newsletters", saved!.DisplayName);      // trimmed
        Assert.Equal("digest", saved.SubjectContains);
        Assert.Equal(["a@b.com", "c@d.com", "e@f.com"], saved.ForwardTo);
        Assert.True(saved.IsFullyEditable);
        Assert.True(closed);
    }

    [Fact]
    public void Editor_ForEdit_RoundTripsFields()
    {
        var original = new ServerRuleModel
        {
            Id = "r1",
            Sequence = 3,
            DisplayName = "Alpha",
            IsEnabled = false,
            SenderContains = "boss",
            SubjectContains = "urgent",
            SentOnlyToMe = true,
            Importance = "high",
            MoveToFolderId = "folder-1",
            MoveToFolderName = "Priority",
            StopProcessingRules = true,
            IsFullyEditable = true,
        };

        var editor = ServerRuleEditorViewModel.ForEdit(original);
        var result = editor.ToModel();

        Assert.Equal("r1", result.Id);
        Assert.Equal(3, result.Sequence);
        Assert.Equal("Alpha", result.DisplayName);
        Assert.False(result.IsEnabled);
        Assert.Equal("boss", result.SenderContains);
        Assert.True(result.SentOnlyToMe);
        Assert.Equal("high", result.Importance);
        Assert.Equal("folder-1", result.MoveToFolderId);
        Assert.True(result.StopProcessingRules);
    }

    [Fact]
    public void ImportanceOption_AnnouncesDisplayName_NotTypeName()
    {
        // Screen readers read a Selector item's name from ToString(), not DisplayMemberPath.
        var option = ServerRuleEditorViewModel.ImportanceOptions.First(o => o.Value == "high");

        Assert.Equal("High", option.ToString());
    }
}
