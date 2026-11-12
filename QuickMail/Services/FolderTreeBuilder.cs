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
    /// <param name="hidden">
    /// Full names of folders to leave out, each with everything inside it (#963). Null or empty
    /// leaves the tree whole — the destination pickers pass nothing, so a hidden folder can still
    /// be filed into.
    /// </param>
    public static List<FolderTreeNode> Build(
        IEnumerable<MailFolderModel> flat,
        AccountModel? account = null,
        IReadOnlyCollection<string>? hidden = null)
    {
        var list = flat.ToList();
        if (list.Count == 0) return [];

        // Two hierarchy models: Microsoft Graph references the parent by id (ParentId), while IMAP
        // encodes nesting in the separator-delimited FullName. Pick the strategy from the data so a
        // Graph account's sub-folders nest correctly without disturbing the IMAP path logic.
        var folderRoots = list.Any(f => f.ParentId != null)
            ? BuildByParentId(list)
            : BuildBySeparator(list);

        if (hidden is { Count: > 0 })
            PruneHidden(folderRoots, new HashSet<string>(hidden, StringComparer.Ordinal));

        // Wrap folder roots under the account as the top-level tree node
        if (account != null)
        {
            var accountNode = new FolderTreeNode
            {
                Label      = account.AccountLabel,
                Folder     = null,
                IsHeader   = true,
                IsExpanded = true,
                AccountId       = account.Id,        // #31: node-key disambiguation
                IsSharedAccount = account.IsShared,  // #31: "shared mailbox" in the accessible name
            };
            foreach (var root in folderRoots)
                accountNode.Children.Add(root);
            return [accountNode];
        }

        return folderRoots;
    }

    /// <summary>
    /// The folders of <paramref name="flat"/> that the folder tree shows once <paramref name="hidden"/>
    /// is applied, in their original order (#963). What Go to Folder lists, so it agrees with the tree
    /// about which folders are inside a hidden one under either hierarchy model.
    /// </summary>
    public static List<MailFolderModel> VisibleFolders(
        IReadOnlyList<MailFolderModel> flat, IReadOnlyCollection<string>? hidden)
    {
        if (hidden is not { Count: > 0 }) return [.. flat];
        var shown = new HashSet<MailFolderModel>(ReferenceEqualityComparer.Instance);
        foreach (var node in Flatten(Build(flat, null, hidden)))
            if (node.Folder is { } f) shown.Add(f);
        return flat.Where(shown.Contains).ToList();
    }

    private static IEnumerable<FolderTreeNode> Flatten(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    // Removes every node whose folder is hidden, with its whole branch. A path segment that is not
    // itself a folder (an IMAP "Archive" level that only exists because "Archive/2020" does) is
    // removed too when hiding left it with nothing under it: it was only there to hold what is now
    // gone, and an empty, unopenable node would be a dead end in the tree.
    private static void PruneHidden(IList<FolderTreeNode> nodes, HashSet<string> hidden)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var node = nodes[i];
            if (node.Folder is { } f && hidden.Contains(f.FullName))
            {
                nodes.RemoveAt(i);
                continue;
            }
            if (node.Children.Count == 0) continue;
            PruneHidden(node.Children, hidden);
            if (node.Folder == null && node.Children.Count == 0)
                nodes.RemoveAt(i);
        }
    }

    // ── IMAP: hierarchy encoded in the separator-delimited FullName ───────────────────
    private static List<FolderTreeNode> BuildBySeparator(List<MailFolderModel> list)
    {
        // Detect separator: use the character between the first two path segments.
        // MailKit uses '.' or '/' depending on the server's namespace separator.
        char sep = DetectSeparator(list);

        // Order well-known folders conventionally (Inbox, Drafts, Sent, Deleted, Junk), then the
        // rest alphabetically. `list` is the local copy from Build()'s flat.ToList(), so sorting it
        // in place is safe.
        list.Sort((a, b) =>
        {
            int ra = WellKnownRank(a), rb = WellKnownRank(b);
            if (ra != rb) return ra.CompareTo(rb);
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
        // (well-known folders first in conventional order, then alphabetical); the insertion order
        // below is preserved into the tree. `list` is Build()'s flat.ToList() copy — safe to sort.
        list.Sort((a, b) =>
        {
            int ra = WellKnownRank(a), rb = WellKnownRank(b);
            if (ra != rb) return ra.CompareTo(rb);
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

    // Name only — no unread count. The count is surfaced separately via the UnreadDisplay badge
    // (visual) and ItemStatusLabel (UIA ItemStatus), so baking it into the label/AutomationName here
    // would both double-render it and, crucially, go stale when counts refresh in place without a
    // tree rebuild (issue #227). Keeping the name count-free also avoids the double screen-reader
    // announcement the split into two UIA properties was designed to prevent.
    private static string BuildLabel(MailFolderModel f) => f.DisplayName;

    // Conventional mailbox order: Inbox, Drafts, Sent, Deleted, Junk, then everything else
    // alphabetically. INBOX is matched by name too — the IMAP inbox is the canonical "INBOX" and a
    // server might not flag its Kind.
    private static int WellKnownRank(MailFolderModel f)
    {
        if (f.Kind == SpecialFolderKind.Inbox ||
            f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return 0;
        return f.Kind switch
        {
            SpecialFolderKind.Drafts => 1,
            SpecialFolderKind.Sent   => 2,
            SpecialFolderKind.Trash  => 3,
            SpecialFolderKind.Junk   => 4,
            _ => 5,
        };
    }

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
