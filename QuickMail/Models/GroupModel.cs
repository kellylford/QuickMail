using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QuickMail.Models;

/// <summary>
/// A named collection of contacts that can be picked as a single unit in any
/// address field. Groups are local-only (stored in groups.json), flat (no
/// nesting), and many-to-many with contacts (a contact can be in many groups
/// and a group has many contacts).
/// </summary>
public class GroupModel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// IDs of the contacts that are members of this group. The relation is
    /// resolved on every load by intersecting these IDs against the live
    /// contact list — IDs that no longer resolve are silently skipped (see
    /// <see cref="ResolvedMemberCount"/>).
    /// </summary>
    public List<int> MemberContactIds { get; set; } = new();

    /// <summary>
    /// Bumped whenever the user picks this group in compose. Used to sort the
    /// groups list by recency (most-recently-used first), matching the contact
    /// sort order in <c>ContactService.SearchContactsAsync</c>.
    /// </summary>
    public long LastUsedTicks { get; set; }

    /// <summary>
    /// Count of members that actually resolved to a live contact. Computed by
    /// the service on load and never persisted (the value is always recomputed
    /// so the file stays compact and the picker always shows the live state).
    /// </summary>
    [JsonIgnore]
    public int ResolvedMemberCount { get; set; }

    /// <summary>
    /// How many MemberContactIds entries are stale (point to a contact that
    /// no longer exists). Surfaced in the group list as "N members (M missing)".
    /// </summary>
    [JsonIgnore]
    public int MissingContactCount =>
        MemberContactIds.Count - ResolvedMemberCount;

    /// <summary>
    /// Display string for the group list, e.g. "Project Team, 7 members",
    /// "Project Team, 3 members (1 missing)" or "Project Team, no members".
    /// Always led by the name: it is the row's accessible name, so a group with
    /// no members must still say which group it is (#649).
    /// </summary>
    [JsonIgnore]
    public string Display => $"{Name}, {MemberSummary}";

    /// <summary>
    /// <see cref="Display"/> without the name: "7 members", "3 members (1 missing)",
    /// "no members". The address book draws it beside the bold name.
    /// </summary>
    [JsonIgnore]
    public string MemberSummary
    {
        get
        {
            var count = ResolvedMemberCount == 0
                ? "no members"
                : $"{ResolvedMemberCount} member{(ResolvedMemberCount == 1 ? "" : "s")}";
            return MissingContactCount > 0 ? $"{count} ({MissingContactCount} missing)" : count;
        }
    }

    // A screen reader reads a data-bound Selector item's UIA Name from ToString()
    // (DisplayMemberPath="Display" only sets the visual). Without this the Groups
    // list announces "QuickMail.Models.GroupModel" for every row. See CLAUDE.md.
    public override string ToString() => Display;
}
