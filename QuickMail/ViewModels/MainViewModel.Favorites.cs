using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Favorite folders (#1056): a Favorites section in the folder tree holding the folders the user
/// picks, from any account. A favorite node holds the same <see cref="MailFolderModel"/> object as the
/// folder's node under its account, so opening it opens the folder and its unread count stays current
/// with no code of its own. The list lives on each account (<see cref="AccountModel.FavoriteFolders"/>)
/// so it goes with the account; the section lists every account's favorites together, in the order
/// the user arranged them (#1058).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The label of the Favorites section's heading.</summary>
    public const string FavoritesLabel = "Favorites";

    /// <summary>The label of the Views section's heading.</summary>
    public const string ViewsLabel = "Views";

    /// <summary>The label of the All Mail section's heading.</summary>
    public const string AllMailLabel = "All Mail";

    /// <summary>
    /// Why <paramref name="node"/> cannot be a favorite, or null when it can. Only a real mail folder
    /// of a known account can; unlike Hide Folder, the Inbox is welcome.
    /// </summary>
    public string? FavoriteFolderRefusal(FolderTreeNode? node)
    {
        if (node is null)
            return "Select a folder in the folder tree first.";
        if (node.IsCalendarNode || (node.Folder is { } cal && IsCalendarFolderName(cal.FullName)))
            return $"'{node.Label}' is a calendar, not a mail folder, so it cannot be a favorite.";
        if (node.IsHeader && node.AccountId is not null)
            return $"'{node.Label}' is an account, not a folder, so it cannot be a favorite.";
        if (node.IsHeader || node.Folder is not { } folder)
            return $"'{node.Label}' only groups the folders inside it, so it cannot be a favorite.";
        if (folder.AccountId == Guid.Empty || folder.FullName.Length == 0 || folder.FullName[0] == '\0' ||
            Accounts.All(a => a.Id != folder.AccountId))
            return $"'{node.Label}' is a view, not a folder, so it cannot be a favorite.";
        return null;
    }

    /// <summary>True when the node's folder is a favorite. The menus' check marks read this.</summary>
    public bool IsFavoriteFolder(FolderTreeNode? node) =>
        FavoriteFolderRefusal(node) is null &&
        Accounts.FirstOrDefault(a => a.Id == node!.Folder!.AccountId) is { } account &&
        IsFavorite(account, node!.Folder!.FullName);

    private static bool IsFavorite(AccountModel account, string fullName) =>
        account.FavoriteFolders.Any(f => string.Equals(f.FullName, fullName, StringComparison.Ordinal));

    /// <summary>
    /// Adds the node's folder to Favorites, at the end, or removes it when it is there already.
    /// Returns the sentence the View reports (status bar plus a Result announcement).
    /// </summary>
    public string ToggleFavoriteFolder(FolderTreeNode? node)
    {
        if (FavoriteFolderRefusal(node) is { } refusal) return refusal;

        var folder  = node!.Folder!;
        var account = Accounts.First(a => a.Id == folder.AccountId);
        var label   = FavoriteLabel(folder, account);

        var removed = account.FavoriteFolders.RemoveAll(f =>
            string.Equals(f.FullName, folder.FullName, StringComparison.Ordinal)) > 0;
        RenumberFavorites(FavoritesInOrder());
        if (!removed)
            account.FavoriteFolders.Add(new FavoriteFolder
            {
                FullName = folder.FullName,
                AddedUtc = DateTime.UtcNow,
                Order    = Accounts.Sum(a => a.FavoriteFolders.Count),
            });

        _accountService.SaveAccounts([.. Accounts]);
        BuildFolderTree();
        return removed ? $"{label} removed from Favorites." : $"{label} added to Favorites.";
    }

    /// <summary>
    /// Moves a favorite within Favorites (#1058). Returns the sentence the View reports. Favorites
    /// whose folders have not loaded keep their places; the move passes the ones the user can see.
    /// </summary>
    public string MoveFavorite(FolderTreeNode node, TreeMove move)
    {
        var all = FavoritesInOrder();
        var index = node.Folder is { } folder
            ? all.FindIndex(e => e.Account.Id == folder.AccountId &&
                                 string.Equals(e.Entry.FullName, folder.FullName, StringComparison.Ordinal))
            : -1;
        if (index < 0) return CannotMove(node);

        var result = ListMove.Apply(all, e => ResolveFavorite(e.Account, e.Entry) is not null, index, move);
        switch (result.Outcome)
        {
            case ListMoveOutcome.NothingToReorder: return "There is only one favorite, so there is nothing to reorder.";
            case ListMoveOutcome.AlreadyAtStart:   return "Already at the start of Favorites.";
            case ListMoveOutcome.AlreadyAtEnd:     return "Already at the end of Favorites.";
        }

        RenumberFavorites(all);
        _accountService.SaveAccounts([.. Accounts]);
        BuildFolderTree();

        var (neighbourAccount, neighbourEntry) = result.Neighbour;
        var neighbourLabel = FavoriteLabel(ResolveFavorite(neighbourAccount, neighbourEntry)!, neighbourAccount);
        return $"Moved {(result.Above ? "above" : "below")} {neighbourLabel}.";
    }

    /// <summary>
    /// Drops the favorites for a folder that no longer exists at <paramref name="fullName"/> —
    /// deleted or moved in QuickMail — and for every folder inside it, the rule
    /// <see cref="ForgetHiddenFolders"/> uses. Saves and returns true when anything was dropped; the
    /// caller rebuilds the tree.
    /// </summary>
    internal bool ForgetFavoriteFolders(Guid accountId, string fullName)
    {
        if (Accounts.FirstOrDefault(a => a.Id == accountId) is not { FavoriteFolders.Count: > 0 } account) return false;
        var removed = account.FavoriteFolders.RemoveAll(f =>
            string.Equals(f.FullName, fullName, StringComparison.Ordinal) ||
            f.FullName.StartsWith(fullName + "/", StringComparison.Ordinal) ||
            f.FullName.StartsWith(fullName + ".", StringComparison.Ordinal));
        if (removed == 0) return false;
        RenumberFavorites(FavoritesInOrder());
        _accountService.SaveAccounts([.. Accounts]);
        return true;
    }

    /// <summary>True when the node is a favorite inside the Favorites section, not the folder's own node.</summary>
    public bool IsInFavoritesGroup(FolderTreeNode node) => IsInSection(node, FolderTreeOrder.Favorites);

    /// <summary>
    /// The Favorites heading with one node per favorite whose folder is loaded, or null when there is
    /// none, so the tree never shows an empty heading. A favorite whose folder is missing (an account
    /// still connecting) is skipped but kept.
    /// </summary>
    private FolderTreeNode? BuildFavoritesGroup()
    {
        FolderTreeNode? header = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (account, entry) in FavoritesInOrder())
        {
            if (ResolveFavorite(account, entry) is not { } folder) continue;
            if (!seen.Add($"{account.Id}:{folder.FullName}")) continue;
            header ??= new FolderTreeNode
            {
                IsHeader   = true,
                Label      = FavoritesLabel,
                IsExpanded = true,
                SectionKey = FolderTreeOrder.Favorites,
            };
            header.Children.Add(new FolderTreeNode { Folder = folder, Label = FavoriteLabel(folder, account) });
        }
        return header;
    }

    /// <summary>
    /// Every account's favorites, in the section's order: the arranged <see cref="FavoriteFolder.Order"/>
    /// (missing last), then when each was added, then account order, then place in the account's list.
    /// </summary>
    private List<(AccountModel Account, FavoriteFolder Entry)> FavoritesInOrder() =>
        [.. Accounts
            .SelectMany((account, accountIndex) => account.FavoriteFolders.Select((entry, position) =>
                (Account: account, Entry: entry, AccountIndex: accountIndex, Position: position)))
            .OrderBy(x => x.Entry.Order ?? int.MaxValue)
            .ThenBy(x => x.Entry.AddedUtc)
            .ThenBy(x => x.AccountIndex)
            .ThenBy(x => x.Position)
            .Select(x => (x.Account, x.Entry))];

    // Numbers every favorite from 0 in the given order, stale ones included, so they keep their places.
    private static void RenumberFavorites(List<(AccountModel Account, FavoriteFolder Entry)> inOrder)
    {
        for (var i = 0; i < inOrder.Count; i++)
            inOrder[i].Entry.Order = i;
    }

    // The live folder object, the one the account's own node holds, so the two share counts.
    private MailFolderModel? ResolveFavorite(AccountModel account, FavoriteFolder entry) =>
        _cachedFolders.TryGetValue(account.Id, out var folders)
            ? folders.FirstOrDefault(f => string.Equals(f.FullName, entry.FullName, StringComparison.Ordinal))
            : null;

    // With more than one account every favorite says whose it is: "GitHub - Kelly".
    private string FavoriteLabel(MailFolderModel folder, AccountModel account) =>
        Accounts.Count > 1 ? $"{folder.DisplayName} - {account.AccountLabel}" : folder.DisplayName;
}
