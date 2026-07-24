using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services.Graph;

namespace QuickMail.Services;

/// <summary>
/// Server-side Inbox rules over the Graph <c>messageRule</c> API
/// (<c>/me/mailFolders/inbox/messageRules</c>). See
/// <c>docs/planning/server-rules-pm-dev-spec.md</c>.
/// <para>
/// Tokens come from the account's default scope set — for work/school Graph accounts that is
/// <c>graph.microsoft.com/.default</c>, which carries <c>MailboxSettings.ReadWrite</c> when the app
/// registration declares it. A tenant that hasn't granted it produces <c>403</c>, surfaced here as
/// <see cref="ServerRuleConsentRequiredException"/> for the View to render as an admin-directed
/// message (spec §4/§5).
/// </para>
/// </summary>
public sealed class GraphServerRuleService : IServerRuleService
{
    private const string RulesPath = "/me/mailFolders/inbox/messageRules";

    private const string ConsentMessage =
        "QuickMail can't manage server rules because your organization hasn't granted it permission. " +
        "Ask your administrator to grant it, then sign in again.";

    private readonly IAccountService _accounts;
    private readonly GraphClient _client;

    public GraphServerRuleService(IAccountService accounts, GraphClient client)
    {
        _accounts = accounts;
        _client = client;
    }

    public async Task<IReadOnlyList<ServerRuleModel>> ListAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = Account(accountId);
        var dtos = await GuardAsync(() =>
            _client.GetAllPagesAsync<GraphMessageRule>(account, RulesPath, ct));

        var rules = dtos.Select(ServerRuleMapper.ToModel)
                        .OrderBy(r => r.Sequence)
                        .ToList();
        LogService.Debug($"ServerRules: listed {rules.Count} rule(s) for {account.Username} " +
                         $"({rules.Count(r => !r.IsFullyEditable)} not fully editable)");
        return rules;
    }

    public async Task<ServerRuleModel> CreateAsync(Guid accountId, ServerRuleModel rule, CancellationToken ct = default)
    {
        var account = Account(accountId);
        var body = ServerRuleMapper.ToRequestBody(rule);

        var created = await GuardAsync(() => _client.PostReadAsync<GraphMessageRule>(
            account, RulesPath, body, scopes: null, silentOnly: false, headers: null, ct));

        if (created is null)
            throw new InvalidOperationException("Graph accepted the new rule but returned no content.");

        LogService.Log($"ServerRules: created rule '{rule.DisplayName}' for {account.Username}");
        return ServerRuleMapper.ToModel(created);
    }

    public async Task UpdateAsync(Guid accountId, ServerRuleModel rule, CancellationToken ct = default)
    {
        // Belt-and-braces behind the UI's disabled Edit. Graph PATCH REPLACES conditions/actions, so
        // rewriting a rule we can't fully represent would silently delete predicates the user set in
        // Outlook. Refuse rather than corrupt the mailbox (spec §16).
        if (!rule.IsFullyEditable)
            throw new InvalidOperationException(
                $"Rule '{rule.DisplayName}' uses conditions or actions QuickMail can't represent " +
                "yet, so it cannot be saved from here without losing them. Edit it in Outlook.");

        var account = Account(accountId);
        var body = ServerRuleMapper.ToRequestBody(rule);

        await GuardAsync(() => _client.PatchAsync(account, $"{RulesPath}/{Uri.EscapeDataString(rule.Id)}", body, ct));
        LogService.Log($"ServerRules: updated rule '{rule.DisplayName}' for {account.Username}");
    }

    public async Task SetEnabledAsync(Guid accountId, string ruleId, bool enabled, CancellationToken ct = default)
    {
        var account = Account(accountId);
        // Only isEnabled is sent, so conditions/actions are left untouched — safe even for rules
        // outside the editable subset.
        var body = new Dictionary<string, object?> { ["isEnabled"] = enabled };

        await GuardAsync(() => _client.PatchAsync(account, $"{RulesPath}/{Uri.EscapeDataString(ruleId)}", body, ct));
        LogService.Log($"ServerRules: {(enabled ? "enabled" : "disabled")} rule {ruleId} for {account.Username}");
    }

    public async Task ReorderAsync(Guid accountId, IReadOnlyList<string> ruleIdsInOrder, CancellationToken ct = default)
    {
        var account = Account(accountId);

        // Sequence is 1-based on the server; assign positions in the given order. Only `sequence` is
        // sent, so the rest of each rule is untouched.
        //
        // NOT ATOMIC, and it can't be: Graph exposes no batch/transactional reorder, so this is N
        // sequential PATCHes. A failure partway leaves the server partially reordered, and duplicate
        // sequence values exist transiently mid-loop (Graph tolerates this — it resolves ordering on
        // read). The caller rolls back its LOCAL order on failure, which does not undo PATCHes
        // already applied server-side; the next refresh shows the server's true order. Accepted for
        // v1: the blast radius is rule ordering, not rule content.
        for (var i = 0; i < ruleIdsInOrder.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var body = new Dictionary<string, object?> { ["sequence"] = i + 1 };
            var id = ruleIdsInOrder[i];
            await GuardAsync(() => _client.PatchAsync(account, $"{RulesPath}/{Uri.EscapeDataString(id)}", body, ct));
        }

        LogService.Log($"ServerRules: reordered {ruleIdsInOrder.Count} rule(s) for {account.Username}");
    }

    public async Task DeleteAsync(Guid accountId, string ruleId, CancellationToken ct = default)
    {
        var account = Account(accountId);
        await GuardAsync(() => _client.DeleteAsync(account, $"{RulesPath}/{Uri.EscapeDataString(ruleId)}", ct));
        LogService.Log($"ServerRules: deleted rule {ruleId} for {account.Username}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private AccountModel Account(Guid accountId)
        => _accounts.LoadAccounts().FirstOrDefault(
               a => a.Id == accountId && a.BackendKind == BackendKind.MicrosoftGraph)
           ?? throw new InvalidOperationException(
               $"Server rules require a Microsoft 365 account; none found with id {accountId}.");

    /// <summary>
    /// Translates Graph's <c>403</c> into the typed consent exception so the View can show an
    /// admin-directed message instead of a raw HTTP error. Everything else propagates unchanged —
    /// a failure must never be swallowed into a silent empty state (CLAUDE.md).
    /// </summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> op)
    {
        try
        {
            return await op().ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ServerRuleConsentRequiredException(ConsentMessage, ex);
        }
    }

    private static async Task GuardAsync(Func<Task> op)
    {
        try
        {
            await op().ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ServerRuleConsentRequiredException(ConsentMessage, ex);
        }
    }
}
