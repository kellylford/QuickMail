using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Pulls contacts from a Google account via the People API (issue #256): saved connections and
/// "other contacts" (prior recipients). Read-only; relies on the account having consented to the
/// People read scopes (see <see cref="IGoogleOAuthService.AuthorizeContactsAsync"/>). If it hasn't,
/// the People API returns 403 and this surfaces as a sync error.
/// </summary>
public sealed class GoogleContactSource : IProviderContactSource
{
    // Matches the Graph source's cap so both providers behave the same (spec §13.1).
    private const int MaxPriorRecipients = 1000;

    private readonly GooglePeopleClient _people;

    public GoogleContactSource(GooglePeopleClient people) => _people = people;

    public ContactSource Source => ContactSource.Google;

    public async Task<IReadOnlyList<ContactModel>> FetchAsync(AccountModel account, CancellationToken ct = default)
    {
        var results = new List<ContactModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Saved contacts (connections) ────────────────────────────────────
        var connections = await _people.GetConnectionsAsync(account.Username, ct);
        foreach (var p in connections)
        {
            var c = Map(p, isPriorRecipient: false);
            if (c is null || !seen.Add(c.EmailAddress)) continue;
            results.Add(c);
        }

        // ── Prior recipients (other contacts) ───────────────────────────────
        var others = await _people.GetOtherContactsAsync(account.Username, ct);
        int priorAdded = 0;
        bool capped = false;
        foreach (var p in others)
        {
            var c = Map(p, isPriorRecipient: true);
            if (c is null || !seen.Add(c.EmailAddress)) continue;
            if (priorAdded >= MaxPriorRecipients) { capped = true; break; }
            results.Add(c);
            priorAdded++;
        }

        if (capped)
            LogService.Log($"GoogleContactSource: capped prior recipients at {MaxPriorRecipients} for {account.AccountLabel}.");

        return results;
    }

    /// <summary>Maps a People API person to a contact, or null if it has no usable email / id.</summary>
    private static ContactModel? Map(GooglePerson p, bool isPriorRecipient)
    {
        var email = p.EmailAddresses?
            .Select(e => e.Value?.Trim())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(p.ResourceName))
            return null;

        var name = p.Names?
            .Select(n => n.DisplayName?.Trim())
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;

        return new ContactModel
        {
            SourceId         = p.ResourceName,
            DisplayName      = name,
            EmailAddress     = email,
            IsPriorRecipient = isPriorRecipient,
        };
    }
}
