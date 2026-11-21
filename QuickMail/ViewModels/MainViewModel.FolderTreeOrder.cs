using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Arranging the folder tree (#1058). The top level is one list of sections (Calendar, Favorites,
/// Views, All Mail) and accounts, and each moves past whatever is beside it; favorites move within
/// Favorites. The order is config.ini's <c>FolderTreeOrder</c>: section keys plus one placeholder per
/// account, filled in accounts.json order, so moving an account past another account reorders
/// accounts.json — the Accounts list and the compose From list follow — while moving it past a
/// section only changes where it sits in the tree.
/// </summary>
public partial class MainViewModel
{
    // As read from config.ini, never normalized in place: accounts load after the first tree is
    // built, and normalizing against none would throw away where the user put them.
    private List<string> _folderTreeOrderStored = [];

    /// <summary>The top-level order made whole for the current accounts.</summary>
    internal List<string> CurrentFolderTreeOrder() =>
        FolderTreeOrder.Normalize(_folderTreeOrderStored, Accounts.Count);

    /// <summary>
    /// Moves a folder tree item: an account or a section among the top level, or a favorite within
    /// Favorites. Returns the sentence the View reports (status bar plus a Result announcement).
    /// </summary>
    public string MoveFolderTreeNode(FolderTreeNode? node, TreeMove move)
    {
        if (node is null) return "Select an item in the folder tree first.";
        if (!node.IsHeader && node.Folder != null && IsInFavoritesGroup(node)) return MoveFavorite(node, move);
        if (node.SectionKey != null || (node.IsHeader && node.AccountId != null)) return MoveTopLevel(node, move);
        return CannotMove(node);
    }

    private static string CannotMove(FolderTreeNode node) =>
        $"'{node.Label}' cannot be moved. Only accounts, sections and favorites can be moved.";

    private sealed record TopLevelSlot(string? Section, AccountModel? Account);

    private string MoveTopLevel(FolderTreeNode node, TreeMove move)
    {
        var slots = new List<TopLevelSlot>();
        var nextAccount = 0;
        var tokensBefore = CurrentFolderTreeOrder();
        foreach (var token in tokensBefore)
            slots.Add(token == FolderTreeOrder.Account
                ? new TopLevelSlot(null, Accounts[nextAccount++])
                : new TopLevelSlot(token, null));

        var index = node.SectionKey is { } key
            ? slots.FindIndex(s => s.Section == key)
            : slots.FindIndex(s => s.Account?.Id == node.AccountId);
        if (index < 0) return CannotMove(node);

        var result = ListMove.Apply(slots, IsShowing, index, move);
        switch (result.Outcome)
        {
            case ListMoveOutcome.NothingToReorder: return "There is nothing else in the folder list to move it past.";
            case ListMoveOutcome.AlreadyAtStart:   return "Already at the top of the folder list.";
            case ListMoveOutcome.AlreadyAtEnd:     return "Already at the bottom of the folder list.";
        }

        // Accounts that passed each other change places in accounts.json too.
        var accountOrder = slots.Where(s => s.Account != null).Select(s => s.Account!).ToList();
        var accountsMoved = !accountOrder.SequenceEqual(Accounts);
        if (accountsMoved)
        {
            for (var i = 0; i < accountOrder.Count; i++)
            {
                var from = Accounts.IndexOf(accountOrder[i]);
                if (from != i) Accounts.Move(from, i);
            }
            _accountService.SaveAccounts([.. Accounts]);
        }

        // An account passing the account beside it changes only accounts.json: the placeholders stay
        // where they were, so a hidden section between the two keeps its place rather than shifting.
        var accountPassedAccount = node.SectionKey == null && (move is TreeMove.Up or TreeMove.Down) &&
                                   result.Neighbour!.Account != null;
        if (!accountPassedAccount)
            SaveFolderTreeOrder(slots.Select(s => s.Section ?? FolderTreeOrder.Account));

        // The flat folder list follows account order too, so a change there rebuilds both, as an
        // account move from the Accounts list does. Either way the live tree is merged, not
        // replaced, so the moved node is the same object afterwards (#719).
        if (accountsMoved) RebuildFolderListFromCache();
        else BuildFolderTree();

        return $"Moved {(result.Above ? "above" : "below")} {SlotLabel(result.Neighbour!)}.";

        bool IsShowing(TopLevelSlot slot) =>
            slot.Account != null || FolderTree?.Any(r => r.SectionKey == slot.Section) == true;
    }

    private static string SlotLabel(TopLevelSlot slot) => slot.Account?.AccountLabel ?? slot.Section switch
    {
        FolderTreeOrder.Calendar  => CalendarFolder.DisplayName,
        FolderTreeOrder.Favorites => FavoritesLabel,
        FolderTreeOrder.Views     => ViewsLabel,
        _                         => AllMailLabel,
    };

    private void SaveFolderTreeOrder(IEnumerable<string> tokens)
    {
        _folderTreeOrderStored = [.. tokens];
        var cfg = _configService.Load();
        cfg.FolderTreeOrder = FolderTreeOrder.Format(_folderTreeOrderStored);
        _configService.Save(cfg);
    }

    /// <summary>
    /// Takes the removed accounts' placeholders out of the stored order, so the accounts after them
    /// keep their places. Called before the accounts leave <see cref="Accounts"/>; without it,
    /// normalizing would remove the last placeholder instead, and every account after the removed one
    /// would shift up a place. A default order stays default.
    /// </summary>
    private void DropAccountsFromFolderTreeOrder(IReadOnlyCollection<AccountModel> removed)
        => DropAccountsFromFolderTreeOrder([.. Accounts], removed.Select(a => a.Id).ToHashSet());

    /// <summary>
    /// The same, for accounts removed elsewhere — Manage Accounts — found by comparing the list from
    /// before it opened with the one read back afterwards.
    /// </summary>
    private void DropRemovedAccountsFromFolderTreeOrder(IReadOnlyList<AccountModel> before)
    {
        var now = Accounts.Select(a => a.Id).ToHashSet();
        var removed = before.Where(a => !now.Contains(a.Id)).Select(a => a.Id).ToHashSet();
        if (removed.Count > 0) DropAccountsFromFolderTreeOrder(before, removed);
    }

    // `order` is the account list the stored placeholders were filled from.
    private void DropAccountsFromFolderTreeOrder(IReadOnlyList<AccountModel> order, HashSet<Guid> removed)
    {
        if (_folderTreeOrderStored.Count == 0) return;
        var tokens = FolderTreeOrder.Normalize(_folderTreeOrderStored, order.Count);
        var accountIndex = 0;
        var kept = new List<string>();
        foreach (var token in tokens)
        {
            if (token == FolderTreeOrder.Account && removed.Contains(order[accountIndex++].Id)) continue;
            kept.Add(token);
        }
        SaveFolderTreeOrder(kept);
    }
}
