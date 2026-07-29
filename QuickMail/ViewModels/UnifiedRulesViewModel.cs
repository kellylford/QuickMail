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

    [ObservableProperty] private UnifiedRuleRow? _selectedRule;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

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
