using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Builds a <see cref="FolderTreeNode"/> hierarchy from a flat list of <see cref="MailFolderModel"/> items.
/// </summary>
public static class FolderTreeBuilder
{
    public static List<FolderTreeNode> Build(IEnumerable<MailFolderModel> flat, AccountModel? account = null)
    {
        var list = flat.ToList();
        if (list.Count == 0) return [];

        // Two hierarchy models: Microsoft Graph references the parent by id (ParentId), while IMAP
        // encodes nesting in the separator-delimited FullName. Pick the strategy from the data so a
        // Graph account's sub-folders nest correctly without disturbing the IMAP path logic.
        var folderRoots = list.Any(f => f.ParentId != null)
            ? BuildByParentId(list)
            : BuildBySeparator(list);

        // Wrap folder roots under the account as the top-level tree node
        if (account != null)
        {
            var accountNode = new FolderTreeNode
            {
                Label      = account.AccountLabel,
                Folder     = null,
                IsHeader   = true,
                IsExpanded = true,
            };
            foreach (var root in folderRoots)
                accountNode.Children.Add(root);
            return [accountNode];
        }

        return folderRoots;
    }

    // ── IMAP: hierarchy encoded in the separator-delimited FullName ───────────────────
    private static List<FolderTreeNode> BuildBySeparator(List<MailFolderModel> list)
    {
        // Detect separator: use the character between the first two path segments.
        // MailKit uses '.' or '/' depending on the server's namespace separator.
        char sep = DetectSeparator(list);

        // Sort: INBOX first, then alphabetically
        list.Sort((a, b) =>
        {
            bool aInbox = a.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase);
            bool bInbox = b.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase);
            if (aInbox && !bInbox) return -1;
            if (!aInbox && bInbox) return 1;
            return string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
        });

        // Build a dictionary of path → node so we can attach children to parents
        var folderRoots = new List<FolderTreeNode>();
        var byPath = new Dictionary<string, FolderTreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in list)
        {
            var parts = folder.FullName.Split(sep);
            EnsurePath(parts, folder, sep, folderRoots, byPath);
        }

        return folderRoots;
    }

    // ── Graph: hierarchy referenced by parent id (ParentId → parent's FullName) ───────
    private static List<FolderTreeNode> BuildByParentId(List<MailFolderModel> list)
    {
        // Sort up front so both the roots and each parent's children come out in display order
        // (Inbox first, then alphabetical); the insertion order below is preserved into the tree.
        list.Sort((a, b) =>
        {
            bool aInbox = a.Kind == SpecialFolderKind.Inbox;
            bool bInbox = b.Kind == SpecialFolderKind.Inbox;
            if (aInbox && !bInbox) return -1;
            if (!aInbox && bInbox) return 1;
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        // One node per folder, keyed by its own id (FullName). Graph ids are case-sensitive.
        var byId = new Dictionary<string, FolderTreeNode>(StringComparer.Ordinal);
        foreach (var f in list)
            byId[f.FullName] = new FolderTreeNode { Folder = f, Label = BuildLabel(f) };

        var roots = new List<FolderTreeNode>();
        foreach (var f in list)
        {
            var node = byId[f.FullName];
            // A folder roots out when it has no parent, or its parent isn't part of this set —
            // top-level Graph folders point at the hidden msgfolderroot, which we never fetch.
            if (f.ParentId != null && byId.TryGetValue(f.ParentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        return roots;
    }

    private static void EnsurePath(
        string[] parts,
        MailFolderModel folder,
        char sep,
        List<FolderTreeNode> roots,
        Dictionary<string, FolderTreeNode> byPath)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            var path = string.Join(sep, parts[..(i + 1)]);

            if (byPath.ContainsKey(path)) continue;

            // Is this the leaf (the real folder) or an intermediate?
            bool isLeaf = (i == parts.Length - 1);
            MailFolderModel? folderForNode = isLeaf ? folder : null;
            string label = isLeaf
                ? BuildLabel(folder)
                : parts[i]; // intermediate — just the segment name

            var node = new FolderTreeNode { Folder = folderForNode, Label = label };
            byPath[path] = node;

            if (i == 0)
                roots.Add(node);
            else
            {
                var parentPath = string.Join(sep, parts[..i]);
                if (byPath.TryGetValue(parentPath, out var parent))
                    parent.Children.Add(node);
                else
                    roots.Add(node); // orphan — treat as root
            }
        }
    }

    private static string BuildLabel(MailFolderModel f) =>
        f.UnreadCount > 0 ? $"{f.DisplayName} ({f.UnreadCount} unread)" : f.DisplayName;

    private static char DetectSeparator(List<MailFolderModel> folders)
    {
        foreach (var f in folders)
        {
            if (f.FullName.Contains('/')) return '/';
            if (f.FullName.Contains('.')) return '.';
        }
        return '.'; // safe default
    }
}
