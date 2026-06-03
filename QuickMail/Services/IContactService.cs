using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IContactService
{
    Task UpsertContactAsync(ContactModel contact);
    Task<List<ContactModel>> SearchContactsAsync(string prefix, CancellationToken ct = default);
    Task<List<ContactModel>> LoadAllContactsAsync();
    Task DeleteContactAsync(int id);

    // ── Groups ───────────────────────────────────────────────────────────────
    // All group operations share the same _loadLock as contact operations so
    // the picker dialog can interleave reads and writes safely (see CLAUDE.md
    // "Single _loadLock covers both" rationale). Storage is a separate file
    // (groups.json) to keep contacts.json untouched.

    /// <summary>
    /// Loads all groups, sorted by LastUsedTicks descending. Each returned
    /// <see cref="GroupModel"/> has its <see cref="GroupModel.ResolvedMemberCount"/>
    /// and <see cref="GroupModel.MissingContactCount"/> recomputed against the
    /// live contact list — stale MemberContactIds are not removed from disk.
    /// </summary>
    Task<List<GroupModel>> LoadAllGroupsAsync();

    /// <summary>
    /// Creates a new group with the given name and returns the assigned Id.
    /// Trims whitespace. Throws <see cref="ArgumentException"/> if the name
    /// is empty after trimming.
    /// </summary>
    Task<int> CreateGroupAsync(string name);

    /// <summary>
    /// Renames an existing group. Throws <see cref="InvalidOperationException"/>
    /// if no group with the given id exists.
    /// </summary>
    Task RenameGroupAsync(int id, string newName);

    /// <summary>
    /// Deletes a group. Contacts referenced by MemberContactIds are not
    /// touched (the relation is held on the group side).
    /// </summary>
    Task DeleteGroupAsync(int id);

    /// <summary>
    /// Adds a contact to a group. Idempotent — adding a member that is
    /// already in the group is a no-op.
    /// </summary>
    Task AddMemberAsync(int groupId, int contactId);

    /// <summary>
    /// Removes a contact from a group. Idempotent — removing a member that
    /// is not in the group is a no-op.
    /// </summary>
    Task RemoveMemberAsync(int groupId, int contactId);

    /// <summary>
    /// Returns the ids of all groups that contain the given contact. Used
    /// by the contact details popover to show "Member of: …".
    /// </summary>
    Task<List<int>> ListGroupsForContactAsync(int contactId);

    /// <summary>
    /// Bumps <see cref="GroupModel.LastUsedTicks"/> on a group (called when
    /// the user picks it in compose). Keeps the picker sorted by recency,
    /// matching the contact sort.
    /// </summary>
    Task TouchGroupAsync(int groupId);

    /// <summary>
    /// Returns up to 5 non-empty groups whose name contains <paramref name="prefix"/>
    /// (case-insensitive), sorted by <see cref="GroupModel.LastUsedTicks"/> descending.
    /// Used by the compose-window address autocomplete to suggest groups alongside
    /// individual contacts. <see cref="GroupModel.ResolvedMemberCount"/> is recomputed
    /// on each returned result.
    /// </summary>
    Task<List<GroupModel>> SearchGroupsAsync(string prefix, CancellationToken ct = default);
}
