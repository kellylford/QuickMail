using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// Shared TreeView focus and type-ahead utilities used by both
/// <see cref="MainWindow"/> and <see cref="FolderPickerWindow"/>.
/// </summary>
public static class TreeViewFocusHelper
{
    /// <summary>
    /// Recursively yields visible (expanded) <see cref="FolderTreeNode"/> items
    /// in depth-first order.
    /// </summary>
    public static IEnumerable<FolderTreeNode> GetVisibleTreeNodes(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.IsExpanded && node.Children.Count > 0)
                foreach (var child in GetVisibleTreeNodes(node.Children))
                    yield return child;
        }
    }

    /// <summary>
    /// Walks the TreeView container hierarchy to find and select the target node.
    /// </summary>
    /// <param name="parent">The root <see cref="ItemsControl"/> to search.</param>
    /// <param name="target">The data item to select.</param>
    /// <param name="focusNode">When true, keyboard focus moves into the selected container.</param>
    /// <returns>True if the node was found and selected.</returns>
    public static bool SelectTreeViewNode(ItemsControl parent, FolderTreeNode target, bool focusNode = true)
    {
        foreach (var item in parent.Items)
        {
            if (item is not FolderTreeNode node) continue;
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;

            if (node == target)
            {
                container.IsSelected = true;
                container.BringIntoView();
                if (focusNode)
                    container.Focus();
                return true;
            }
            if (node.IsExpanded && SelectTreeViewNode(container, target, focusNode))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Type-ahead over the visible (expanded) nodes of a folder tree: selects the next node
    /// after the current selection whose <see cref="FolderTreeNode.Label"/> starts with
    /// <paramref name="prefix"/>, wrapping around. Used by MainWindow's folder tree and the
    /// folder picker's tree view. WPF's built-in <c>TextSearch</c> is not an alternative here:
    /// it is disabled by default on <c>TreeView</c>/<c>TreeViewItem</c>, and even when enabled
    /// it only matches a single level's items, never the visible tree as a whole.
    /// </summary>
    /// <returns>True if a matching node was found and selected.</returns>
    public static bool TrySelectNextMatch(TreeView tree, IEnumerable<FolderTreeNode> roots, string prefix)
    {
        var visible = GetVisibleTreeNodes(roots).ToList();
        var startIdx = tree.SelectedItem is FolderTreeNode current ? visible.IndexOf(current) : -1;
        var idx = TypeAheadMatcher.FindNext(visible, n => n.Label, startIdx, prefix);
        return idx >= 0 && SelectTreeViewNode(tree, visible[idx]);
    }

    /// <summary>
    /// Extracts a single-character type-ahead search string from a key event.
    /// Returns false when modifiers are held or the key is not a letter/digit.
    /// </summary>
    public static bool TryGetTypeAheadKeyText(KeyEventArgs e, out string text)
    {
        text = string.Empty;

        if (Keyboard.Modifiers != ModifierKeys.None)
            return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is >= Key.A and <= Key.Z)
        {
            text = key.ToString();
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            text = ((char)('0' + (key - Key.D0))).ToString();
            return true;
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            text = ((char)('0' + (key - Key.NumPad0))).ToString();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds a <see cref="FolderTreeNode"/> whose <see cref="FolderTreeNode.Folder"/>
    /// matches <paramref name="target"/>, searching recursively.
    /// </summary>
    public static FolderTreeNode? FindFolderTreeNode(IEnumerable<FolderTreeNode> nodes, MailFolderModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Folder != null && FoldersMatch(node.Folder, target))
                return node;

            var child = FindFolderTreeNode(node.Children, target);
            if (child != null)
                return child;
        }

        return null;
    }

    /// <summary>
    /// Expands all ancestor nodes along the path to <paramref name="target"/>
    /// so their children's item containers are generated before selection.
    /// </summary>
    /// <returns>True if the target was found in the tree.</returns>
    public static bool FindAndExpandFolderPath(IEnumerable<FolderTreeNode> nodes, MailFolderModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Folder != null && FoldersMatch(node.Folder, target))
                return true;

            if (FindAndExpandFolderPath(node.Children, target))
            {
                node.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares two <see cref="MailFolderModel"/> instances for equality.
    /// Treats <see cref="Guid.Empty"/> as a wildcard that matches any account.
    /// </summary>
    public static bool FoldersMatch(MailFolderModel a, MailFolderModel b) =>
        a.FullName.Equals(b.FullName, StringComparison.OrdinalIgnoreCase) &&
        (a.AccountId == b.AccountId || a.AccountId == Guid.Empty || b.AccountId == Guid.Empty);
}
