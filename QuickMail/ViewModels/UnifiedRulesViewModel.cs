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

    // The main window's whole message list as it was when the Rules Manager opened, for Test Rule —
    // not the selection. Null when there was no list.
    private readonly IReadOnlyList<MailMessageSummary>? _selectedMessagesForTest;

    // "Show field labels in the rules list" — read once at open; a
    // Settings change is picked up next time the manager opens.
    private readonly bool _showFieldLabels;

    // Set when the Rules Manager was opened from a shared mailbox, which the account list leaves out
    // (#678). The status line says so until the user chooses an account themselves.
    private string? _sharedMailboxLabel;

    // Opened from a shared mailbox, the title names the account shown for the life of the window.
    private readonly bool _openedFromSharedMailbox;

    // Every shared mailbox's label, so a rule template for one can say why it gets no rule.
    private readonly Dictionary<Guid, string> _sharedAccountLabels;

    public UnifiedRulesViewModel(
        IRuleService clientRules,
        IServerRuleService? serverRules,
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>>? foldersByAccount = null,
        Guid? preferredAccountId = null,
        IEnumerable<MailMessageSummary>? selectedMessagesForTest = null,
        IConfigService? configService = null)
    {
        _clientRules = clientRules;
        _serverRules = serverRules;
        _selectedMessagesForTest = selectedMessagesForTest?.ToList();
        _showFieldLabels = configService?.Load().RuleListShowFieldLabels ?? false;
        _foldersByAccount = foldersByAccount;
        // Shared mailboxes are left out (#678). QuickMail cannot reach a shared mailbox's server-side
        // rules, and a client-side rule there would act, from one person's machine, on mail everyone
        // else reads; its rules belong in Outlook.
        var everyAccount = accounts.ToList();
        _allAccounts = everyAccount.Where(a => !a.IsShared).ToList();
        _sharedAccountLabels = everyAccount.Where(a => a.IsShared)
            .GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().AccountLabel);
        if (preferredAccountId is Guid opened
            && everyAccount.FirstOrDefault(a => a.Id == opened) is { IsShared: true } shared)
        {
            _sharedMailboxLabel = shared.AccountLabel;
            _openedFromSharedMailbox = true;
        }

        AccountOptions = _allAccounts
            .Select(a => new AccountOption { Id = a.Id, DisplayName = a.AccountLabel })
            .ToList();

        // Land on the account the user is currently in. With no current-account context (a view that
        // spans accounts, such as All Inboxes) fall back to the account marked default in Account
        // Manager — the one new rules were created for before the rules windows were unified — and
        // only then to the first. The picker decides the account a new rule belongs to.
        var defaultAccountId = _allAccounts.FirstOrDefault(a => a.IsDefault)?.Id;
        _selectedAccount = AccountOptions.FirstOrDefault(o => o.Id == preferredAccountId)
                           ?? AccountOptions.FirstOrDefault(o => o.Id == defaultAccountId)
                           ?? AccountOptions.FirstOrDefault();
    }

    public List<AccountOption> AccountOptions { get; }

    /// <summary>Shown only when there's a choice to make (a single account needs no picker).</summary>
    public bool ShowAccountSelector => AccountOptions.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountSupportsServerRules))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
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
        if (template.AccountId is Guid tid)
        {
            // An account the list does not hold, such as a shared mailbox (#678), gets no rule. Opening the
            // editor anyway would scope the rule to whichever account the picker happens to show.
            if (AccountOptions.FirstOrDefault(o => o.Id == tid) is not { } opt)
            {
                // Say why, on the status line (read on demand, and there whether announcements are on or
                // off), rather than coming forward with nothing. The window's account list is taken when
                // it opens, so an account added since is the other way to get here.
                StatusText = _sharedAccountLabels.TryGetValue(tid, out var shared)
                    ? $"Rules for the shared mailbox {shared} are managed in Outlook."
                    : "The Rules Manager does not list that message's account. Close it and open it again to make a rule there.";
                // Paired with a result announcement, as Test and Run on Existing are: the window comes forward
                // with focus where it was, so nothing else would tell anyone who hears results.
                Announce(StatusText, AnnouncementCategory.Result);
                return;
            }
            SelectedAccount = opt;
        }
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
        // Set StatusText as well as announcing: the status line is a visible, F6-reachable surface, so a
        // user running with announcements off still gets the outcome — error included — rather than a
        // button that appears to do nothing.
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

    // Run the selected client rule against the messages currently in the main window and report the
    // match count.
    [RelayCommand(CanExecute = nameof(CanTestSelected))]
    private void TestRule()
    {
        if (SelectedRule is not { RunsWhere: RuleRunsWhere.Client } row) return;   // defensive; CanExecute gates it

        var list = _selectedMessagesForTest?.ToList() ?? [];
        if (list.Count == 0)
        {
            StatusText = "The message list is empty, so there is nothing to test the rule against.";
            Announce(StatusText, AnnouncementCategory.Result);
            return;
        }

        // A client rule acts only on its own account's mail, so it is tested only against that (#687). The
        // list can hold several accounts' messages: All Inboxes, or a shared mailbox's folder, which opens
        // this window on another account (#678).
        var accountId = row.Client!.AccountId ?? SelectedAccount?.Id;
        // Named from the account being tested, not the list's selection, which can already be moving to
        // another account. "for the … account" rather than "from …": an account with no name of its own is
        // labelled by its address, and "messages from me@example.com" would read as a sender.
        var account = AccountPhrase(AccountOptions.FirstOrDefault(o => o.Id == accountId)?.DisplayName ?? SelectedAccount?.DisplayName);
        var messages = list.Where(m => m.AccountId == accountId).ToList();
        if (messages.Count == 0)
        {
            StatusText = $"The message list has no messages for {account}, so there is nothing to test the rule against.";
            Announce(StatusText, AnnouncementCategory.Result);
            return;
        }

        var matched = _clientRules.TestRule(row.Client!, messages).Count;
        StatusText = messages.Count == 1
            ? $"Rule would {(matched == 1 ? "" : "not ")}match the only message in the list for {account}."
            : $"Rule would match {matched} of the {messages.Count} messages in the list for {account}.";
        Announce(StatusText, AnnouncementCategory.Result);
    }

    /// <summary>"the Work account", for Test's result. A label that already ends in "account" does not get a
    /// second one: "the Work Account", not "the Work Account account".</summary>
    internal static string AccountPhrase(string? label)
    {
        var name = label?.Trim();
        if (string.IsNullOrEmpty(name)) return "this account";
        return name.EndsWith("account", StringComparison.OrdinalIgnoreCase) ? $"the {name}" : $"the {name} account";
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

        // Client rule — persist. Whether we say so depends on the account (#550):
        //  • On an account that supports server rules, a rule landing client-side is a surprise — the
        //    status line says this account *also* runs rules in the cloud, yet this one won't (it uses a
        //    client-only action like Mark as unread). So announce it. A non-blocking Announce (Result —
        //    honors AnnounceResults), not the old modal: the rules window is now the one every account
        //    uses, and a focus-stealing dialog on each such save doesn't belong there.
        //  • On a client-only account (IMAP / personal Graph) every rule is a client-side rule, and the
        //    status line says so on every load — "N rules, client-side only", or the mode outright when
        //    there are none — so a per-save notice would just be chatter. Stay silent.
        var rule = editor.ToClientRule(accountId);
        AddClientRule(rule);
        if (AccountSupportsServerRules)
            Announce("Saving as a client-side rule.", AnnouncementCategory.Result);
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
            return "This rule can no longer run as a client-side rule. Remove the conditions or actions client-side rules don't support.";

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
        await RefreshCoreAsync(ct);
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
    /// True when the selected account is a <em>work or school</em> Microsoft 365 (Graph) account, so it
    /// can carry server-side rules. Drives which rules load, and how a New rule is classified/routed.
    ///
    /// Personal Microsoft (Graph) accounts are excluded (#541): server rules run on
    /// <c>MailboxSettings.ReadWrite</c>, which <see cref="OAuthService.GraphMailScopesPersonal"/>
    /// deliberately omits as an org-only capability — so offering them to a personal account only
    /// yields a 403 that surfaces as a meaningless "ask your administrator" for a mailbox with no admin.
    /// Personal accounts use client-side rules only, the same as any non-server-rules account.
    ///
    /// Uses <see cref="OAuthService.ResolveIsPersonalMicrosoftAccount"/> (flag, else the domain guess),
    /// the same resolution scope selection uses, so an undetected personal account is caught too.
    /// </summary>
    public bool AccountSupportsServerRules
        => _serverRules != null
           && SelectedAccountModel is { } acct
           && SupportsServerRules(acct);

    /// <summary>
    /// Whether <paramref name="account"/> is the kind that can carry server-side rules: a
    /// <em>work or school</em> Microsoft 365 (Graph) account. Personal Graph accounts are excluded
    /// for the reason spelt out on <see cref="AccountSupportsServerRules"/>.
    /// <para>
    /// The pure per-account capability test, factored out so it reads the same everywhere and
    /// <see cref="AccountSupportsServerRules"/> (which also requires a live server-rule service) is its
    /// one caller today. Since #550 there is a single rules window for every account, so the old
    /// window-chooser that this once had to agree with is gone — but keeping the capability question in
    /// one named place is still the right shape.
    /// </para>
    /// </summary>
    public static bool SupportsServerRules(AccountModel account)
        => account.BackendKind == BackendKind.MicrosoftGraph
           && !OAuthService.ResolveIsPersonalMicrosoftAccount(account);

    private AccountModel? SelectedAccountModel
        => _allAccounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id);

    partial void OnSelectedAccountChanged(AccountOption? value)
    {
        // Choosing an account answers the shared-mailbox notice: it was about where the window opened.
        _sharedMailboxLabel = null;
        RefreshCommand.ExecuteAsync(null).LogFaults("UnifiedRules: account-change refresh");
    }

    /// <summary>
    /// Loads the selected account's rules into one list: server rules first (in execution order),
    /// then client rules. A server-load failure never hides the client rules — they load in their own
    /// scope (the standard fetch pattern in ARCHITECTURE.md).
    /// </summary>
    private CancellationTokenSource? _refreshCts;

    // The account-context refresh (initial open + every account switch, via RefreshCommand).
    [RelayCommand]
    private Task RefreshAsync(CancellationToken ct) => RefreshCoreAsync(ct);

    private async Task RefreshCoreAsync(CancellationToken ct)
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
            // Which half failed, so the status line counts only what actually loaded (#679).
            var serverFailed = false;
            var clientFailed = false;

            // Server rules — Graph accounts only. Isolated so a Graph/network failure still lets the
            // client rules below load.
            if (AccountSupportsServerRules && _serverRules is not null)
            {
                try
                {
                    var server = await _serverRules.ListAsync(accountId, token);
                    // Graph returns folder ids, not names; resolve them from the folder cache so the
                    // prose reads "move to Deleted Items" rather than "another folder". Only fill an
                    // empty name — a name the editor already set is left as-is.
                    foreach (var r in server)
                    {
                        if (string.IsNullOrWhiteSpace(r.MoveToFolderName))
                            r.MoveToFolderName = ResolveFolderName(accountId, r.MoveToFolderId);
                        if (string.IsNullOrWhiteSpace(r.CopyToFolderName))
                            r.CopyToFolderName = ResolveFolderName(accountId, r.CopyToFolderId);
                    }
                    rows.AddRange(server.Select(r => UnifiedRuleRow.ForServer(r, _showFieldLabels)));
                }
                catch (OperationCanceledException) { return; }   // superseded — leave state untouched
                catch (Exception ex)
                {
                    serverFailed = true;
                    failures.Add($"Couldn't load server-side rules: {ex.Message}");
                    LogService.Log("UnifiedRules: server load failed", ex);
                }
            }

            // Client rules for this account (per-account since #364).
            try
            {
                var client = _clientRules.LoadRules().Where(r => r.AccountId == accountId);
                // Resolve a folder name only for a Graph account, whose TargetFolder is an opaque id.
                // An IMAP TargetFolder is already the readable folder path, and resolving it would return
                // the leaf DisplayName — collapsing "Work/Archive" and "Personal/Archive" to the same
                // "Archive" — so leave IMAP rules to render their raw path (the ForClient fallback).
                var isGraphAccount =
                    _allAccounts.FirstOrDefault(a => a.Id == accountId)?.BackendKind == BackendKind.MicrosoftGraph;
                rows.AddRange(client.Select(r => UnifiedRuleRow.ForClient(r, _showFieldLabels,
                    isGraphAccount && r.Action == RuleAction.MoveToFolder
                        ? ResolveFolderName(accountId, r.TargetFolder) : null,
                    targetIsOpaque: isGraphAccount)));
            }
            catch (Exception ex)
            {
                clientFailed = true;
                failures.Add($"Couldn't load client-side rules: {ex.Message}");
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
            StatusText = SharedMailboxPreamble()
                + BuildStatus(rows, failures, AccountSupportsServerRules, serverFailed, clientFailed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Cancels any in-flight load. The View calls this on close so a slow Graph fetch can't
    /// complete and write into a window that's gone.</summary>
    public void CancelPendingLoad() => _refreshCts?.Cancel();

    /// <summary>
    /// The human-readable name for a folder id in <paramref name="accountId"/>'s folder cache, or null
    /// when it can't be resolved (no cache for the account, or the id isn't in it). Both a Graph server
    /// rule (<see cref="ServerRuleModel.MoveToFolderId"/>) and a Graph client rule
    /// (<see cref="MailRule.TargetFolder"/>) store an opaque id (e.g. "AQMkAD…"); looking it up here lets
    /// the rule prose read "move to Deleted Items" rather than the id. Null lets the caller keep whatever
    /// fallback prose it has. Callers pass only Graph folder ids: an IMAP TargetFolder is already the
    /// readable path and the client-row builder deliberately does not resolve it (see there).
    /// </summary>
    private string? ResolveFolderName(Guid accountId, string? folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId)
            || _foldersByAccount is null
            || !_foldersByAccount.TryGetValue(accountId, out var folders))
            return null;
        var match = folders.FirstOrDefault(f => f.FullName == folderId);
        return string.IsNullOrWhiteSpace(match?.DisplayName) ? null : match.DisplayName;
    }

    /// <summary>
    /// What kinds of rule this account can hold, said outright.
    /// <para>
    /// This used to be an <see cref="AnnouncementCategory.Hint"/> spoken on every account-context load,
    /// which meant arrowing down the account picker spoke a sentence per account it passed through
    /// (#550). The status line is an F6 stop, deliberately read on demand rather than made a live
    /// region, so putting it here keeps the information available without pushing it at anyone.
    /// </para>
    /// </summary>
    /// <remarks>Names its subject rather than opening with "It": the clause is appended to a count and
    /// to a load failure, and after "Couldn't load server-side rules: …" the nearest noun an "It" could attach
    /// to is <em>server rules</em>.</remarks>
    internal static string ModeClause(bool supportsServerRules)
        => supportsServerRules
            ? "This account supports server-side and client-side rules."
            : "This account supports client-side rules only.";

    /// <summary>The status line for an account with no rules: there are none to count, so the mode is
    /// the whole message.</summary>
    internal static string NoRulesStatus(bool supportsServerRules)
        => "No rules yet. " + ModeClause(supportsServerRules);

    /// <summary>
    /// The window's title. Opened from a shared mailbox it names the account shown (#678): with one account
    /// there is no Account list to focus, and focus lands on that account's first rule, so the title is
    /// what says whose rules these are as the window opens.
    /// </summary>
    public string WindowTitle => _openedFromSharedMailbox && SelectedAccount is { } shown
        ? $"Rules Manager — {shown.DisplayName}"
        : "Rules Manager";

    /// <summary>
    /// The sentence that opens the status line when the Rules Manager was opened from a shared mailbox
    /// (#678): its rules are managed in Outlook, and the window is showing another account instead.
    /// Empty otherwise, and once the user has chosen an account.
    /// </summary>
    internal string SharedMailboxPreamble()
        => _sharedMailboxLabel is { } label
            ? $"Rules for the shared mailbox {label} are managed in Outlook. Showing {SelectedAccount?.DisplayName} instead. "
            : string.Empty;

    /// <summary>Ends a fragment with a full stop so the next sentence can be appended to it. Exception
    /// messages are the input here and are inconsistent about their own punctuation.</summary>
    private static string Terminated(string fragment)
    {
        var t = fragment.TrimEnd();
        return t.Length == 0 || t[^1] is '.' or '!' or '?' ? t : t + ".";
    }

    internal static string BuildStatus(List<UnifiedRuleRow> rows, List<string> failures, bool supportsServerRules,
        bool serverLoadFailed = false, bool clientLoadFailed = false)
    {
        if (failures.Count > 0)
        {
            // Lead with what went wrong, then the counts for the section(s) that did load. With nothing
            // loaded there is nothing to count, so state the mode instead — otherwise a failed load on a
            // client-only account and one on a server-capable account produce the same sentence, and the
            // window has no other surface that tells them apart. Each failure is terminated first: an
            // exception message rarely ends in a full stop, and gluing the next sentence onto it gives
            // one run-on with no break to read.
            // One half failed on an account that has both: count the half that loaded, even when it holds
            // none, so its count is stated rather than left to be inferred. "No client-side rules." is only as
            // sure as the load behind it: RuleService.LoadRules returns an empty list for a rules file it
            // cannot read, so that case arrives here as loaded and empty.
            var oneHalfFailed = supportsServerRules && serverLoadFailed != clientLoadFailed;
            var loaded = oneHalfFailed
                ? " " + LoadedCount(rows, supportsServerRules, serverLoadFailed)
                : rows.Count == 0
                    ? " " + ModeClause(supportsServerRules)
                    : " " + Counts(rows, supportsServerRules);
            return string.Join(" ", failures.Select(Terminated)) + loaded;
        }
        return rows.Count == 0 ? NoRulesStatus(supportsServerRules) : Counts(rows, supportsServerRules);

        // After one half failed to load, count only the half that did (#679). "0 on server" straight after
        // "Couldn't load server-side rules" reads as "this account has none" — the very misreading the failure
        // text is there to prevent.
        static string LoadedCount(List<UnifiedRuleRow> r, bool supportsServer, bool serverFailed)
        {
            var kind = serverFailed ? RuleRunsWhere.Client : RuleRunsWhere.Server;
            var n = r.Count(x => x.RunsWhere == kind);
            var noun = kind == RuleRunsWhere.Client ? "client-side" : "server-side";
            var count = n == 0 ? $"No {noun} rules." : $"{n} {noun} rule{(n == 1 ? "" : "s")}.";
            return count + " " + ModeClause(supportsServer);
        }

        static string Counts(List<UnifiedRuleRow> r, bool supportsServer)
        {
            // Counts first — what the reader came for — then what the account can hold, in the same words
            // whichever kind it is. Neither half is left to be inferred from the other's shape: an earlier
            // pass said "N client-side rules." and let the ABSENCE of the server split mean "client-only",
            // then said the presence of "0 on server" meant "server rules are possible here". Both are the
            // same trick, and the mode used to be spoken outright on every load (#550).
            var head = supportsServer
                ? $"{r.Count} rule{(r.Count == 1 ? "" : "s")}: " +
                  $"{r.Count(x => x.RunsWhere == RuleRunsWhere.Server)} on server, " +
                  $"{r.Count(x => x.RunsWhere == RuleRunsWhere.Client)} on client."
                // A client-only account can't have server rules, so the split would be "0 on server"
                // clutter; the clause that follows says the same thing in words.
                : $"{r.Count} rule{(r.Count == 1 ? "" : "s")}.";

            return head + " " + ModeClause(supportsServer);
        }
    }
}
