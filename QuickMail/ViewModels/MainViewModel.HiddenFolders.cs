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
    /// <summary>
    /// What the View says after a folder is hidden, as a Hint so it respects the user's hint setting.
    /// Kept out of the Result sentence <see cref="HideFolder"/> returns.
    /// </summary>
    public const string HiddenFoldersHint = "Show All Hidden Folders, on the Folder menu, brings hidden folders back.";

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
                result[accountId] = FolderTreeBuilder.VisibleFolders(folders, HiddenFoldersOf(accountId));
            return result;
        }
    }

    private List<string>? HiddenFoldersOf(Guid accountId) =>
        Accounts.FirstOrDefault(a => a.Id == accountId)?.HiddenFolders;

    /// <summary>
    /// Why <paramref name="node"/> cannot be hidden, or null when it can. Kept apart from
    /// <see cref="HideFolder"/> so the View can refuse before it touches focus.
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
    /// <para>
    /// The node may be a folder listed inside a saved view. Hiding acts on the folder, so it goes from
    /// under its account; the view keeps it, because the user put it there by name, and the sentence
    /// says so rather than leave them wondering why it is still in front of them.
    /// </para>
    /// </summary>
    public string HideFolder(FolderTreeNode? node)
    {
        if (HideFolderRefusal(node) is { } refusal) return refusal;

        var folder  = node!.Folder!;
        var account = Accounts.First(a => a.Id == folder.AccountId);
        // Counted on the account's own node: a view's copy of the folder has no children.
        var inside = AccountTreeNodeFor(folder) is { } own ? CountFoldersInside(own) : 0;

        if (!account.HiddenFolders.Contains(folder.FullName, StringComparer.Ordinal))
            account.HiddenFolders.Add(folder.FullName);
        _accountService.SaveAccounts([.. Accounts]);
        BuildFolderTree();

        var what = inside switch
        {
            0 => $"{node.Label} hidden.",
            1 => $"{node.Label} and the folder inside it hidden.",
            _ => $"{node.Label} and the {inside} folders inside it hidden.",
        };
        if (IsInViewsGroup(node))
            what += " It still shows in the saved view, which lists it by name.";
        return what;
    }

    /// <summary>
    /// Brings back every hidden folder in every account. Returns the sentence the View reports.
    /// The number is folders, not entries: hiding Archive with two folders inside it brings three back.
    /// </summary>
    public string ShowAllHiddenFolders()
    {
        if (Accounts.All(a => a.HiddenFolders.Count == 0)) return "No folders are hidden.";

        var folders = 0;
        foreach (var (accountId, cached) in _cachedFolders)
            folders += cached.Count - FolderTreeBuilder.VisibleFolders(cached, HiddenFoldersOf(accountId)).Count;
        foreach (var account in Accounts) account.HiddenFolders.Clear();

        _accountService.SaveAccounts([.. Accounts]);
        BuildFolderTree();
        return folders switch
        {
            // Entries whose folders this session has not loaded (an account still connecting).
            0 => "All hidden folders shown again.",
            1 => "1 hidden folder shown again.",
            _ => $"{folders} hidden folders shown again.",
        };
    }

    /// <summary>
    /// Drops the hidden entries for a folder that no longer exists at <paramref name="fullName"/> —
    /// deleted or moved — and for every folder inside it, so a folder created at that path later is
    /// not hidden from birth. Inside means an IMAP path continuing with '/' or '.' (the two separators
    /// QuickMail meets); a Graph folder's children have unrelated ids, and their entries simply stop
    /// matching anything.
    /// </summary>
    internal void ForgetHiddenFolders(Guid accountId, string fullName)
    {
        if (Accounts.FirstOrDefault(a => a.Id == accountId) is not { HiddenFolders.Count: > 0 } account) return;
        var removed = account.HiddenFolders.RemoveAll(h =>
            string.Equals(h, fullName, StringComparison.Ordinal) ||
            h.StartsWith(fullName + "/", StringComparison.Ordinal) ||
            h.StartsWith(fullName + ".", StringComparison.Ordinal));
        if (removed > 0) _accountService.SaveAccounts([.. Accounts]);
    }

    /// <summary>True when the node is part of the folder tree's Views group, not of an account.</summary>
    public bool IsInViewsGroup(FolderTreeNode node)
    {
        if (FolderTree == null) return false;
        foreach (var root in FolderTree)
            if (root.IsHeader && root.AccountId is null && root.Label == "Views" && Contains(root, node))
                return true;
        return false;

        static bool Contains(FolderTreeNode parent, FolderTreeNode target) =>
            parent.Children.Any(c => ReferenceEquals(c, target) || Contains(c, target));
    }

    // The node for this folder under its own account header, as opposed to a saved view's copy.
    private FolderTreeNode? AccountTreeNodeFor(MailFolderModel folder)
    {
        var header = FolderTree?.FirstOrDefault(n => n.IsHeader && n.AccountId == folder.AccountId);
        return header == null ? null : Find(header);

        FolderTreeNode? Find(FolderTreeNode parent)
        {
            foreach (var c in parent.Children)
            {
                if (c.Folder is { } f && f.AccountId == folder.AccountId &&
                    string.Equals(f.FullName, folder.FullName, StringComparison.Ordinal))
                    return c;
                if (Find(c) is { } found) return found;
            }
            return null;
        }
    }

    private static int CountFoldersInside(FolderTreeNode node)
    {
        var count = 0;
        foreach (var child in node.Children)
            count += (child.Folder != null ? 1 : 0) + CountFoldersInside(child);
        return count;
    }
}
