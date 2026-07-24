using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Manages <b>server-side</b> Inbox rules on a Microsoft Graph account — the rules Exchange runs on
/// its own servers, even when QuickMail is closed. Deliberately NOT part of
/// <see cref="IMailService"/>: it is Graph-only and has no IMAP equivalent.
/// <para>
/// QuickMail's in-app rules (<see cref="IRuleService"/>) are a separate feature that runs during
/// sync. The two are never merged or synchronized — see
/// <c>docs/planning/server-rules-pm-dev-spec.md</c> §3.
/// </para>
/// </summary>
public interface IServerRuleService
{
    /// <summary>All rules for the account, ordered by execution sequence.</summary>
    Task<IReadOnlyList<ServerRuleModel>> ListAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Creates a rule and returns it as stored by the server (including its new id).</summary>
    Task<ServerRuleModel> CreateAsync(Guid accountId, ServerRuleModel rule, CancellationToken ct = default);

    /// <summary>
    /// Rewrites a rule's name, conditions, and actions.
    /// <para>
    /// Throws <see cref="InvalidOperationException"/> when the rule is not
    /// <see cref="ServerRuleModel.IsFullyEditable"/>: Graph PATCH replaces the whole
    /// conditions/actions object, so saving a partially-modelled rule would silently delete
    /// predicates the user set elsewhere (spec §16).
    /// </para>
    /// </summary>
    Task UpdateAsync(Guid accountId, ServerRuleModel rule, CancellationToken ct = default);

    /// <summary>Enables or disables a rule. Safe even for rules outside the editable subset.</summary>
    Task SetEnabledAsync(Guid accountId, string ruleId, bool enabled, CancellationToken ct = default);

    /// <summary>Rewrites execution order; the given ids are assigned sequences 1..n in order.</summary>
    Task ReorderAsync(Guid accountId, IReadOnlyList<string> ruleIdsInOrder, CancellationToken ct = default);

    Task DeleteAsync(Guid accountId, string ruleId, CancellationToken ct = default);
}
