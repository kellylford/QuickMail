using System;

namespace QuickMail.Models;

/// <summary>
/// One folder in the folder tree's Favorites section (#1056), stored on its account
/// (<see cref="AccountModel.FavoriteFolders"/>).
/// </summary>
public sealed class FavoriteFolder
{
    /// <summary>
    /// The folder's <see cref="MailFolderModel.FullName"/>, compared ordinally: Graph ids are
    /// case-sensitive, and an IMAP path comes back the same way each time.
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>When the folder was made a favorite. Orders favorites that have no <see cref="Order"/>.</summary>
    public DateTime AddedUtc { get; set; }

    /// <summary>
    /// The favorite's place in the Favorites section, across every account (#1058). Every add and
    /// every move renumbers all favorites from 0, so a new one goes last. Null on an entry written
    /// before favorites could be moved; those sort after the numbered ones, by <see cref="AddedUtc"/>.
    /// </summary>
    public int? Order { get; set; }
}
