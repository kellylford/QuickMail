using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Hidden folders (#963): a user can take folders they never use out of the folder tree and Go to
/// Folder, and bring them all back. Display only — a hidden folder still syncs, still counts in the
/// aggregate views, and is still offered as a destination in Move, Copy and rule pickers. The list
/// lives on each account (<see cref="AccountModel.HiddenFolders"/>), so it goes with the account.
/// </summary>
public partial class MainViewModel
{
    /// <summary>True when any account has a hidden folder.</summary>
    public bool HasHiddenFolders => Accounts.Any(a => a.HiddenFolders.Count > 0);

    /// <summary>
    /// <see cref="CachedFolders"/> without the hidden folders and everything inside them: what Go to
    /// Folder lists, so it never offers a folder the tree does not show.
    /// </summary>
    public IReadOnlyDictionary<Guid, List<MailFolderModel>> VisibleCachedFolders
    {
        get
        {
            var result = new Dictionary<Guid, List<MailFolderModel>>();
            foreach (var (accountId, folders) in _cachedFolders)
            {
                var hidden = Accounts.FirstOrDefault(a => a.Id == accountId)?.HiddenFolders;
                result[accountId] = FolderTreeBuilder.VisibleFolders(folders, hidden);
            }
            return result;
        }
    }

    /// <summary>
    /// Why <paramref name="node"/> cannot be hidden, or null when it can. Kept apart from
    /// <see cref="HideFolder"/> so the View can refuse before it moves focus.
    /// </summary>
    public string? HideFolderRefusal(FolderTreeNode? node)
    {
        if (node is null)
            return "Select a folder in the folder tree first.";
        if (node.IsCalendarNode || (node.Folder is { } cal && IsCalendarFolderName(cal.FullName)))
            return $"'{node.Label}' is a calendar, not a mail folder, so it cannot be hidden.";
        if (node.IsHeader && node.AccountId is not null)
            return $"'{node.Label}' is an account, not a folder, so it cannot be hidden.";
        if (node.IsHeader || node.Folder is not { } folder)
            return $"'{node.Label}' only groups the folders inside it, so it cannot be hidden. Hide those folders instead.";
        if (folder.AccountId == Guid.Empty || folder.FullName.Length == 0 || folder.FullName[0] == '\0' ||
            Accounts.All(a => a.Id != folder.AccountId))
            return $"'{node.Label}' is a view, not a folder, so it cannot be hidden.";
        // On servers that keep every folder under INBOX (INBOX.Sent, INBOX.Drafts, …) hiding the
        // Inbox would hide the whole account, and the Inbox is the one folder every account needs.
        if (folder.Kind == SpecialFolderKind.Inbox ||
            folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return "The Inbox cannot be hidden.";
        return null;
    }

    /// <summary>
    /// Hides the node's folder, with every folder inside it, and rebuilds the tree without it.
    /// Returns the sentence the View reports (status bar plus a Result announcement).
    /// </summary>
    public string HideFolder(FolderTreeNode? node)
    {
        if (HideFolderRefusal(node) is { } refusal) return refusal;

        var folder  = node!.Folder!;
        var account = Accounts.First(a => a.Id == folder.AccountId);
        if (!account.HiddenFolders.Contains(folder.FullName, StringComparer.Ordinal))
            account.HiddenFolders.Add(folder.FullName);
        _accountService.SaveAccounts([.. Accounts]);

        var inside = CountFoldersInside(node);
        BuildFolderTree();
        OnPropertyChanged(nameof(HasHiddenFolders));

        var what = inside switch
        {
            0 => $"{node.Label} hidden.",
            1 => $"{node.Label} and the folder inside it hidden.",
            _ => $"{node.Label} and the {inside} folders inside it hidden.",
        };
        return what + " Show All Hidden Folders brings it back.";
    }

    /// <summary>
    /// Brings back every hidden folder in every account. Returns the sentence the View reports.
    /// </summary>
    public string ShowAllHiddenFolders()
    {
        var count = 0;
        foreach (var account in Accounts)
        {
            count += account.HiddenFolders.Count;
            account.HiddenFolders.Clear();
        }
        if (count == 0) return "No folders are hidden.";

        _accountService.SaveAccounts([.. Accounts]);
        BuildFolderTree();
        OnPropertyChanged(nameof(HasHiddenFolders));
        return count == 1 ? "1 hidden folder shown again." : $"{count} hidden folders shown again.";
    }

    private static int CountFoldersInside(FolderTreeNode node)
    {
        var count = 0;
        foreach (var child in node.Children)
            count += (child.Folder != null ? 1 : 0) + CountFoldersInside(child);
        return count;
    }
}
