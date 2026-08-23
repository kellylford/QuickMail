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
    /// closed. Returns true when focus landed on the message.
    /// <para>When the message container still cannot be produced, focus lands on the group header
    /// instead and this returns false: somewhere inside the tree, next to what was asked for, beats
    /// leaving focus wherever it was with nothing to show for the keystroke.</para>
    /// </summary>
    /// <param name="groupIndex">The group's index in the tree's own <c>ItemsSource</c>.</param>
    /// <param name="messageIndex">The message's index within the group.</param>
    public static bool FocusMessage(
        TreeView tree, int groupIndex, object group, object message, int messageIndex)
    {
        if (groupIndex < 0 || groupIndex >= tree.Items.Count || messageIndex < 0) return false;

        // Realize the group's own container. Nothing below can happen until this exists.
        if (FindItemsHost(tree) is not { } rootPanel) return false;
        rootPanel.BringIndexIntoViewPublic(groupIndex);
        tree.UpdateLayout();

        if (tree.ItemContainerGenerator.ContainerFromItem(group) is not TreeViewItem groupTvi)
            return false;

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
            return true;
        }

        groupTvi.IsSelected = true;
        groupTvi.Focus();
        return false;
    }

    /// <summary>
    /// The virtualizing panel holding <paramref name="root"/>'s <em>own</em> items.
    /// <para>The <c>IsItemsHost</c> test is what keeps this from returning a panel that belongs to
    /// something nested in an item's template. Depth-first order alone is not enough of a guarantee
    /// to rest on: a header template that ever gained an items control would silently start
    /// answering here.</para>
    /// </summary>
    public static VirtualizingStackPanel? FindItemsHost(DependencyObject? root)
    {
        if (root == null) return null;
        if (root is VirtualizingStackPanel { IsItemsHost: true } host) return host;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FindItemsHost(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
