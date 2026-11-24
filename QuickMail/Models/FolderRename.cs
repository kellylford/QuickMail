using System;
using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>
/// One folder rename that has happened on the server (#1083), and how to carry a stored folder
/// reference across it.
///
/// <para>On IMAP a folder is named by its path, so a rename changes <see cref="OldFullName"/> to
/// <see cref="NewFullName"/> and, with it, the path of every folder inside it: "Projects/2026"
/// becomes "Work/2026". Microsoft 365 and POP3 name folders by id, which a rename keeps; there the
/// two full names are equal, <see cref="NamesChange"/> is false, and only the renamed folder's
/// display name differs.</para>
/// </summary>
/// <param name="AccountId">The account the folder is in. A reference on another account is never
/// touched, however its name reads: another account's "Projects" is another folder.</param>
/// <param name="OldFullName">The folder's full name before the rename.</param>
/// <param name="NewFullName">The folder's full name after it.</param>
/// <param name="Separator">The account's hierarchy separator, which marks a folder inside the
/// renamed one. Null when <see cref="NamesChange"/> is false.</param>
/// <param name="OldDisplayName">The folder's name as shown before the rename.</param>
/// <param name="NewDisplayName">The folder's name as shown after it.</param>
public sealed record FolderRename(
    Guid AccountId,
    string OldFullName,
    string NewFullName,
    char? Separator,
    string OldDisplayName,
    string NewDisplayName)
{
    /// <summary>Whether full names change: true on IMAP, false where folders are named by id.</summary>
    public bool NamesChange => !string.Equals(OldFullName, NewFullName, StringComparison.Ordinal);

    /// <summary>
    /// The full name <paramref name="reference"/> has after the rename, or null when the rename
    /// leaves it as it is. Matched ordinally, as every stored folder reference in QuickMail is: a
    /// Microsoft 365 id is case-sensitive, and on IMAP "projects" and "Projects" can be two folders.
    /// A sibling whose name merely starts the same way ("Projects old") is not inside the folder and
    /// is left alone; only the separator marks a subfolder.
    /// </summary>
    public string? Map(string? reference)
    {
        if (!NamesChange || string.IsNullOrEmpty(reference)) return null;
        if (string.Equals(reference, OldFullName, StringComparison.Ordinal)) return NewFullName;
        if (Separator is { } sep &&
            reference.Length > OldFullName.Length + 1 &&
            reference[OldFullName.Length] == sep &&
            reference.StartsWith(OldFullName, StringComparison.Ordinal))
            return NewFullName + reference[OldFullName.Length..];
        return null;
    }

    /// <summary>
    /// <paramref name="reference"/> after the rename: its new full name when the rename moves it,
    /// otherwise itself.
    /// </summary>
    public string Apply(string reference) => Map(reference) ?? reference;

    /// <summary>
    /// Whether <paramref name="reference"/> names the renamed folder itself, before the rename (or,
    /// where names do not change, at all). What decides whether a stored display name goes with it:
    /// a subfolder keeps its own name.
    /// </summary>
    public bool IsTheFolder(string? reference) =>
        string.Equals(reference, OldFullName, StringComparison.Ordinal);
}

/// <summary>
/// What became of a request to rename a folder (#1083), for the window or picker that asked.
/// </summary>
/// <param name="Succeeded">The server renamed it.</param>
/// <param name="Message">The sentence for the status bar: why it was refused, or what happened.</param>
/// <param name="Announcement">What to say aloud on success, beyond the new name that focus landing on
/// the folder already says: which rules and views were updated, and anything that could not be.
/// Empty when there is nothing more. On failure, the reason (the same as <see cref="Message"/>).</param>
/// <param name="Rename">The rename, when it happened.</param>
/// <param name="AccountFolders">The account's folder list after the rename, for a picker to rebuild from.</param>
public sealed record FolderRenameResult(
    bool Succeeded,
    string Message,
    string Announcement,
    FolderRename? Rename = null,
    IReadOnlyList<MailFolderModel>? AccountFolders = null)
{
    public static FolderRenameResult Refused(string reason) => new(false, reason, reason);
}
