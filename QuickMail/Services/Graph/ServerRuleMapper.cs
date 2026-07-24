using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services.Graph;

/// <summary>
/// Maps Graph <c>messageRule</c> JSON ⇄ <see cref="ServerRuleModel"/>, and decides whether a rule is
/// safe to edit.
/// <para>
/// <b>Why the gating matters.</b> Graph <c>PATCH</c> <i>replaces</i> the whole
/// <c>conditions</c>/<c>actions</c> object instead of merging individual predicates. If we let the
/// user edit a rule containing predicates we don't model and then PATCH from our narrower model,
/// those predicates are silently deleted from their mailbox. So any rule using anything outside the
/// supported subset — or having exceptions — is marked not fully editable and becomes view + toggle
/// + delete only. See <c>docs/planning/server-rules-pm-dev-spec.md</c> §16.
/// </para>
/// </summary>
internal static class ServerRuleMapper
{
    /// <summary>Condition predicates QuickMail can represent and therefore safely rewrite.</summary>
    internal static readonly HashSet<string> SupportedConditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "senderContains", "fromAddresses", "subjectContains", "bodyOrSubjectContains",
        "sentToMe", "sentOnlyToMe", "hasAttachments", "importance",
    };

    /// <summary>Actions QuickMail can represent and therefore safely rewrite.</summary>
    internal static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "moveToFolder", "markAsRead", "markImportance", "delete", "forwardTo", "stopProcessingRules",
    };

    /// <summary>
    /// Graph string predicates are <b>collections</b> — Outlook lets a user put several terms in one
    /// (e.g. <c>subjectContains: ["invoice", "receipt"]</c>) — but the editor exposes a single value.
    /// A rule carrying more than one term therefore is NOT fully representable: saving it would
    /// PATCH back a one-element array and silently delete the rest. These are gated by cardinality
    /// in <see cref="ToModel"/> even though the predicate name itself is supported.
    /// <para>
    /// Recipient collections (<c>fromAddresses</c>, <c>forwardTo</c>) are absent here on purpose —
    /// they're modelled as full lists and round-trip completely.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> SingleValueStringPredicates = new(StringComparer.OrdinalIgnoreCase)
    {
        "senderContains", "subjectContains", "bodyOrSubjectContains",
    };

    /// <summary>Friendlier labels for the predicates/actions we don't support yet.</summary>
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bodyContains"] = "body contains",
        ["headerContains"] = "header contains",
        ["recipientContains"] = "recipient contains",
        ["sentToAddresses"] = "sent to specific addresses",
        ["sentCcMe"] = "sent CC to me",
        ["sentToOrCcMe"] = "sent to or CC me",
        ["notSentToMe"] = "not sent to me",
        ["categories"] = "categories",
        ["sensitivity"] = "sensitivity",
        ["messageActionFlag"] = "message flag",
        ["withinSizeRange"] = "message size",
        ["isAutomaticForward"] = "automatic forward",
        ["isMeetingRequest"] = "meeting request",
        ["isMeetingResponse"] = "meeting response",
        ["isReadReceipt"] = "read receipt",
        ["isPermissionControlled"] = "permission controlled",
        ["isSigned"] = "signed",
        ["isEncrypted"] = "encrypted",
        ["isVoicemail"] = "voicemail",
        ["isApprovalRequest"] = "approval request",
        ["isAutomaticReply"] = "automatic reply",
        ["isNonDeliveryReport"] = "non-delivery report",
        ["copyToFolder"] = "copy to folder",
        ["forwardAsAttachmentTo"] = "forward as attachment",
        ["redirectTo"] = "redirect to",
        ["assignCategories"] = "assign categories",
        ["permanentDelete"] = "permanently delete",
        ["exceptions"] = "exceptions",
    };

    // ── Graph → model ───────────────────────────────────────────────────────

    internal static ServerRuleModel ToModel(GraphMessageRule dto)
    {
        var m = new ServerRuleModel
        {
            Id = dto.Id,
            DisplayName = dto.DisplayName ?? string.Empty,
            Sequence = dto.Sequence,
            IsEnabled = dto.IsEnabled,
            IsReadOnly = dto.IsReadOnly,
            HasError = dto.HasError,
            RawConditions = dto.Conditions,
            RawActions = dto.Actions,
            RawExceptions = dto.Exceptions,
        };

        var unsupported = new List<string>();

        if (dto.Conditions is { ValueKind: JsonValueKind.Object } cond)
        {
            foreach (var p in cond.EnumerateObject())
            {
                if (!IsMeaningful(p.Value)) continue;
                if (!SupportedConditions.Contains(p.Name)) { unsupported.Add(Friendly(p.Name)); continue; }

                // The predicate NAME is supported, but its CARDINALITY may not be: the editor holds
                // one value, so a multi-term predicate can't be rewritten without dropping terms.
                // Gate it rather than truncate — silent data loss is exactly what §16 exists to stop.
                if (SingleValueStringPredicates.Contains(p.Name) && HasMultipleValues(p.Value))
                {
                    unsupported.Add($"{Friendly(p.Name)} (multiple values)");
                    continue;
                }

                switch (p.Name.ToLowerInvariant())
                {
                    case "sendercontains": m.SenderContains = FirstString(p.Value); break;
                    case "subjectcontains": m.SubjectContains = FirstString(p.Value); break;
                    case "bodyorsubjectcontains": m.BodyOrSubjectContains = FirstString(p.Value); break;
                    case "fromaddresses": m.FromAddresses = Recipients(p.Value); break;
                    case "senttome": m.SentToMe = p.Value.ValueKind == JsonValueKind.True; break;
                    case "sentonlytome": m.SentOnlyToMe = p.Value.ValueKind == JsonValueKind.True; break;
                    case "hasattachments": m.HasAttachments = p.Value.ValueKind == JsonValueKind.True; break;
                    case "importance": m.Importance = p.Value.GetString(); break;
                }
            }
        }

        if (dto.Actions is { ValueKind: JsonValueKind.Object } act)
        {
            foreach (var p in act.EnumerateObject())
            {
                if (!IsMeaningful(p.Value)) continue;
                if (!SupportedActions.Contains(p.Name)) { unsupported.Add(Friendly(p.Name)); continue; }

                switch (p.Name.ToLowerInvariant())
                {
                    case "movetofolder": m.MoveToFolderId = p.Value.GetString(); break;
                    case "markasread": m.MarkAsRead = p.Value.ValueKind == JsonValueKind.True; break;
                    case "markimportance": m.MarkImportance = p.Value.GetString(); break;
                    case "delete": m.Delete = p.Value.ValueKind == JsonValueKind.True; break;
                    case "forwardto": m.ForwardTo = Recipients(p.Value); break;
                    case "stopprocessingrules": m.StopProcessingRules = p.Value.ValueKind == JsonValueKind.True; break;
                }
            }
        }

        // Exceptions are preserved on the wire but never authored here. A rule that has any means we
        // cannot safely rewrite it from our model, so it is view-only (spec §16 / §18).
        if (dto.Exceptions is { ValueKind: JsonValueKind.Object } exc &&
            exc.EnumerateObject().Any(p => IsMeaningful(p.Value)))
        {
            unsupported.Add(Friendly("exceptions"));
        }

        m.UnsupportedFields = unsupported.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        m.IsFullyEditable = m.UnsupportedFields.Count == 0;
        return m;
    }

    // ── Model → Graph ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds the create/update body. Only ever called for a fully-editable rule — callers must
    /// enforce that, because this writes <c>conditions</c>/<c>actions</c> wholesale.
    /// <c>exceptions</c> is deliberately never written, so PATCH leaves any server-side exceptions
    /// untouched.
    /// </summary>
    internal static Dictionary<string, object?> ToRequestBody(ServerRuleModel rule)
    {
        var conditions = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(rule.SenderContains)) conditions["senderContains"] = new[] { rule.SenderContains };
        if (!string.IsNullOrWhiteSpace(rule.SubjectContains)) conditions["subjectContains"] = new[] { rule.SubjectContains };
        if (!string.IsNullOrWhiteSpace(rule.BodyOrSubjectContains)) conditions["bodyOrSubjectContains"] = new[] { rule.BodyOrSubjectContains };
        if (rule.FromAddresses.Count > 0) conditions["fromAddresses"] = rule.FromAddresses.Select(ToRecipient).ToArray();
        if (rule.SentToMe) conditions["sentToMe"] = true;
        if (rule.SentOnlyToMe) conditions["sentOnlyToMe"] = true;
        if (rule.HasAttachments) conditions["hasAttachments"] = true;
        if (!string.IsNullOrWhiteSpace(rule.Importance)) conditions["importance"] = rule.Importance;

        var actions = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(rule.MoveToFolderId)) actions["moveToFolder"] = rule.MoveToFolderId;
        if (rule.MarkAsRead) actions["markAsRead"] = true;
        if (!string.IsNullOrWhiteSpace(rule.MarkImportance)) actions["markImportance"] = rule.MarkImportance;
        if (rule.Delete) actions["delete"] = true;
        if (rule.ForwardTo.Count > 0) actions["forwardTo"] = rule.ForwardTo.Select(ToRecipient).ToArray();
        if (rule.StopProcessingRules) actions["stopProcessingRules"] = true;

        return new Dictionary<string, object?>
        {
            ["displayName"] = rule.DisplayName,
            ["sequence"] = rule.Sequence,
            ["isEnabled"] = rule.IsEnabled,
            ["conditions"] = conditions,
            ["actions"] = actions,
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True when a property actually participates in the rule. Graph returns unset predicates as
    /// <c>null</c>, empty arrays, or <c>false</c>; treating those as "present" would flag every rule
    /// as non-editable.
    /// </summary>
    internal static bool IsMeaningful(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.String => !string.IsNullOrEmpty(v.GetString()),
        JsonValueKind.Object => v.EnumerateObject().Any(),
        _ => true,
    };

    /// <summary>
    /// True when a string-collection predicate carries more than one term — the case the editor
    /// cannot represent (see <see cref="SingleValueStringPredicates"/>).
    /// </summary>
    private static bool HasMultipleValues(JsonElement v)
        => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 1;

    /// <summary>Graph string predicates are collections; the editor exposes a single value.</summary>
    private static string? FirstString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Array => v.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String
            ? v.EnumerateArray().First().GetString()
            : null,
        _ => null,
    };

    /// <summary>Extracts addresses from a Graph recipient collection.</summary>
    private static List<string> Recipients(JsonElement v)
    {
        var list = new List<string>();
        if (v.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("emailAddress", out var ea) &&
                ea.ValueKind == JsonValueKind.Object &&
                ea.TryGetProperty("address", out var addr) &&
                addr.ValueKind == JsonValueKind.String)
            {
                var s = addr.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }

    private static object ToRecipient(string address)
        => new Dictionary<string, object?> { ["emailAddress"] = new Dictionary<string, object?> { ["address"] = address } };

    private static string Friendly(string graphName)
    {
        if (FriendlyNames.TryGetValue(graphName, out var friendly)) return friendly;

        // Fallback: split camelCase into words so the user sees "sent to addresses", not a raw token.
        var sb = new StringBuilder();
        foreach (var ch in graphName)
        {
            if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
