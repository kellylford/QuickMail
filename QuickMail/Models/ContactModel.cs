using System;
using System.Collections.Generic;
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

    // ── Display-only, stamped by the address book (issue #256) — not persisted ────

    /// <summary>
    /// Human label for where this contact came from, shown in the address book's Account column:
    /// the owning account's name for synced contacts, "Local address book" for local ones. Stamped
    /// by <c>AddressBookViewModel</c> on load; never serialized.
    /// </summary>
    [JsonIgnore]
    public string SourceLabel { get; set; } = "Local address book";

    /// <summary>
    /// Composed accessible name for the contact row (name, email, source), honoring the
    /// ContactListShowFieldLabels setting. Stamped by <c>AddressBookViewModel</c> on load; never
    /// serialized. Falls back to <see cref="Display"/> when not stamped.
    /// </summary>
    [JsonIgnore]
    public string AccessibleName { get; set; } = string.Empty;

    /// <summary>
    /// Every account that has its own copy of this address, for a row that survived the
    /// collapse-by-email in <c>ContactService</c>. One person can be in the local address book
    /// *and* synced from two accounts; the address book shows a single row for them, so the
    /// account filter has to match on any contributing account rather than on the surviving
    /// row's own <see cref="OwnerAccountId"/>. Stamped by <c>ContactService</c>; never serialized.
    /// Empty when the contact did not come through that path.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<Guid> MergedAccountIds { get; set; } = [];

    /// <summary>
    /// True when a local copy of this address contributed to the row — see
    /// <see cref="MergedAccountIds"/>. Distinct from <see cref="IsLocal"/>, which describes only
    /// the surviving row.
    /// </summary>
    [JsonIgnore]
    public bool MergedIncludesLocal { get; set; }

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(DisplayName)
        ? EmailAddress
        : $"{DisplayName} <{EmailAddress}>";

    /// <summary>
    /// Text WPF's <c>TextSearch</c> matches when the user types in a contact list
    /// (issue #371). The name is what the user thinks of the contact as, so it comes
    /// first; contacts stored with only an address (prior recipients) fall back to the
    /// address so they are still reachable by typing. Must be a single value — a
    /// <c>TextSearch.TextPath</c> binds to exactly one property.
    /// </summary>
    [JsonIgnore]
    public string TypeAheadText => string.IsNullOrWhiteSpace(DisplayName)
        ? EmailAddress
        : DisplayName;

    /// <summary>
    /// Display text, and the last-resort accessible name for a contact row (issue #644).
    /// A data-bound Selector item's UIA Name falls back to <c>ToString()</c> when the row
    /// container carries no <c>AutomationProperties.Name</c> — without this override a
    /// screen reader reads "QuickMail.Models.ContactModel". Lists that compose a richer
    /// name (the address book stamps <see cref="AccessibleName"/>) still win; this is the
    /// floor, not the ceiling.
    /// </summary>
    public override string ToString() => Display;
}
