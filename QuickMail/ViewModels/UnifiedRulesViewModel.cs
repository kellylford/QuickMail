using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
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

    // Messages selected in the main window, for Test Rule (parity with RulesManagerViewModel). Null when
    // the manager was opened without a message-list selection.
    private readonly IReadOnlyList<MailMessageSummary>? _selectedMessagesForTest;

    public UnifiedRulesViewModel(
        IRuleService clientRules,
        IServerRuleService? serverRules,
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>>? foldersByAccount = null,
        Guid? preferredAccountId = null,
        IEnumerable<MailMessageSummary>? selectedMessagesForTest = null)
    {
        _clientRules = clientRules;
        _serverRules = serverRules;
        _selectedMessagesForTest = selectedMessagesForTest?.ToList();
        _foldersByAccount = foldersByAccount;
        _allAccounts = accounts.ToList();

        AccountOptions = _allAccounts
            .Select(a => new AccountOption { Id = a.Id, DisplayName = a.AccountLabel })
            .ToList();

        // Land on the account the user is currently in; fall back to the first account when there's
        // no current-account context (an aggregate view at the tree top).
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
    [NotifyPropertyChangedFor(nameof(CanTestSelected))]
    [NotifyPropertyChangedFor(nameof(ToggleEnabledLabel))]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyCanExecuteChangedFor(nameof(EditRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestRuleCommand))]
    private UnifiedRuleRow? _selectedRule;

    /// <summary>Enable/Disable button text: "Enable" for a disabled rule, "Disable" for an enabled one.</summary>
    public string ToggleEnabledLabel => SelectedRule?.IsEnabled == true ? "Disable" : "Enable";

    /// <summary>Detail prose for the selected rule (empty when nothing is selected).</summary>
    public string DetailText => SelectedRule?.DetailText ?? string.Empty;

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
    private void NewRule() => OpenNewEditor(ServerRuleEditorViewModel.ForNew());

    /// <summary>Open the New-rule editor prefilled from a message (Ctrl+Shift+T). The prefilled rule
    /// classifies server/client on save exactly like a hand-made New rule.</summary>
    public void NewRuleFromTemplate(MailRule template)
    {
        // Land on the message's account so the rule is scoped to it (the picker may be on another).
        if (template.AccountId is Guid tid && AccountOptions.FirstOrDefault(o => o.Id == tid) is { } opt)
            SelectedAccount = opt;
        OpenNewEditor(ServerRuleEditorViewModel.ForNewFromTemplate(template));
    }

    private void OpenNewEditor(ServerRuleEditorViewModel editor)
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        editor.Saved += _ => SaveNewAsync(accountId, editor);
        editor.AnnouncementRequested += (t, c) => AnnouncementRequested?.Invoke(t, c);
        EditorRequested?.Invoke(editor);
    }

    /// <summary>Ask the owner to run client rules over already-cached mail (it owns the local store),
    /// returning how many messages were moved/deleted. The Guid is the account to scope the run to —
    /// this window is one account at a time, so it runs only the account in the picker, not every
    /// account (#493). Server rules run server-side, so this is client-only — same as the standalone
    /// manager (#346).</summary>
    public event Func<Guid?, Task<int>>? RunOnExistingRequested;

    /// <summary>Run on Existing applies the selected account's ENABLED client rules to its Inbox. Disabled
    /// when that account has none — a Graph account whose rules are all server-side has nothing for this
    /// to do, and a greyed control is clearer than one that reports "0 moved" (and matches Test/Edit
    /// gating). Server rules can't be run against existing mail via Graph, so they never count here.</summary>
    public bool CanRunOnExisting =>
        SelectedAccount != null && Rules.Any(r => r.RunsWhere == RuleRunsWhere.Client && r.Client!.IsEnabled);

    [RelayCommand(CanExecute = nameof(CanRunOnExisting))]
    private async Task RunOnExistingAsync()
    {
        if (RunOnExistingRequested is null || SelectedAccount is not { } account) return;
        // Set StatusText as well as announcing (parity with RulesManagerViewModel): the status line is a
        // visible, F6-reachable surface, so a user running with announcements off still sees the outcome —
        // otherwise this newly-surfaced button would be imperceptible to them, error included.
        StatusText = $"Running {account.DisplayName}'s rules on existing mail…";
        Announce(StatusText, AnnouncementCategory.Status);
        int affected;
        try
        {
            affected = await RunOnExistingRequested.Invoke(account.Id);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't run rules on existing mail: {ex.Message}";
            Announce(StatusText, AnnouncementCategory.Result);
            return;
        }
        StatusText = affected > 0
            ? $"Applied {account.DisplayName}'s rules to existing mail: {affected} message{(affected == 1 ? "" : "s")} moved or deleted."
            : $"Applied {account.DisplayName}'s rules to existing mail.";
        Announce(StatusText, AnnouncementCategory.Result);
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
        string? error = null;
        if (row.RunsWhere == RuleRunsWhere.Server)
        {
            var rule = row.Server!;
            newState = !rule.IsEnabled;
            error = await RunServerWriteAsync(async () =>
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
        // A failed Graph write returns its message; don't announce success over it.
        Announce(error ?? (newState ? "Rule enabled." : "Rule disabled."), AnnouncementCategory.Result);
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task DeleteRuleAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId || SelectedRule is not { } row) return;

        var confirmed = ConfirmDeleteRequested?.Invoke(
            $"Delete rule '{row.Name}'? It will stop running.", "Delete Rule") ?? false;
        if (!confirmed) return;

        // Remember where the deleted row sat so focus lands on a neighbour, not nowhere.
        var index = Rules.IndexOf(row);

        string? error = null;
        if (row.RunsWhere == RuleRunsWhere.Server)
            // Reload ourselves (with the fallback index) rather than letting the write re-select by a
            // now-gone id and strand the selection.
            error = await RunServerWriteAsync(
                () => _serverRules!.DeleteAsync(accountId, row.Server!.Id, ct), reloadOnSuccess: false);
        else
            DeleteClientRule(row.Client!.Id);

        if (error is null) await ReloadAndReselectAsync(fallbackIndex: index, ct: ct);
        Announce(error ?? "Rule deleted.", AnnouncementCategory.Result);
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveUpAsync(CancellationToken ct) => MoveServerAsync(-1, ct);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveDownAsync(CancellationToken ct) => MoveServerAsync(+1, ct);

    /// <summary>Test applies only to a CLIENT rule: a server rule runs in Exchange, so there is nothing
    /// local to test it against. Disabled for a server row (and when nothing is selected), consistent
    /// with how Edit/Delete/Move disable for a read-only server rule — a greyed control beats one that,
    /// for a user running with announcements off, does nothing perceptible when pressed.</summary>
    public bool CanTestSelected => SelectedRule is { RunsWhere: RuleRunsWhere.Client };

    // Parity with RulesManagerViewModel.TestRule: run the selected client rule against the messages
    // currently in the main window and report the match count.
    [RelayCommand(CanExecute = nameof(CanTestSelected))]
    private void TestRule()
    {
        if (SelectedRule is not { RunsWhere: RuleRunsWhere.Client } row) return;   // defensive; CanExecute gates it

        var messages = _selectedMessagesForTest?.ToList() ?? [];
        if (messages.Count == 0)
        {
            StatusText = "No messages selected in the main window.";
            Announce(StatusText, AnnouncementCategory.Result);
            return;
        }

        var matched = _clientRules.TestRule(row.Client!, messages);
        StatusText = $"Rule would match {matched.Count} of {messages.Count} selected messages.";
        Announce(StatusText, AnnouncementCategory.Result);
    }

    // ── Save routing ────────────────────────────────────────────────────────

    private async Task<string?> SaveNewAsync(Guid accountId, ServerRuleEditorViewModel editor)
    {
        var kind = editor.Classify(AccountSupportsServerRules);
        if (kind.IsConflict) return kind.ConflictError;   // editor shows it and stays open

        if (kind.Kind == RuleRunsWhere.Server)
        {
            var model = editor.ToModel();
            model.Sequence = ServerRows().Count + 1;       // Graph rejects sequence 0
            // Re-select the created rule — its id only exists on CreateAsync's return, so route
            // through the value-returning overload (otherwise nothing is selected and focus strands).
            return await RunServerWriteAsync(
                () => _serverRules!.CreateAsync(accountId, model),
                created => created.Id, reloadOnSuccess: true);
        }

        // Client rule — persist and tell the user it runs in QuickMail (spec §20.3).
        var rule = editor.ToClientRule(accountId);
        AddClientRule(rule);
        // Show the notice BEFORE re-selecting: ReloadAndReselect posts a focus move to the new row,
        // and if that's pending when the modal notice opens, focus jumps into the background window
        // while the notice is up. Raising the (modal) notice first means the reselect + focus request
        // run only once it's dismissed.
        ClientRuleNoticeRequested?.Invoke(
            $"Saved as a QuickMail rule (it runs while QuickMail is open) — {kind.ClientReason}.");
        await ReloadAndReselectAsync(clientId: rule.Id);
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

        var error = await RunServerWriteAsync(
            () => _serverRules!.ReorderAsync(accountId, order, ct), reloadOnSuccess: true, selectServerId: rule.Id);
        // An action outcome is a Result, not background Status (which users can silence).
        Announce(error ?? $"Moved {(delta < 0 ? "up" : "down")}.", AnnouncementCategory.Result);
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
    private Task<string?> RunServerWriteAsync(
        Func<Task> write, string? selectServerId = null, bool reloadOnSuccess = true)
        => RunServerWriteAsync<object?>(
            async () => { await write(); return null; },
            _ => selectServerId, reloadOnSuccess);

    /// <summary>
    /// As above, for a write that returns a value — e.g. <see cref="IServerRuleService.CreateAsync"/>
    /// hands back the created rule, whose new id is the only place to learn what to re-select.
    /// <paramref name="selectIdFromResult"/> maps the result to the server id to land on.
    /// </summary>
    private async Task<string?> RunServerWriteAsync<T>(
        Func<Task<T>> write, Func<T, string?> selectIdFromResult, bool reloadOnSuccess = true)
    {
        IsBusy = true;
        try
        {
            var result = await write();
            if (reloadOnSuccess) await ReloadAndReselectAsync(serverId: selectIdFromResult(result));
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
    private async Task ReloadAndReselectAsync(
        string? serverId = null, Guid? clientId = null, int? fallbackIndex = null, CancellationToken ct = default)
    {
        await RefreshAsync(ct);
        SelectedRule = Rules.FirstOrDefault(r =>
            (serverId != null && r.Server?.Id == serverId) ||
            (clientId != null && r.Client?.Id == clientId));
        // Nothing matched (e.g. after a delete — the id is gone): land on the neighbour that took the
        // deleted row's slot, so focus and the enabled/disabled buttons don't strand.
        if (SelectedRule is null && fallbackIndex is int idx && Rules.Count > 0)
            SelectedRule = Rules[Math.Clamp(idx, 0, Rules.Count - 1)];
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

    partial void OnSelectedAccountChanged(AccountOption? value)
        => RefreshCommand.ExecuteAsync(null).LogFaults("UnifiedRules: account-change refresh");

    /// <summary>
    /// Loads the selected account's rules into one list: server rules first (in execution order),
    /// then client rules. A server-load failure never hides the client rules — they load in their own
    /// scope (the standard fetch pattern in ARCHITECTURE.md).
    /// </summary>
    private CancellationTokenSource? _refreshCts;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId)
        {
            Rules.Clear();
            StatusText = string.Empty;
            OnPropertyChanged(nameof(CanRunOnExisting));
            RunOnExistingCommand.NotifyCanExecuteChanged();
            return;
        }

        // Supersede any in-flight refresh so two loads can't interleave (arrow through the account
        // picker on a slow connection and the list could end up showing a mix), and a load can't
        // outlive the window (the View cancels this on close via CancelPendingLoad).
        var previous = _refreshCts;
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        previous?.Cancel();
        previous?.Dispose();
        var token = _refreshCts.Token;

        IsBusy = true;
        try
        {
            var rows = new List<UnifiedRuleRow>();
            var failures = new List<string>();

            // Server rules — Graph accounts only. Isolated so a Graph/network failure still lets the
            // client rules below load.
            if (AccountSupportsServerRules && _serverRules is not null)
            {
                try
                {
                    var server = await _serverRules.ListAsync(accountId, token);
                    rows.AddRange(server.Select(UnifiedRuleRow.ForServer));
                }
                catch (OperationCanceledException) { return; }   // superseded — leave state untouched
                catch (Exception ex)
                {
                    failures.Add($"Couldn't load server rules: {ex.Message}");
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
                failures.Add($"Couldn't load QuickMail rules: {ex.Message}");
                LogService.Log("UnifiedRules: client load failed", ex);
            }

            if (token.IsCancellationRequested) return;   // a newer refresh (or a close) won the race

            Rules.Clear();
            foreach (var row in rows) Rules.Add(row);

            // Run on Existing enables/disables with the account's enabled client-rule set, which just
            // changed. (Every write path — toggle, add, delete, account switch — routes through here.)
            OnPropertyChanged(nameof(CanRunOnExisting));
            RunOnExistingCommand.NotifyCanExecuteChanged();

            // Open on an actionable, readable first rule: select it so the detail pane populates and
            // the list has a selection (re-selection after a write is handled by ReloadAndReselect).
            if (SelectedRule is null || !Rules.Contains(SelectedRule))
                SelectedRule = Rules.FirstOrDefault();

            // A load failure must survive to the status line — otherwise "couldn't reach Graph" reads
            // as "this account has no server rules", which invites the wrong next action.
            StatusText = BuildStatus(rows, failures);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Cancels any in-flight load. The View calls this on close so a slow Graph fetch can't
    /// complete and write into a window that's gone.</summary>
    public void CancelPendingLoad() => _refreshCts?.Cancel();

    private static string BuildStatus(List<UnifiedRuleRow> rows, List<string> failures)
    {
        if (failures.Count > 0)
        {
            // Lead with what went wrong; append counts only for the section(s) that did load.
            var loaded = rows.Count == 0 ? "" : " " + Counts(rows);
            return string.Join(" ", failures) + loaded;
        }
        return rows.Count == 0 ? "No rules for this account." : Counts(rows);

        static string Counts(List<UnifiedRuleRow> r)
        {
            var server = r.Count(x => x.RunsWhere == RuleRunsWhere.Server);
            var client = r.Count(x => x.RunsWhere == RuleRunsWhere.Client);
            return $"{r.Count} rule{(r.Count == 1 ? "" : "s")}: {server} on server, {client} in QuickMail.";
        }
    }
}
