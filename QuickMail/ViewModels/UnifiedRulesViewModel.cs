using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// The unified per-account rules list (spec §20.7): one account picker over ALL accounts, and one
/// merged collection of <see cref="UnifiedRuleRow"/> holding both the account's server (Microsoft 365)
/// rules and its client (QuickMail) rules. Replaces the interim two-section layout.
/// <para>
/// This first slice owns the account picker and the merged load. CRUD routing (New classifies and
/// routes; Edit/Delete/toggle go to the matching service; Move is server-only) lands on top of it.
/// </para>
/// </summary>
public partial class UnifiedRulesViewModel : ObservableObject
{
    private readonly IRuleService _clientRules;
    private readonly IServerRuleService? _serverRules;
    private readonly IReadOnlyDictionary<Guid, List<MailFolderModel>>? _foldersByAccount;
    private readonly List<AccountModel> _allAccounts;

    public UnifiedRulesViewModel(
        IRuleService clientRules,
        IServerRuleService? serverRules,
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>>? foldersByAccount = null,
        Guid? preferredAccountId = null)
    {
        _clientRules = clientRules;
        _serverRules = serverRules;
        _foldersByAccount = foldersByAccount;
        _allAccounts = accounts.ToList();

        AccountOptions = _allAccounts
            .Select(a => new AccountOption { Id = a.Id, DisplayName = a.AccountLabel })
            .ToList();

        // Land on the account the user is currently in (see ServerRulesViewModel); fall back to the
        // first account when there's no current-account context (an aggregate view at the tree top).
        _selectedAccount = AccountOptions.FirstOrDefault(o => o.Id == preferredAccountId)
                           ?? AccountOptions.FirstOrDefault();
    }

    public List<AccountOption> AccountOptions { get; }

    /// <summary>Shown only when there's a choice to make (a single account needs no picker).</summary>
    public bool ShowAccountSelector => AccountOptions.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountSupportsServerRules))]
    private AccountOption? _selectedAccount;

    public ObservableCollection<UnifiedRuleRow> Rules { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    [NotifyPropertyChangedFor(nameof(CanModifySelected))]
    [NotifyCanExecuteChangedFor(nameof(EditRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private UnifiedRuleRow? _selectedRule;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    // ── Events (the View wires these) ───────────────────────────────────────

    /// <summary>Ask the View to open the (modeless) rule editor.</summary>
    public event Action<ServerRuleEditorViewModel>? EditorRequested;

    /// <summary>Ask the View to confirm a delete (message, title) → user's yes/no.</summary>
    public event Func<string, string, bool>? ConfirmDeleteRequested;

    /// <summary>Ask the View to show the "saved as a QuickMail rule" dialog (spec §20.3).</summary>
    public event Action<string>? ClientRuleNoticeRequested;

    public event Action<string, AnnouncementCategory>? AnnouncementRequested;
    public event Action<string>? WriteBlockedByPermission;
    public event Action? FocusSelectedRuleRequested;

    // ── Gating ──────────────────────────────────────────────────────────────

    /// <summary>Edit is allowed for a client rule, or a server rule that's fully representable and not read-only.</summary>
    public bool CanEditSelected => SelectedRule is { } r
        && (r.RunsWhere == RuleRunsWhere.Client || r.Server is { IsFullyEditable: true, IsReadOnly: false });

    /// <summary>Delete/toggle need the rule to be writable (a server read-only rule can't be changed).</summary>
    public bool CanModifySelected => SelectedRule is { } r
        && (r.RunsWhere == RuleRunsWhere.Client || r.Server is { IsReadOnly: false });

    /// <summary>Reorder is server-only (client rules have no execution order) and not at the top.</summary>
    public bool CanMoveUp => SelectedRule is { RunsWhere: RuleRunsWhere.Server, Server.IsReadOnly: false }
        && ServerIndexOf(SelectedRule) > 0;

    public bool CanMoveDown => SelectedRule is { RunsWhere: RuleRunsWhere.Server, Server.IsReadOnly: false }
        && ServerIndexOf(SelectedRule) is var i && i >= 0 && i < ServerRows().Count - 1;

    private List<UnifiedRuleRow> ServerRows() => Rules.Where(r => r.RunsWhere == RuleRunsWhere.Server).ToList();
    private int ServerIndexOf(UnifiedRuleRow? row) => row is null ? -1 : ServerRows().FindIndex(r => ReferenceEquals(r, row));

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void NewRule()
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Saved += _ => SaveNewAsync(accountId, editor);
        editor.AnnouncementRequested += (t, c) => AnnouncementRequested?.Invoke(t, c);
        EditorRequested?.Invoke(editor);
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void EditRule()
    {
        if (SelectedAccount?.Id is not Guid accountId || SelectedRule is not { } row) return;

        var editor = row.RunsWhere == RuleRunsWhere.Server
            ? ServerRuleEditorViewModel.ForEdit(row.Server!)
            : ServerRuleEditorViewModel.ForEditClient(row.Client!);
        editor.AnnouncementRequested += (t, c) => AnnouncementRequested?.Invoke(t, c);
        editor.Saved += _ => row.RunsWhere == RuleRunsWhere.Server
            ? SaveEditedServerAsync(accountId, row.Server!, editor)
            : SaveEditedClientAsync(accountId, row.Client!, editor);
        EditorRequested?.Invoke(editor);
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task ToggleEnabledAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId || SelectedRule is not { } row) return;

        bool newState;
        if (row.RunsWhere == RuleRunsWhere.Server)
        {
            var rule = row.Server!;
            newState = !rule.IsEnabled;
            await RunServerWriteAsync(async () =>
            {
                await _serverRules!.SetEnabledAsync(accountId, rule.Id, newState, ct);
                rule.IsEnabled = newState;
            }, rule.Id);
        }
        else
        {
            var rule = row.Client!;
            newState = !rule.IsEnabled;
            SetClientEnabled(rule.Id, newState);
            await ReloadAndReselectAsync(clientId: rule.Id, ct: ct);
        }
        Announce(newState ? "Rule enabled." : "Rule disabled.", AnnouncementCategory.Result);
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task DeleteRuleAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId || SelectedRule is not { } row) return;

        var confirmed = ConfirmDeleteRequested?.Invoke(
            $"Delete rule '{row.Name}'? It will stop running.", "Delete Rule") ?? false;
        if (!confirmed) return;

        if (row.RunsWhere == RuleRunsWhere.Server)
            await RunServerWriteAsync(() => _serverRules!.DeleteAsync(accountId, row.Server!.Id, ct));
        else
        {
            DeleteClientRule(row.Client!.Id);
            await ReloadAndReselectAsync(ct: ct);
        }
        Announce("Rule deleted.", AnnouncementCategory.Result);
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveUpAsync(CancellationToken ct) => MoveServerAsync(-1, ct);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveDownAsync(CancellationToken ct) => MoveServerAsync(+1, ct);

    // ── Save routing ────────────────────────────────────────────────────────

    private async Task<string?> SaveNewAsync(Guid accountId, ServerRuleEditorViewModel editor)
    {
        var kind = editor.Classify(AccountSupportsServerRules);
        if (kind.IsConflict) return kind.ConflictError;   // editor shows it and stays open

        if (kind.Kind == RuleRunsWhere.Server)
        {
            var model = editor.ToModel();
            model.Sequence = ServerRows().Count + 1;       // Graph rejects sequence 0
            return await RunServerWriteAsync(
                () => _serverRules!.CreateAsync(accountId, model), reloadOnSuccess: true);
        }

        // Client rule — persist and tell the user it runs in QuickMail (spec §20.3).
        var rule = editor.ToClientRule(accountId);
        AddClientRule(rule);
        await ReloadAndReselectAsync(clientId: rule.Id);
        ClientRuleNoticeRequested?.Invoke(
            $"Saved as a QuickMail rule (it runs while QuickMail is open) — {kind.ClientReason}.");
        return null;
    }

    private async Task<string?> SaveEditedServerAsync(Guid accountId, ServerRuleModel original, ServerRuleEditorViewModel editor)
        => await RunServerWriteAsync(
            () => _serverRules!.UpdateAsync(accountId, editor.ToModel()), reloadOnSuccess: true, selectServerId: original.Id);

    private async Task<string?> SaveEditedClientAsync(Guid accountId, MailRule original, ServerRuleEditorViewModel editor)
    {
        // Editing preserves the kind: a client rule stays a client rule (spec §20.6). If the edits made
        // it un-representable as a client rule, block rather than silently convert.
        if (!editor.IsClientRepresentable)
            return "This rule can no longer run in QuickMail. Remove the conditions or actions QuickMail rules don't support.";

        var updated = editor.ToClientRule(accountId);
        updated.Id = original.Id;                          // preserve identity
        UpdateClientRule(updated);
        await ReloadAndReselectAsync(clientId: updated.Id);
        return null;
    }

    private async Task MoveServerAsync(int delta, CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId || SelectedRule?.Server is not { } rule) return;

        var order = ServerRows().Select(r => r.Server!).ToList();
        var from = order.FindIndex(r => ReferenceEquals(r, rule));
        var to = from + delta;
        if (from < 0 || to < 0 || to >= order.Count) return;
        (order[from], order[to]) = (order[to], order[from]);

        await RunServerWriteAsync(
            () => _serverRules!.ReorderAsync(accountId, order, ct), reloadOnSuccess: true, selectServerId: rule.Id);
        Announce($"Moved {(delta < 0 ? "up" : "down")}.", AnnouncementCategory.Status);
    }

    // ── Client-rule persistence (rules.json via IRuleService) ────────────────

    private void AddClientRule(MailRule rule)
    {
        var all = _clientRules.LoadRules();
        all.Add(rule);
        _clientRules.SaveRules(all);
    }

    private void UpdateClientRule(MailRule rule)
    {
        var all = _clientRules.LoadRules();
        var i = all.FindIndex(r => r.Id == rule.Id);
        if (i >= 0) all[i] = rule; else all.Add(rule);
        _clientRules.SaveRules(all);
    }

    private void DeleteClientRule(Guid id)
    {
        var all = _clientRules.LoadRules();
        all.RemoveAll(r => r.Id == id);
        _clientRules.SaveRules(all);
    }

    private void SetClientEnabled(Guid id, bool enabled)
    {
        var all = _clientRules.LoadRules();
        var rule = all.FirstOrDefault(r => r.Id == id);
        if (rule is null) return;
        rule.IsEnabled = enabled;
        _clientRules.SaveRules(all);
    }

    // ── Server-write plumbing ───────────────────────────────────────────────

    /// <summary>
    /// Runs a Graph write, translating a consent refusal into the admin-directed path and surfacing
    /// other failures instead of swallowing them. Returns null on success or a message on failure.
    /// On success it reloads the list (server ids change) and re-selects the target.
    /// </summary>
    private async Task<string?> RunServerWriteAsync(
        Func<Task> write, string? selectServerId = null, bool reloadOnSuccess = true)
    {
        IsBusy = true;
        try
        {
            await write();
            if (reloadOnSuccess) await ReloadAndReselectAsync(serverId: selectServerId);
            return null;
        }
        catch (ServerRuleConsentRequiredException ex)
        {
            StatusText = ex.Message;
            WriteBlockedByPermission?.Invoke(ex.Message);
            LogService.Log("UnifiedRules: blocked by missing permission");
            return ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message.Contains("ErrorNotSupportedMessageRule", StringComparison.OrdinalIgnoreCase)
                ? "This rule can't be changed from QuickMail — edit it in Outlook."
                : ex.Message;
            LogService.Log("UnifiedRules: server write failed", ex);
            return StatusText;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Reloads the account's rules and re-selects a rule by server id or client id, then
    /// asks the View to return focus to it.</summary>
    private async Task ReloadAndReselectAsync(string? serverId = null, Guid? clientId = null, CancellationToken ct = default)
    {
        await RefreshAsync(ct);
        SelectedRule = Rules.FirstOrDefault(r =>
            (serverId != null && r.Server?.Id == serverId) ||
            (clientId != null && r.Client?.Id == clientId));
        if (SelectedRule is not null) FocusSelectedRuleRequested?.Invoke();
    }

    private void Announce(string text, AnnouncementCategory category) => AnnouncementRequested?.Invoke(text, category);

    /// <summary>
    /// True when the selected account is a Microsoft 365 (Graph) account, so it can carry server-side
    /// rules. Drives which rules load, and (later) how a New rule is classified/routed.
    /// </summary>
    public bool AccountSupportsServerRules
        => _serverRules != null && SelectedAccountModel?.BackendKind == BackendKind.MicrosoftGraph;

    private AccountModel? SelectedAccountModel
        => _allAccounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id);

    partial void OnSelectedAccountChanged(AccountOption? value) => _ = RefreshCommand.ExecuteAsync(null);

    /// <summary>
    /// Loads the selected account's rules into one list: server rules first (in execution order),
    /// then client rules. A server-load failure never hides the client rules — they load in their own
    /// scope (the standard fetch pattern in ARCHITECTURE.md).
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId)
        {
            Rules.Clear();
            StatusText = string.Empty;
            return;
        }

        IsBusy = true;
        try
        {
            var rows = new List<UnifiedRuleRow>();

            // Server rules — Graph accounts only. Isolated so a Graph/network failure still lets the
            // client rules below load.
            if (AccountSupportsServerRules && _serverRules is not null)
            {
                try
                {
                    var server = await _serverRules.ListAsync(accountId, ct);
                    rows.AddRange(server.Select(UnifiedRuleRow.ForServer));
                }
                catch (Exception ex)
                {
                    StatusText = $"Couldn't load server rules: {ex.Message}";
                    LogService.Log("UnifiedRules: server load failed", ex);
                }
            }

            // Client rules for this account (per-account since #364).
            try
            {
                var client = _clientRules.LoadRules().Where(r => r.AccountId == accountId);
                rows.AddRange(client.Select(UnifiedRuleRow.ForClient));
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't load QuickMail rules: {ex.Message}";
                LogService.Log("UnifiedRules: client load failed", ex);
            }

            Rules.Clear();
            foreach (var row in rows) Rules.Add(row);
            if (string.IsNullOrEmpty(StatusText) || !IsBusy) StatusText = BuildStatus(rows);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildStatus(IReadOnlyList<UnifiedRuleRow> rows)
    {
        if (rows.Count == 0) return "No rules for this account.";
        var server = rows.Count(r => r.RunsWhere == RuleRunsWhere.Server);
        var client = rows.Count(r => r.RunsWhere == RuleRunsWhere.Client);
        return $"{rows.Count} rule{(rows.Count == 1 ? "" : "s")}: {server} on server, {client} in QuickMail.";
    }
}
