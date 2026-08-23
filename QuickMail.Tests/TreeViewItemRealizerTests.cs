using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Guards <see cref="TreeViewItemRealizer"/> against the fault that produced it (issue #617):
/// landing focus on a message inside a group that virtualization has not built a container for.
///
/// <para>The tree here is configured exactly as the three real grouped trees are in
/// MainWindow.xaml — <c>IsVirtualizing</c>, <c>VirtualizationMode=Recycling</c>,
/// <c>CanContentScroll=True</c>, a <c>HierarchicalDataTemplate</c>, and <c>IsExpanded</c> bound
/// TwoWay in the container style — because every one of those is load-bearing for what is asserted.</para>
/// </summary>
public class TreeViewItemRealizerTests
{
    private sealed class Group
    {
        public string Name { get; init; } = "";
        public List<string> Messages { get; init; } = [];
        public bool IsExpanded { get; set; }
    }

    private static List<Group> BuildGroups(int groups, int perGroup) =>
        Enumerable.Range(0, groups).Select(g => new Group
        {
            Name     = $"g{g}",
            Messages = Enumerable.Range(0, perGroup).Select(m => $"g{g}m{m}").ToList(),
        }).ToList();

    // Mirrors the real trees' configuration. Returns the tree and the window holding it; the
    // window must be shown, because virtualization only realizes containers during a real layout.
    private static (TreeView Tree, Window Window) BuildTree(List<Group> groups)
    {
        var tree = new TreeView { ItemsSource = groups };
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(tree, true);

        var rowFactory = new FrameworkElementFactory(typeof(StackPanel));
        for (var i = 0; i < 3; i++)   // a three-line row, as the real templates have
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            rowFactory.AppendChild(text);
        }
        tree.ItemTemplate = new HierarchicalDataTemplate
        {
            ItemsSource = new Binding("Messages"),
            VisualTree  = rowFactory,
        };

        var containerStyle = new Style(typeof(TreeViewItem));
        containerStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
            new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
        tree.ItemContainerStyle = containerStyle;

        var window = new Window
        {
            Width = 500, Height = 600, Content = tree,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Left = -4000, Top = -4000,      // off-screen: this must not steal the user's display
        };
        window.Show();
        tree.UpdateLayout();
        return (tree, window);
    }

    /// <summary>
    /// The fact the whole helper exists for. A TreeView showing a HierarchicalDataTemplate scrolls
    /// in pixels even with CanContentScroll="True", so a row index handed to ScrollToVerticalOffset
    /// moves the viewport by about one row instead of by that many rows. If this ever starts
    /// reporting Item, the far simpler scroll-and-wait approach becomes viable again — but until
    /// then, code that scrolls by row index is silently broken.
    /// </summary>
    [StaFact]
    public void HierarchicalTreeScrollsInPixels_NotRows()
    {
        var (tree, window) = BuildTree(BuildGroups(100, 20));
        try
        {
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(tree));

            var scroller = tree.Template.FindName("_tv_scrollviewer_", tree) as ScrollViewer;
            Assert.NotNull(scroller);
            // 100 rows in an extent far larger than 100 is the same statement in numbers.
            Assert.True(scroller!.ExtentHeight > 100 * 4,
                $"extent {scroller.ExtentHeight} looks item-based, not pixel-based");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void FocusMessage_LandsOnAMessageInAGroupBelowTheViewport()
    {
        var groups = BuildGroups(100, 20);
        var (tree, window) = BuildTree(groups);
        try
        {
            // Nothing near group 60 is realized at rest — that is the state the old code gave up in.
            Assert.Null(tree.ItemContainerGenerator.ContainerFromItem(groups[60]));

            var landed = TreeViewItemRealizer.FocusMessage(
                tree, groupIndex: 60, groups[60], groups[60].Messages[7], messageIndex: 7);

            Assert.True(landed);
            var groupTvi = Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(groups[60]));
            var msgTvi   = Assert.IsType<TreeViewItem>(groupTvi.ItemContainerGenerator.ContainerFromItem(groups[60].Messages[7]));
            Assert.True(msgTvi.IsSelected);
            Assert.Same(msgTvi, Keyboard.FocusedElement);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void FocusMessage_ExpandsAClosedGroup()
    {
        var groups = BuildGroups(40, 10);
        var (tree, window) = BuildTree(groups);
        try
        {
            Assert.False(groups[30].IsExpanded);

            var landed = TreeViewItemRealizer.FocusMessage(
                tree, groupIndex: 30, groups[30], groups[30].Messages[4], messageIndex: 4);

            Assert.True(landed);
            // TwoWay-bound, so the model records it and the expansion survives a rebuild.
            Assert.True(groups[30].IsExpanded);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void FocusMessage_ReachesTheLastMessageOfTheLastGroup()
    {
        var groups = BuildGroups(100, 20);
        var (tree, window) = BuildTree(groups);
        try
        {
            var landed = TreeViewItemRealizer.FocusMessage(
                tree, groupIndex: 99, groups[99], groups[99].Messages[19], messageIndex: 19);

            Assert.True(landed);
        }
        finally { window.Close(); }
    }

    [StaTheory]
    [InlineData(-1, 0)]     // group not in the collection (IndexOf miss)
    [InlineData(100, 0)]    // group index past the end
    [InlineData(5, -1)]     // negative message index
    public void FocusMessage_OutOfRangeIsRefused_NotThrown(int groupIndex, int messageIndex)
    {
        var groups = BuildGroups(100, 20);
        var (tree, window) = BuildTree(groups);
        try
        {
            Assert.False(TreeViewItemRealizer.FocusMessage(tree, groupIndex, groups[0], "nope", messageIndex));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void FindItemsHost_ReturnsTheTreesOwnPanel_NotANestedOne()
    {
        var groups = BuildGroups(10, 5);
        var (tree, window) = BuildTree(groups);
        try
        {
            var host = TreeViewItemRealizer.FindItemsHost(tree);
            Assert.NotNull(host);
            Assert.True(host!.IsItemsHost);
            // The tree's own host holds the group containers, one per root item.
            Assert.IsType<TreeViewItem>(host.Children[0]);
            Assert.Same(tree.ItemContainerGenerator.ContainerFromIndex(0), host.Children[0]);
        }
        finally { window.Close(); }
    }
}
