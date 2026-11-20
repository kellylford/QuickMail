// Issue #923: where the focused row is, for the top/bottom cue. Against a bare ListView and TreeView
// rather than MainWindow, for the reason SelectorItemAccessibilityTests gives: showing MainWindow
// brings up WebView2, the tray icon and timers on the test's STA thread (#211). The containers are
// handed in as the focused element, so the tests don't depend on real keyboard focus.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ListEdgePositionTests
{
    private static MailMessageSummary Msg(string subject) =>
        new() { From = "Chris Lee", Subject = subject, Date = DateTimeOffset.Now, IsRead = true };

    private static Window Host(FrameworkElement content)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 600, Height = 400, Content = content,
        };
        window.Show();
        content.UpdateLayout();
        Drain();
        return window;
    }

    // ── Flat list ──────────────────────────────────────────────────────────────

    [StaFact]
    public void AFocusedRowOfTheListIsItsIndex()
    {
        var items = new[] { Msg("one"), Msg("two"), Msg("three") };
        var list = new ListView { ItemsSource = items };
        var window = Host(list);
        try
        {
            var first = list.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
            var last = list.ItemContainerGenerator.ContainerFromIndex(2) as ListViewItem;
            Assert.NotNull(first);
            Assert.NotNull(last);

            Assert.Equal((0, 3), ListEdgePosition.InList(list, first));
            Assert.Equal((2, 3), ListEdgePosition.InList(list, last));
            Assert.Equal(ListEdge.Top, ListEdgeCue.At(Key.Home, ModifierKeys.None, 0, 3));
            Assert.Equal(ListEdge.Bottom, ListEdgeCue.At(Key.End, ModifierKeys.None, 2, 3));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void FocusOnTheListItselfOrNowhereIsNoRow()
    {
        var list = new ListView { ItemsSource = new[] { Msg("one"), Msg("two") } };
        var window = Host(list);
        try
        {
            Assert.Equal((-1, 2), ListEdgePosition.InList(list, list));
            Assert.Equal((-1, 2), ListEdgePosition.InList(list, null));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ARowOfAnotherListIsNotThisListsRow()
    {
        var mine = new ListView { ItemsSource = new[] { Msg("one"), Msg("two") } };
        var other = new ListView { ItemsSource = new[] { Msg("three") } };
        var panel = new StackPanel();
        panel.Children.Add(mine);
        panel.Children.Add(other);
        var window = Host(panel);
        try
        {
            var theirs = other.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
            Assert.NotNull(theirs);
            Assert.Equal((-1, 2), ListEdgePosition.InList(mine, theirs));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ASingleRowIsBothTheTopAndTheBottom()
    {
        var list = new ListView { ItemsSource = new[] { Msg("only") } };
        var window = Host(list);
        try
        {
            var row = list.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
            Assert.NotNull(row);
            var (index, count) = ListEdgePosition.InList(list, row);
            Assert.Equal((0, 1), (index, count));
            Assert.Equal(ListEdge.Top, ListEdgeCue.At(Key.Up, ModifierKeys.None, index, count));
            Assert.Equal(ListEdge.Bottom, ListEdgeCue.At(Key.Down, ModifierKeys.None, index, count));
        }
        finally { window.Close(); }
    }

    // ── Trees ──────────────────────────────────────────────────────────────────

    /// <summary>A tree shaped like MainWindow's conversation tree: groups with their messages under them, expansion bound two-way.</summary>
    private static TreeView ConversationTree(IEnumerable<ConversationGroup> groups)
    {
        var template = new HierarchicalDataTemplate(typeof(ConversationGroup)) { ItemsSource = new Binding(nameof(ConversationGroup.Messages)) };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        template.VisualTree = text;

        var style = new Style(typeof(TreeViewItem));
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
            new Binding(nameof(ConversationGroup.IsExpanded)) { Mode = BindingMode.TwoWay, FallbackValue = false }));
        return new TreeView { ItemsSource = groups.ToList(), ItemTemplate = template, ItemContainerStyle = style };
    }

    private static TreeViewItem Row(TreeView tree, object item, ConversationGroup? parent = null)
    {
        var generator = parent is null
            ? tree.ItemContainerGenerator
            : ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(parent)).ItemContainerGenerator;
        var row = generator.ContainerFromItem(item) as TreeViewItem;
        Assert.NotNull(row);
        return row!;
    }

    [StaFact]
    public void ATreeCountsOnlyTheRowsYouCanReach()
    {
        var a1 = Msg("a1");
        var a2 = Msg("a2");
        var b1 = Msg("b1");
        var open = new ConversationGroup { NormalizedSubject = "a", Messages = [a1, a2], IsExpanded = true };
        var closed = new ConversationGroup { NormalizedSubject = "b", Messages = [b1], IsExpanded = false };
        var groups = new[] { open, closed };
        var tree = ConversationTree(groups);
        var window = Host(tree);
        try
        {
            var visible = ListEdgePosition.VisibleRows(groups).ToList();
            Assert.Equal(new object[] { open, a1, a2, closed }, visible);

            Assert.Equal((0, 4), ListEdgePosition.InTree(tree, visible, Row(tree, open)));
            Assert.Equal((2, 4), ListEdgePosition.InTree(tree, visible, Row(tree, a2, open)));
            // The collapsed last group is the bottom: Down there goes nowhere.
            var (index, count) = ListEdgePosition.InTree(tree, visible, Row(tree, closed));
            Assert.Equal((3, 4), (index, count));
            Assert.Equal(ListEdge.Bottom, ListEdgeCue.At(Key.Down, ModifierKeys.None, index, count));

            // Opened, its message is the bottom and the group is not.
            closed.IsExpanded = true;
            tree.UpdateLayout();
            Drain();
            visible = ListEdgePosition.VisibleRows(groups).ToList();
            Assert.Equal((3, 5), ListEdgePosition.InTree(tree, visible, Row(tree, closed)));
            Assert.Equal((4, 5), ListEdgePosition.InTree(tree, visible, Row(tree, b1, closed)));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void FocusOnTheTreeItselfOrAnotherTreeIsNoRow()
    {
        var group = new ConversationGroup { NormalizedSubject = "a", Messages = [Msg("a1")] };
        var groups = new[] { group };
        var tree = ConversationTree(groups);
        var otherGroup = new ConversationGroup { NormalizedSubject = "z", Messages = [Msg("z1")] };
        var other = ConversationTree([otherGroup]);
        var panel = new StackPanel();
        panel.Children.Add(tree);
        panel.Children.Add(other);
        var window = Host(panel);
        try
        {
            var visible = ListEdgePosition.VisibleRows(groups).ToList();
            Assert.Equal((-1, 1), ListEdgePosition.InTree(tree, visible, tree));
            Assert.Equal((-1, 1), ListEdgePosition.InTree(tree, visible, null));
            Assert.Equal((-1, 1), ListEdgePosition.InTree(tree, visible, Row(other, otherGroup)));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ASingleGroupTreeIsBothEnds()
    {
        var group = new ConversationGroup { NormalizedSubject = "a", Messages = [Msg("a1")] };
        var groups = new[] { group };
        var tree = ConversationTree(groups);
        var window = Host(tree);
        try
        {
            var visible = ListEdgePosition.VisibleRows(groups).ToList();
            var (index, count) = ListEdgePosition.InTree(tree, visible, Row(tree, group));
            Assert.Equal((0, 1), (index, count));
            Assert.Equal(ListEdge.Top, ListEdgeCue.At(Key.Home, ModifierKeys.None, index, count));
            Assert.Equal(ListEdge.Bottom, ListEdgeCue.At(Key.End, ModifierKeys.None, index, count));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void SenderGroupsListTheirRowsTheSameWay()
    {
        var m1 = Msg("m1");
        var open = new SenderGroup { SenderKey = "x", Messages = [m1], IsExpanded = true };
        var closed = new SenderGroup { SenderKey = "y", Messages = [Msg("m2")], IsExpanded = false };
        Assert.Equal(new object[] { open, m1, closed }, ListEdgePosition.VisibleRows(new[] { open, closed }).ToList());
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
