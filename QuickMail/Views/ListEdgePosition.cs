using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// Where the focused row sits among the rows a person can arrow through, for
/// <see cref="Helpers.ListEdgeCue"/> (issue #923). Kept out of MainWindow so it can be tested
/// against a real ListView and TreeView without standing up the whole window.
/// <para>Index -1 means no row of this list has focus: focus is on the list itself, or elsewhere.
/// Then the keys go to a row rather than nowhere, so it is never an edge.</para>
/// </summary>
public static class ListEdgePosition
{
    /// <summary>The focused row of a flat list, and how many rows it has.</summary>
    public static (int Index, int Count) InList(ListView list, IInputElement? focused)
    {
        var count = list.Items.Count;
        if (focused is not ListViewItem row || ItemsControl.ItemsControlFromItemContainer(row) != list) return (-1, count);
        return (list.ItemContainerGenerator.IndexFromContainer(row), count);
    }

    /// <summary>
    /// The focused row of a tree among <paramref name="visible"/>, its rows in arrow order
    /// (<see cref="VisibleRows(IEnumerable{ConversationGroup})"/>), and how many there are.
    /// </summary>
    public static (int Index, int Count) InTree(TreeView tree, List<object> visible, IInputElement? focused)
    {
        if (focused is not TreeViewItem row || !IsInside(tree, row)) return (-1, visible.Count);
        var item = row.DataContext;
        for (var i = 0; i < visible.Count; i++)
            if (ReferenceEquals(visible[i], item)) return (i, visible.Count);
        return (-1, visible.Count);
    }

    /// <summary>A conversation tree's rows in arrow order: each group, then its messages when it is expanded.</summary>
    public static IEnumerable<object> VisibleRows(IEnumerable<ConversationGroup> groups)
    {
        foreach (var group in groups)
        {
            yield return group;
            if (!group.IsExpanded) continue;
            foreach (var msg in group.Messages)
                yield return msg;
        }
    }

    /// <summary>A From or To tree's rows in arrow order: each group, then its messages when it is expanded.</summary>
    public static IEnumerable<object> VisibleRows(IEnumerable<SenderGroup> groups)
    {
        foreach (var group in groups)
        {
            yield return group;
            if (!group.IsExpanded) continue;
            foreach (var msg in group.Messages)
                yield return msg;
        }
    }

    private static bool IsInside(DependencyObject ancestor, DependencyObject descendant)
    {
        for (DependencyObject? current = descendant; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
