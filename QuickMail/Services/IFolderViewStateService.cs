using System;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Remembers the message-list presentation (<see cref="ListState"/>) each folder was last given,
/// so a folder opens the way it was left. Issue #520.
/// </summary>
public interface IFolderViewStateService
{
    // Named Recall/Remember/Forget rather than Get/Set/Clear: CA1716 flags Get and Set on an
    // interface member as reserved words in other CLR languages.

    /// <summary>The remembered state for a folder, or null if it has never been customised.</summary>
    ListState? Recall(Guid accountId, string folderFullName);

    /// <summary>Records the state for a folder, replacing any previous entry.</summary>
    void Remember(Guid accountId, string folderFullName, ListState state);

    /// <summary>Forgets a folder's state so it goes back to inheriting the global default.</summary>
    void Forget(Guid accountId, string folderFullName);

    /// <summary>
    /// Moves this account's remembered states to new folder names after a rename (#1083).
    /// <paramref name="rename"/> returns a folder's new full name, or null for a folder it leaves
    /// alone.
    /// </summary>
    void Rename(Guid accountId, Func<string, string?> rename);
}
