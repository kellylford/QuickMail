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

    // ── Construction ────────────────────────────────────────────────────────

    public ServerRulesViewModel(IServerRuleService service, IEnumerable<AccountModel> graphAccounts)
    {
        _service = service;

        AccountOptions = graphAccounts
            .Where(a => a.BackendKind == BackendKind.MicrosoftGraph)
            .Select(a => new AccountOption { Id = a.Id, DisplayName = a.AccountLabel })
            .ToList();

        _selectedAccount = AccountOptions.FirstOrDefault();
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
    private ServerRuleModel? _selectedRule;

    [ObservableProperty] private AccountOption? _selectedAccount;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Editing is blocked for read-only rules and for rules using predicates/actions we can't
    /// represent — saving those would replace the server's richer object with our narrower one and
    /// silently drop the user's other predicates (spec §16).
    /// </summary>
    public bool CanEditSelected => SelectedRule is { IsFullyEditable: true, IsReadOnly: false };

    /// <summary>Toggle/reorder/delete only need the rule to be writable, not fully representable.</summary>
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
            foreach (var r in rules) Rules.Add(r);

            SelectedRule = Rules.FirstOrDefault(r => r.Id == previouslySelected) ?? Rules.FirstOrDefault();

            var disabled = Rules.Count(r => !r.IsEnabled);
            StatusText = Rules.Count == 0
                ? "No server rules."
                : $"{Rules.Count} rule{(Rules.Count == 1 ? "" : "s")}" + (disabled > 0 ? $", {disabled} disabled." : ".");
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
        editor.Saved += async rule => await SaveNewAsync(accountId, rule);
        EditorRequested?.Invoke(editor);
    }

    [RelayCommand]
    private void EditRule()
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        if (SelectedRule is not { } rule) return;

        if (!CanEditSelected)
        {
            var why = rule.IsReadOnly
                ? "This rule is read-only on the server and can't be changed here."
                : "This rule uses conditions or actions QuickMail can't edit yet. You can enable, disable, or delete it here, or edit it in Outlook.";
            StatusText = why;
            Announce(why, AnnouncementCategory.Hint);
            return;
        }

        var editor = ServerRuleEditorViewModel.ForEdit(rule);
        editor.Saved += async updated => await SaveExistingAsync(accountId, updated);
        EditorRequested?.Invoke(editor);
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        if (SelectedRule is not { } rule) return;
        if (!CanModifySelected) return;

        var target = !rule.IsEnabled;
        await RunWriteAsync(async () =>
        {
            await _service.SetEnabledAsync(accountId, rule.Id, target, ct);
            rule.IsEnabled = target;
            OnPropertyChanged(nameof(DetailText));
            Announce(target ? "Rule enabled." : "Rule disabled.", AnnouncementCategory.Result);
        });
    }

    [RelayCommand]
    private Task MoveUpAsync(CancellationToken ct) => MoveAsync(-1, ct);

    [RelayCommand]
    private Task MoveDownAsync(CancellationToken ct) => MoveAsync(+1, ct);

    [RelayCommand]
    private async Task DeleteRuleAsync(CancellationToken ct)
    {
        if (SelectedAccount?.Id is not Guid accountId) return;
        if (SelectedRule is not { } rule) return;
        if (!CanModifySelected) return;

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
            await _service.ReorderAsync(accountId, Rules.Select(r => r.Id).ToList(), ct);

            // Keep local sequence numbers consistent with what the server was just told.
            for (var i = 0; i < Rules.Count; i++) Rules[i].Sequence = i + 1;

            Announce($"Moved {(delta < 0 ? "up" : "down")}. Now {to + 1} of {Rules.Count}.",
                AnnouncementCategory.Status);
        }, onFailure: () => Rules.Move(to, from));   // put it back if the server refused
    }

    private async Task SaveNewAsync(Guid accountId, ServerRuleModel rule)
    {
        await RunWriteAsync(async () =>
        {
            var created = await _service.CreateAsync(accountId, rule);
            Rules.Add(created);
            SelectedRule = created;
            Announce("Rule created.", AnnouncementCategory.Result);
        });
    }

    private async Task SaveExistingAsync(Guid accountId, ServerRuleModel rule)
    {
        await RunWriteAsync(async () =>
        {
            await _service.UpdateAsync(accountId, rule);

            var index = Rules.ToList().FindIndex(r => r.Id == rule.Id);
            if (index >= 0) Rules[index] = rule;
            SelectedRule = rule;

            Announce("Rule updated.", AnnouncementCategory.Result);
        });
    }

    /// <summary>
    /// Runs a write, translating a permission refusal into the admin-directed path and never
    /// swallowing other failures into a silent no-op.
    /// </summary>
    private async Task RunWriteAsync(Func<Task> write, Action? onFailure = null)
    {
        IsBusy = true;
        try
        {
            await write();
        }
        catch (ServerRuleConsentRequiredException ex)
        {
            onFailure?.Invoke();
            HandlePermissionRefusal(ex);
        }
        catch (Exception ex)
        {
            onFailure?.Invoke();
            StatusText = ex.Message;
            Announce(StatusText, AnnouncementCategory.Result);
            LogService.Log("ServerRules: write failed", ex);
        }
        finally
        {
            IsBusy = false;
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
