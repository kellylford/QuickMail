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
        // Diagnostic (/debug only): name the specific unsupported field(s) per flagged rule so a
        // gap in the supported subset can be identified. Field NAMES only — not the raw predicate
        // values, which would put mail subjects/addresses in the log.
        foreach (var r in rules.Where(x => !x.IsFullyEditable))
            LogService.Debug($"ServerRules: not-editable '{r.DisplayName}' unsupported=[{string.Join(", ", r.UnsupportedFields)}]");
        var disabled = rules.Where(r => !r.IsEnabled).Select(r => r.DisplayName).ToList();
        if (disabled.Count > 0)
            LogService.Debug($"ServerRules: disabled rules: {string.Join("; ", disabled)}");
        // Server read-only rules are the ones the API refuses to move/edit/delete
        // (ErrorNotSupportedMessageRule). Name them so a reorder/edit failure is easy to trace back.
        var readOnly = rules.Where(r => r.IsReadOnly).Select(r => r.DisplayName).ToList();
        if (readOnly.Count > 0)
            LogService.Debug($"ServerRules: read-only rules: {string.Join("; ", readOnly)}");
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

    public async Task ReorderAsync(Guid accountId, IReadOnlyList<ServerRuleModel> rulesInOrder, CancellationToken ct = default)
    {
        var account = Account(accountId);

        // Reassign which rule holds which sequence value, in the new order — WITHOUT renumbering the
        // whole list. We keep the existing set of server sequence values and only PATCH the rules
        // whose value actually changes. This is critical: a mailbox commonly contains a rule the
        // server refuses to modify ("ErrorNotSupportedMessageRule"). A full 1..N re-sequence PATCHes
        // every rule and 400s the moment it reaches that one — poisoning an otherwise valid move of
        // an unrelated rule. By touching only the rules whose position changed (two, for a single
        // Move up/down), a protected rule elsewhere is never sent a PATCH.
        //
        // Still not atomic (Graph has no batch reorder) and a transient duplicate sequence can exist
        // mid-loop — Graph tolerates that and resolves ordering on read. If a PATCH fails, the caller
        // rolls back its LOCAL order; the next refresh reflects the server's true state.
        var sortedSequences = rulesInOrder.Select(r => r.Sequence).OrderBy(s => s).ToList();
        var patched = 0;
        for (var i = 0; i < rulesInOrder.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rule = rulesInOrder[i];
            var target = sortedSequences[i];
            if (rule.Sequence == target) continue;   // unchanged position → don't touch this rule

            var body = new Dictionary<string, object?> { ["sequence"] = target };
            await GuardAsync(() => _client.PatchAsync(account, $"{RulesPath}/{Uri.EscapeDataString(rule.Id)}", body, ct));
            rule.Sequence = target;   // keep the local model in sync with what the server was told
            patched++;
        }

        LogService.Log($"ServerRules: reordered {rulesInOrder.Count} rule(s), {patched} PATCHed, for {account.Username}");
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
