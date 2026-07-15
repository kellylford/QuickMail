using System;
using System.Text.Json.Serialization;

namespace QuickMail.Models;

public class ContactModel
{
    public int    Id            { get; set; }
    public string DisplayName   { get; set; } = string.Empty;
    public string EmailAddress  { get; set; } = string.Empty;
    public long   LastUsedTicks { get; set; }

    // ── Provenance (issue #256 — server contact sync) ────────────────────────
    // All four default to a plain local contact so existing contacts.json files
    // (written before these fields existed) deserialize unchanged: System.Text.Json
    // fills absent properties with their defaults, i.e. Source = Local, no owner.

    /// <summary>
    /// Where this contact came from. <see cref="ContactSource.Local"/> for user-created
    /// contacts (the default); a provider value for synced entries.
    /// </summary>
    public ContactSource Source { get; set; } = ContactSource.Local;

    /// <summary>
    /// Provider-side identifier (Graph contact/person id, Google People <c>resourceName</c>).
    /// Null for local contacts. Used to update a synced contact in place across re-syncs
    /// rather than duplicating it, and to diff the server set against the local cache.
    /// </summary>
    public string? SourceId { get; set; }

    /// <summary>
    /// The account this contact was synced from. Null for local contacts. A sync only ever
    /// replaces rows matching its own <c>(OwnerAccountId, Source)</c> slice, so local contacts
    /// and other accounts' synced contacts are never disturbed.
    /// </summary>
    public Guid? OwnerAccountId { get; set; }

    /// <summary>
    /// True when this entry came from a "people you've emailed" source (Graph <c>/me/people</c>,
    /// Google other-contacts) rather than a saved address-book contact. Prior recipients rank
    /// below saved contacts in autocomplete dedup.
    /// </summary>
    public bool IsPriorRecipient { get; set; }

    /// <summary>True for user-owned contacts; false for anything synced from a server.</summary>
    [JsonIgnore]
    public bool IsLocal => Source == ContactSource.Local;

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(DisplayName)
        ? EmailAddress
        : $"{DisplayName} <{EmailAddress}>";
}
