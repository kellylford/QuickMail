using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace QuickMail.Helpers;

/// <summary>
/// Puts selection and focus on one message inside one group of a virtualized grouped tree
/// (Conversations, From, To), creating the containers it needs on the way.
///
/// <para>
/// The obvious approach — scroll the target into the viewport and let the layout pass generate the
/// container — does not work here, and quietly did nothing for years. A <c>TreeView</c> showing a
/// <c>HierarchicalDataTemplate</c> uses hierarchical virtualization, which scrolls in <em>pixels</em>
/// whatever <c>ScrollViewer.CanContentScroll</c> says: measured on the real templates,
/// <c>VirtualizingPanel.GetScrollUnit</c> reports <c>Pixel</c> and the extent works out at about
/// 48px per group row. Code that computed a row index and passed it to
/// <c>ScrollToVerticalOffset</c> therefore moved the viewport by roughly one row when it meant
/// sixty, the container it was waiting for never appeared, and the caller gave up without moving
/// focus and without saying anything. Worse, on an already-correct viewport that same call dragged
/// the tree back toward the top. See <c>TreeViewItemRealizerTests</c>, which pins the pixel-unit
/// fact so a future change cannot re-tempt anyone into the index-offset version.
/// </para>
///
/// <para>
/// <see cref="VirtualizingStackPanel.BringIndexIntoViewPublic"/> is the supported way to ask a
/// virtualizing panel for a container by index. It is synchronous, so no dispatcher retry ladder is
/// needed — which also means the caller finds out whether it worked.
/// </para>
/// </summary>
public static class TreeViewItemRealizer
{
    /// <summary>
    /// Selects and focuses <paramref name="message"/>, expanding <paramref name="group"/> if it is
    /// closed.
    /// <para>The three outcomes are distinguished because the caller has to treat them differently:
    /// only <see cref="GroupedMessageFocus.None"/> leaves focus where it was, and only that case
    /// needs anything said about it. Landing on the header is a focus move the platform reports on
    /// its own.</para>
    /// </summary>
    /// <param name="groupIndex">The group's index in the tree's own <c>ItemsSource</c>.</param>
    /// <param name="messageIndex">The message's index within the group.</param>
    public static GroupedMessageFocus FocusMessage(
        TreeView tree, int groupIndex, object group, object message, int messageIndex)
    {
        if (groupIndex < 0 || groupIndex >= tree.Items.Count || messageIndex < 0)
            return GroupedMessageFocus.None;

        // Realize the group's own container. Nothing below can happen until this exists.
        if (FindItemsHost(tree) is not { } rootPanel) return GroupedMessageFocus.None;
        rootPanel.BringIndexIntoViewPublic(groupIndex);
        tree.UpdateLayout();

        if (tree.ItemContainerGenerator.ContainerFromItem(group) is not TreeViewItem groupTvi)
            return GroupedMessageFocus.None;

        if (!groupTvi.IsExpanded)
        {
            groupTvi.IsExpanded = true;
            tree.UpdateLayout();   // generates the child items host
        }

        // The child panel exists only once the group is expanded, and virtualizes independently.
        if (FindItemsHost(groupTvi) is { } childPanel && messageIndex < groupTvi.Items.Count)
        {
            childPanel.BringIndexIntoViewPublic(messageIndex);
            tree.UpdateLayout();
        }

        if (groupTvi.ItemContainerGenerator.ContainerFromItem(message) is TreeViewItem msgTvi)
        {
            msgTvi.IsSelected = true;
            msgTvi.Focus();
            msgTvi.BringIntoView();
            return GroupedMessageFocus.Message;
        }

        // Somewhere inside the tree, next to what was asked for, beats leaving focus where it was
        // with nothing to show for the keystroke.
        groupTvi.IsSelected = true;
        groupTvi.Focus();
        return GroupedMessageFocus.Group;
    }

    /// <summary>
    /// The virtualizing panel holding <paramref name="owner"/>'s <em>own</em> items.
    /// <para>The ownership test is <c>ItemsControl.GetItemsOwner</c>, not <c>IsItemsHost</c>: a panel
    /// nested inside an item's template is the items host of <em>that</em> control, so
    /// <c>IsItemsHost</c> is true for it too and would not exclude it. Nor does depth-first order
    /// save us — the real <c>TreeViewItem</c> template puts <c>PART_Header</c> before
    /// <c>ItemsHost</c>, so a header that ever gained a list would be reached first and
    /// <c>BringIndexIntoViewPublic</c> would then be called on a stranger's panel, throwing out of a
    /// key handler as soon as that list held fewer items than the group.</para>
    /// </summary>
    public static VirtualizingStackPanel? FindItemsHost(ItemsControl owner) => Search(owner);

    private static VirtualizingStackPanel? Search(DependencyObject node, ItemsControl? owner = null)
    {
        owner ??= node as ItemsControl;
        if (node is VirtualizingStackPanel panel
            && ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner))
            return panel;

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            if (Search(VisualTreeHelper.GetChild(node, i), owner) is { } found) return found;
        return null;
    }
}

/// <summary>Where <see cref="TreeViewItemRealizer.FocusMessage"/> left keyboard focus.</summary>
public enum GroupedMessageFocus
{
    /// <summary>Focus did not move. The only outcome that needs announcing.</summary>
    None,
    /// <summary>Focus landed on the group header — the message container could not be produced.</summary>
    Group,
    /// <summary>Focus landed on the message, which is what was asked for.</summary>
    Message,
}
