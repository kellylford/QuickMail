using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services.Graph;

namespace QuickMail.Services;

/// <summary>
/// Pulls contacts from a Microsoft account via Graph: <c>/me/contacts</c> (saved contacts) and
/// <c>/me/people</c> (relevance-ranked prior recipients). Read-only; uses the explicit
/// <see cref="OAuthService.GraphContactScopes"/> so it works for personal and work/school accounts
/// alike (issue #256).
/// </summary>
public sealed class GraphContactSource : IProviderContactSource
{
    // Prior recipients can be numerous. Cap what we import so contacts.json stays small and the
    // address book / autocomplete stay responsive (spec §13.1). Saved contacts are never capped.
    private const int MaxPriorRecipients = 1000;

    private readonly GraphClient _graph;

    public GraphContactSource(GraphClient graph) => _graph = graph;

    public ContactSource Source => ContactSource.Microsoft;

    public async Task<IReadOnlyList<ContactModel>> FetchAsync(AccountModel account, CancellationToken ct = default)
    {
        var results = new List<ContactModel>();
        // De-dup emails within the provider result: a saved contact wins over the same address
        // appearing again as a prior recipient.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Saved contacts ──────────────────────────────────────────────────
        // silentOnly: sync must never launch an interactive sign-in — the address book runs modal,
        // where an embedded WebView2 could deadlock the UI thread. Consent is obtained up front when
        // the user enables sync; if the grant later lapses, this throws and sync reports an error.
        var contacts = await _graph.GetAllPagesAsync<GraphContact>(
            account, "/me/contacts?$select=id,displayName,emailAddresses&$top=100",
            OAuthService.GraphContactScopes, silentOnly: true, ct);

        foreach (var c in contacts)
        {
            var email = FirstEmail(c.EmailAddresses?.Select(e => e.Address));
            if (email is null || string.IsNullOrEmpty(c.Id)) continue;
            if (!seen.Add(email)) continue;
            results.Add(new ContactModel
            {
                SourceId         = c.Id,
                DisplayName      = c.DisplayName?.Trim() ?? string.Empty,
                EmailAddress     = email,
                IsPriorRecipient = false,
            });
        }

        // ── Prior recipients (people the user has corresponded with) ─────────
        var people = await _graph.GetAllPagesAsync<GraphPerson>(
            account, "/me/people?$select=id,displayName,scoredEmailAddresses&$top=100",
            OAuthService.GraphContactScopes, silentOnly: true, ct);

        int priorAdded = 0;
        bool capped = false;
        foreach (var p in people)
        {
            var email = FirstEmail(p.ScoredEmailAddresses?.Select(e => e.Address));
            if (email is null) continue;
            if (!seen.Add(email)) continue;
            if (priorAdded >= MaxPriorRecipients) { capped = true; break; }
            results.Add(new ContactModel
            {
                // People ids can be absent; the email is a stable per-account key in that case.
                SourceId         = string.IsNullOrEmpty(p.Id) ? email : p.Id,
                DisplayName      = p.DisplayName?.Trim() ?? string.Empty,
                EmailAddress     = email,
                IsPriorRecipient = true,
            });
            priorAdded++;
        }

        if (capped)
            LogService.Log($"GraphContactSource: capped prior recipients at {MaxPriorRecipients} for {account.AccountLabel}.");

        return results;
    }

    private static string? FirstEmail(IEnumerable<string?>? addresses)
    {
        var a = addresses?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
        return string.IsNullOrEmpty(a) ? null : a;
    }
}
