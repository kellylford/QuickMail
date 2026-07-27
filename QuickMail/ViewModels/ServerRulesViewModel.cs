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
/// Backs the <b>"On the server"</b> tab of the Rules Manager — Exchange/Graph rules that run on
/// Microsoft's servers even when QuickMail is closed. Separate from
/// <see cref="RulesManagerViewModel"/> (the "In QuickMail" tab); the two are never merged or
/// synchronized. See <c>docs/planning/server-rules-pm-dev-spec.md</c> §3/§9.
/// </summary>
public partial class ServerRulesViewModel : ObservableObject
{
    private readonly IServerRuleService _service;

    // ── Events (View subscribes) ────────────────────────────────────────────

    /// <summary>Ask the View for delete confirmation. (No MessageBox in a ViewModel.)</summary>
    public event Func<string, string, bool>? ConfirmDeleteRequested;

    /// <summary>Screen-reader announcement request: (text, category).</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>
    /// A write was refused for lack of <c>MailboxSettings.ReadWrite</c>. The View shows an
    /// <b>admin-directed</b> message — never an in-app "Reauthorize", which cannot succeed under
    /// <c>.default</c> (spec §4/§5).
    /// </summary>
    public event Action<string>? WriteBlockedByPermission;

    /// <summary>The View should open the rule editor (modeless) for this prepared editor VM.</summary>
    public event Action<ServerRuleEditorViewModel>? EditorRequested;

    /// <summary>
    /// After an action that changed a rule in place (toggle, move, delete), the View should return
    /// keyboard focus to the selected rule in the list — so the user isn't stranded on a button, and
    /// (for toggle) the screen reader re-announces the updated row when focus lands back on it.
    /// </summary>
    public event Action? FocusSelectedRuleRequested;

    // ── Construction ────────────────────────────────────────────────────────

    private readonly IReadOnlyDictionary<Guid, List<MailFolderModel>>? _foldersByAccount;

    public ServerRulesViewModel(
        IServerRuleService service,
        IEnumerable<AccountModel> graphAccounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>>? foldersByAccount = null,
        Guid? preferredAccountId = null)
    {
        _service = service;
        _foldersByAccount = foldersByAccount;

        AccountOptions = graphAccounts
            .Where(a => a.BackendKind == BackendKind.MicrosoftGraph)
            .Select(a => new AccountOption { Id = a.Id, DisplayName = a.AccountLabel })
            .ToList();

        // Land on the account the user is currently in (the inbox they opened the Rules Manager from)
        // rather than always the first Graph account — otherwise opening from the Guest inbox would
        // show icanbrew's rules. Falls back to the first account when there's no current-account
        // context (e.g. an aggregate/unified view at the top of the tree).
        _selectedAccount = AccountOptions.FirstOrDefault(o => o.Id == preferredAccountId)
                           ?? AccountOptions.FirstOrDefault();
    }

    /// <summary>
    /// Fills in the display names for a rule's move/copy target folders. Graph rules carry only the
    /// opaque folder ID; the cached folder list maps that ID (stored as <c>FullName</c>) to a
    /// readable name. Best-effort — a target not in the cached set (e.g. a rarely-synced subfolder)
    /// falls back to "another folder".
    /// </summary>
    private void ResolveFolderNames(ServerRuleModel rule)
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        if (_foldersByAccount is null || !_foldersByAccount.TryGetValue(accountId, out var folders)) return;

        if (!string.IsNullOrWhiteSpace(rule.MoveToFolderId))
            rule.MoveToFolderName = folders.FirstOrDefault(f => f.FullName == rule.MoveToFolderId)?.DisplayName;
        if (!string.IsNullOrWhiteSpace(rule.CopyToFolderId))
            rule.CopyToFolderName = folders.FirstOrDefault(f => f.FullName == rule.CopyToFolderId)?.DisplayName;
    }

    // ── State ───────────────────────────────────────────────────────────────

    public ObservableCollection<ServerRuleModel> Rules { get; } = [];

    /// <summary>Graph accounts only — "All accounts" is meaningless for server rules.</summary>
    public List<AccountOption> AccountOptions { get; }

    /// <summary>The account ComboBox is only worth showing when there's a choice to make.</summary>
    public bool ShowAccountSelector => AccountOptions.Count > 1;

    /// <summary>False when the signed-in user has no Graph account — the tab is hidden entirely.</summary>
    public bool HasGraphAccount => AccountOptions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    [NotifyPropertyChangedFor(nameof(CanModifySelected))]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyPropertyChangedFor(nameof(ToggleEnabledLabel))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRuleCommand))]
    private ServerRuleModel? _selectedRule;

    /// <summary>Enable/Disable button text: "Enable" for a disabled rule, "Disable" for an enabled one.</summary>
    public string ToggleEnabledLabel => SelectedRule?.IsEnabled == true ? "Disable" : "Enable";

    /// <summary>Move Up is invalid for the first rule, a server read-only rule, or none selected.
    /// A read-only rule can't be re-sequenced (Graph refuses the PATCH), so gate it like Edit/Delete.</summary>
    public bool CanMoveUp => SelectedRule is { IsReadOnly: false } r && Rules.IndexOf(r) > 0;

    /// <summary>Move Down is invalid for the last rule, a server read-only rule, or none selected.</summary>
    public bool CanMoveDown => SelectedRule is { IsReadOnly: false } r && Rules.IndexOf(r) is var i && i >= 0 && i < Rules.Count - 1;

    [ObservableProperty] private AccountOption? _selectedAccount;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Editing is blocked for read-only rules and for rules using predicates/actions we can't
    /// represent — saving those would replace the server's richer object with our narrower one and
    /// silently drop the user's other predicates (spec §16).
    /// <para>
    /// Wired to <c>EditRuleCommand.CanExecute</c> so the Edit button and context-menu item are
    /// <b>disabled</b> for these rules. (Tim's call, 2026-07-25, reversing the earlier
    /// keep-enabled-and-explain design: he runs with announcements off, so an explain-on-invoke is
    /// inaudible — a pressable item that silently fails is worse than a disabled one. The list row
    /// already states "read-only" / "not editable in QuickMail", so the reason is still conveyed.)
    /// </para>
    /// </summary>
    public bool CanEditSelected => SelectedRule is { IsFullyEditable: true, IsReadOnly: false };

    /// <summary>
    /// Toggle/reorder/delete only need the rule to be writable, not fully representable. Wired to
    /// those commands' <c>CanExecute</c> so they're disabled for read-only rules (see
    /// <see cref="CanEditSelected"/> for the rationale).
    /// </summary>
    public bool CanModifySelected => SelectedRule is { IsReadOnly: false };

    /// <summary>Full prose for the detail region, including parts QuickMail can't edit.</summary>
    public string DetailText => SelectedRule?.DetailText() ?? string.Empty;

    partial void OnSelectedAccountChanged(AccountOption? value) => _ = RefreshCommand.ExecuteAsync(null);

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId) return;

        IsBusy = true;
        try
        {
            var rules = await _service.ListAsync(accountId, ct);

            var previouslySelected = SelectedRule?.Id;
            Rules.Clear();
            foreach (var r in rules)
            {
                ResolveFolderNames(r);
                Rules.Add(r);
            }

            SelectedRule = Rules.FirstOrDefault(r => r.Id == previouslySelected) ?? Rules.FirstOrDefault();

            var disabled = Rules.Count(r => !r.IsEnabled);
            var notEditable = Rules.Count(r => !r.IsFullyEditable);
            StatusText = Rules.Count == 0
                ? "No server rules."
                : $"{Rules.Count} rule{(Rules.Count == 1 ? "" : "s")}"
                  + (disabled > 0 ? $", {disabled} disabled" : "")
                  + (notEditable > 0 ? $", {notEditable} not editable in QuickMail" : "")
                  + ".";
            Announce(StatusText, AnnouncementCategory.Status);
        }
        catch (ServerRuleConsentRequiredException ex)
        {
            HandlePermissionRefusal(ex);
        }
        catch (Exception ex)
        {
            // Never a silent empty state — surface the failure (CLAUDE.md).
            StatusText = $"Couldn't load server rules: {ex.Message}";
            Announce(StatusText, AnnouncementCategory.Status);
            LogService.Log("ServerRules: list failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CreateRule()
    {
        if (SelectedAccount?.Id is not Guid accountId) return;

        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Saved += rule => SaveNewAsync(accountId, rule);
        EditorRequested?.Invoke(editor);
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void EditRule()
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        if (SelectedRule is not { } rule) return;
        if (!CanEditSelected) return;   // defensive; the command is disabled for these rules

        var editor = ServerRuleEditorViewModel.ForEdit(rule);
        editor.Saved += updated => SaveExistingAsync(accountId, updated);
        EditorRequested?.Invoke(editor);
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task ToggleEnabledAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        if (SelectedRule is not { } rule) return;
        if (!CanModifySelected) return;   // defensive; the command is disabled for read-only rules

        var target = !rule.IsEnabled;
        await RunWriteAsync(async () =>
        {
            await _service.SetEnabledAsync(accountId, rule.Id, target, ct);
            rule.IsEnabled = target;                   // observable → the row's RowText updates in place
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(ToggleEnabledLabel));
            Announce(target ? "Rule enabled." : "Rule disabled.", AnnouncementCategory.Result);
            // Focus returns to the row (so the new state is read) via RunWriteAsync's finally.
        });
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveUpAsync(CancellationToken ct) => MoveAsync(-1, ct);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveDownAsync(CancellationToken ct) => MoveAsync(+1, ct);

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task DeleteRuleAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        if (SelectedRule is not { } rule) return;
        if (!CanModifySelected) return;   // defensive; the command is disabled for read-only rules

        var confirmed = ConfirmDeleteRequested?.Invoke(
            $"Delete server rule '{rule.DisplayName}'? It will stop running on the server.",
            "Delete Server Rule") ?? false;
        if (!confirmed) return;

        await RunWriteAsync(async () =>
        {
            await _service.DeleteAsync(accountId, rule.Id, ct);

            // Keep focus somewhere sensible: the next rule, or the one above if this was last.
            var index = Rules.IndexOf(rule);
            Rules.Remove(rule);
            SelectedRule = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];

            Announce("Rule deleted.", AnnouncementCategory.Result);
            // Focus moves to the newly-selected neighbour via RunWriteAsync's finally.
        });
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private async Task MoveAsync(int delta, CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        if (SelectedRule is not { } rule) return;

        var from = Rules.IndexOf(rule);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Rules.Count) return;

        Rules.Move(from, to);
        SelectedRule = rule;

        await RunWriteAsync(async () =>
        {
            // Pass the models (each carries its current server Sequence). The service reassigns only
            // the changed sequence values, so a server-protected rule elsewhere isn't PATCHed and
            // can't 400 this move.
            await _service.ReorderAsync(accountId, Rules.ToList(), ct);

            Announce($"Moved {(delta < 0 ? "up" : "down")}. Now {to + 1} of {Rules.Count}.",
                AnnouncementCategory.Status);
            // The rule's position changed → Move Up/Down availability may have flipped (now at an end).
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanMoveUp));
            OnPropertyChanged(nameof(CanMoveDown));
            // Focus follows the rule to its new position via RunWriteAsync's finally.
        }, onFailure: () => Rules.Move(to, from));   // put it back if the server refused
    }

    /// <summary>Persists a new rule. Returns null on success, or an error message for the editor.</summary>
    private async Task<string?> SaveNewAsync(Guid accountId, ServerRuleModel rule)
    {
        // Graph rejects sequence 0 (must be 1-based); a new rule goes to the end of the list.
        rule.Sequence = Rules.Count + 1;

        // focusSelectedAfter:false — the editor is still open during the write; don't pull focus to
        // the list. On success we focus the new rule *after* the editor closes (below).
        var error = await RunWriteAsync(async () =>
        {
            var created = await _service.CreateAsync(accountId, rule);
            Rules.Add(created);
            SelectedRule = created;
            Announce("Rule created.", AnnouncementCategory.Result);
        }, focusSelectedAfter: false);

        if (error is null) FocusSelectedRuleRequested?.Invoke();   // land on the new rule once the editor closes
        return error;
    }

    /// <summary>Persists an edited rule. Returns null on success, or an error message for the editor.</summary>
    private async Task<string?> SaveExistingAsync(Guid accountId, ServerRuleModel rule)
    {
        var error = await RunWriteAsync(async () =>
        {
            await _service.UpdateAsync(accountId, rule);

            var index = Rules.ToList().FindIndex(r => r.Id == rule.Id);
            if (index >= 0) Rules[index] = rule;
            SelectedRule = rule;

            Announce("Rule updated.", AnnouncementCategory.Result);
        }, focusSelectedAfter: false);

        if (error is null) FocusSelectedRuleRequested?.Invoke();
        return error;
    }

    /// <summary>
    /// Runs a write, translating a permission refusal into the admin-directed path and never
    /// swallowing other failures into a silent no-op. Returns null on success, or a user-facing
    /// error message (which the editor shows when the write came from a Save).
    /// </summary>
    /// <param name="focusSelectedAfter">
    /// When true (the in-list actions: toggle/move/delete), focus returns to the selected rule so the
    /// user isn't stranded on a button. Save paths pass false — the editor is still open during the
    /// write and manages its own focus (staying on the error, or focusing the list after it closes).
    /// </param>
    private async Task<string?> RunWriteAsync(Func<Task> write, Action? onFailure = null, bool focusSelectedAfter = true)
    {
        IsBusy = true;
        try
        {
            await write();
            return null;
        }
        catch (ServerRuleConsentRequiredException ex)
        {
            onFailure?.Invoke();
            HandlePermissionRefusal(ex);
            return StatusText;
        }
        catch (Exception ex)
        {
            onFailure?.Invoke();
            // Some rules (typically created by Outlook) can't be modified through the Graph API at
            // all — the server returns ErrorNotSupportedMessageRule. Translate that into a plain,
            // actionable message instead of surfacing raw HTTP/JSON.
            StatusText = ex.Message.Contains("ErrorNotSupportedMessageRule", StringComparison.OrdinalIgnoreCase)
                ? $"'{SelectedRule?.DisplayName}' can't be changed from QuickMail — edit it in Outlook."
                : ex.Message;
            Announce(StatusText, AnnouncementCategory.Result);
            LogService.Log("ServerRules: write failed", ex);
            return StatusText;
        }
        finally
        {
            IsBusy = false;
            // Put keyboard focus back on the selected rule so the user isn't stranded on a button
            // (and, after a failed move, so arrow keys stay in the list). Suppressed for Save paths.
            if (focusSelectedAfter && SelectedRule is not null) FocusSelectedRuleRequested?.Invoke();
        }
    }

    private void HandlePermissionRefusal(ServerRuleConsentRequiredException ex)
    {
        StatusText = ex.Message;
        WriteBlockedByPermission?.Invoke(ex.Message);
        Announce(ex.Message, AnnouncementCategory.Hint);
        LogService.Log("ServerRules: blocked by missing MailboxSettings.ReadWrite");
    }

    private void Announce(string text, AnnouncementCategory category)
        => AnnouncementRequested?.Invoke(text, category);
}
