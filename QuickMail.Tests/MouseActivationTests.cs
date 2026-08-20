// Mouse clicks in the main window's lists and trees — reported by a sighted user as "clicking a
// folder does not select the folder".
//
// What was actually happening: the click did move the tree's selection, and the row highlighted.
// Nothing else followed. QuickMail is keyboard-first, and every list here activates on Enter through
// a handler that reads SelectedItem; the mouse had no equivalent path. So the message list went on
// showing the folder the user came from, and the next F6 back into the tree snapped the highlight
// back to the folder that was really open — the click undone in front of them.
//
// The fix resolves the row from the element that was clicked, never from SelectedItem. That
// distinction is the point of most of these tests: the expander chevron, the scroll bar and the
// empty space below the last row do not move the selection, so a SelectedItem-based handler would
// activate whatever happened to be selected beforehand. The flat message list did exactly that.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class MouseActivationTests
{
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MailFolderModel Folder(string name, bool isHeader = false) => new()
    {
        FullName = name, DisplayName = name, AccountId = AccountId, IsHeader = isHeader,
    };

    // Inbox (a real folder) with one child, plus an account header that carries no folder — the
    // three node shapes the tree actually contains.
    private static List<FolderTreeNode> Nodes()
    {
        var child = new FolderTreeNode { Label = "Projects", Folder = Folder("INBOX/Projects") };
        var inbox = new FolderTreeNode { Label = "Inbox", Folder = Folder("INBOX"), IsExpanded = true };
        inbox.Children.Add(child);
        var header = new FolderTreeNode { Label = "Work", IsHeader = true, AccountId = AccountId };
        return [inbox, header];
    }

    // ── The folder tree ──────────────────────────────────────────────────────

    [StaFact]
    public void ClickingAFolderLabel_ResolvesThatFolder()
    {
        InFolderTree((tree, roots) =>
        {
            var inbox = roots[0];
            var label = Descendant<TextBlock>(Row(tree, inbox));
            Assert.NotNull(label);

            Assert.Same(inbox.Folder, MouseActivation.FolderFromClick(label));
        });
    }

    [StaFact]
    public void ClickingTextInsideARow_ResolvesTheRow_EvenThoughARunHasNoVisualParent()
    {
        // A Run is a content element: VisualTreeHelper.GetParent throws on it rather than returning
        // its TextBlock. Message subjects in the grouped trees are drawn from Runs, so a walk that
        // only knows the visual tree finds no row at all for the most ordinary click there.
        InFolderTree((tree, roots) =>
        {
            var inbox = roots[0];
            var run = Descendant<TextBlock>(Row(tree, inbox))?.Inlines.OfType<Run>().FirstOrDefault();
            Assert.NotNull(run);

            Assert.Same(inbox.Folder, MouseActivation.FolderFromClick(run));
        });
    }

    [StaFact]
    public void ClickingAChildRow_ResolvesTheChild_NotItsParent()
    {
        // Child rows are nested inside the parent's container, so the nearest row has to win.
        // Resolving to the outermost one would open the parent whenever a subfolder is clicked.
        InFolderTree((tree, roots) =>
        {
            var parentRow = Row(tree, roots[0]);
            var child = roots[0].Children[0];
            var label = Descendant<TextBlock>(Row(parentRow, child));
            Assert.NotNull(label);

            Assert.Same(child.Folder, MouseActivation.FolderFromClick(label));
        });
    }

    [StaFact]
    public void ClickingTheExpanderChevron_ResolvesNothing()
    {
        // Expanding a branch is not opening a folder. The chevron does not move the selection
        // either, so a SelectedItem-based handler would have fetched the previously open folder.
        InFolderTree((tree, roots) =>
        {
            var row = Row(tree, roots[0]);
            var expander = row.Template.FindName("Expander", row) as ToggleButton;
            Assert.NotNull(expander);

            Assert.Null(MouseActivation.FolderFromClick(expander));
        });
    }

    [StaFact]
    public void ClickingTheEmptySpaceBelowTheRows_ResolvesNothing()
    {
        // The click lands on the items host: inside the tree, on no row.
        InFolderTree((tree, _) =>
        {
            var host = Descendant<ItemsPresenter>(tree);
            Assert.NotNull(host);

            Assert.Null(MouseActivation.FolderFromClick(host));
            Assert.Null(MouseActivation.FolderFromClick(tree));
        });
    }

    [StaFact]
    public void ClickingTheScrollBar_ResolvesNothing()
    {
        InFolderTree((tree, _) =>
        {
            var bar = Descendant<ScrollBar>(tree);
            Assert.NotNull(bar);

            Assert.Null(MouseActivation.FolderFromClick(bar));
        });
    }

    [StaFact]
    public void ClickingAnAccountHeaderRow_ResolvesNoFolder()
    {
        // Header nodes carry no folder — the same nodes Enter skips.
        InFolderTree((tree, roots) =>
        {
            var label = Descendant<TextBlock>(Row(tree, roots[1]));
            Assert.NotNull(label);

            Assert.Null(MouseActivation.FolderFromClick(label));
            Assert.Same(roots[1], MouseActivation.ItemFromClick<FolderTreeNode>(label));
        });
    }

    [StaFact]
    public void AFolderMarkedAsAHeader_IsNotActivated()
    {
        // SelectFolderAsync returns immediately for one of these, so activating it would be a click
        // that visibly does nothing — the bug this whole change is about.
        var row = new TreeViewItem
        {
            DataContext = new FolderTreeNode { Label = "Work", Folder = Folder("Work", isHeader: true) },
        };

        Assert.Null(MouseActivation.FolderFromClick(row));
    }

    [Fact]
    public void NoSource_ResolvesNothing()
    {
        Assert.Null(MouseActivation.FolderFromClick(null));
        Assert.Null(MouseActivation.ItemFromClick<FolderTreeNode>(null));
        Assert.Null(MouseActivation.ItemFromClick<FolderTreeNode>("not a dependency object"));
    }

    [StaFact]
    public void ARowHoldingAnotherKindOfItem_ResolvesNothing()
    {
        // A group header in the conversation tree, clicked while looking for a message: the row
        // resolves, its item is not a message, and the click stays plain selection.
        var row = new TreeViewItem { DataContext = new FolderTreeNode { Label = "Inbox" } };

        Assert.Null(MouseActivation.ItemFromClick<MailMessageSummary>(row));
    }

    // ── Multi-select gestures are not activations ───────────────────────────

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void ModifierClicksExtendTheSelection(ModifierKeys modifiers)
    {
        // The message list is Extended-selection. Ctrl+clicking five messages to delete them used
        // to open all five on the way past — five message windows, in Window mode.
        Assert.True(MouseActivation.ExtendsSelection(modifiers));
    }

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Alt)]
    public void APlainClickActivates(ModifierKeys modifiers)
    {
        Assert.False(MouseActivation.ExtendsSelection(modifiers));
    }

    [Fact]
    public void TheMessageListSkipsModifierClicks()
    {
        Assert.Contains("if (MouseActivation.ExtendsSelection(Keyboard.Modifiers)) return;",
                        MessageListClickHandler(), StringComparison.Ordinal);
    }

    // ── The account list (a ListBox, the other container shape) ──────────────

    [StaFact]
    public void ClickingAnAccountRow_ResolvesThatAccount()
    {
        var accounts = new List<AccountModel>
        {
            new() { Id = AccountId, AccountName = "Work", Username = "kelly@example.com" },
        };
        var list = new ListBox { ItemsSource = accounts, DisplayMemberPath = "AccountName" };

        InWindow(list, () =>
        {
            var row = list.ItemContainerGenerator.ContainerFromItem(accounts[0]) as ListBoxItem;
            Assert.NotNull(row);
            var label = Descendant<TextBlock>(row!);
            Assert.NotNull(label);

            Assert.Same(accounts[0], MouseActivation.ItemFromClick<AccountModel>(label));
            Assert.Null(MouseActivation.ItemFromClick<AccountModel>(list));
        });
    }

    // ── Wiring: every pane that activates on Enter also activates on the mouse ──
    //
    // Read from source. The handlers live on MainWindow, which no test here stands up — and the
    // helper above cannot tell whether anything is calling it.

    [Theory]
    [InlineData("FolderList", "MouseLeftButtonUp=\"FolderList_MouseLeftButtonUp\"")]
    [InlineData("AccountList", "MouseLeftButtonUp=\"AccountList_MouseLeftButtonUp\"")]
    [InlineData("MessageList", "MouseLeftButtonUp=\"MessageList_MouseLeftButtonUp\"")]
    [InlineData("the attachment list", "MouseDoubleClick=\"ReadingPaneAttachmentList_MouseDoubleClick\"")]
    public void MainWindowWiresTheMouseHandler(string pane, string attribute)
    {
        Assert.True(Source("Views/MainWindow.xaml").Contains(attribute, StringComparison.Ordinal),
                    pane + " no longer declares " + attribute);
    }

    [Fact]
    public void AllThreeGroupedTreesWireTheMouseHandler()
    {
        // Conversations, From and To. A fix applied to one tree and not the others is the "works
        // from one view only" incompleteness the feature checklist calls out.
        var declarations = Source("Views/MainWindow.xaml")
            .Split("MouseLeftButtonUp=\"GroupTree_MouseLeftButtonUp\"").Length - 1;

        Assert.Equal(3, declarations);
    }

    [Fact]
    public void TheMessageWindowsAttachmentListOpensOnDoubleClick()
    {
        Assert.Contains("MouseDoubleClick=\"AttachmentList_MouseDoubleClick\"",
                        Source("Views/MessageWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void ClickingAFolderRunsTheSameCommandAsEnter()
    {
        // Both paths go through SelectFolderCommand, so a click gets the view reset, the account
        // switch and the fetch — not a bare assignment to SelectedFolder that leaves the app naming
        // a folder it never loaded.
        Assert.Contains("await _vm.SelectFolderCommand.ExecuteAsync(folder);",
                        FolderClickHandler(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolderTreeStillHasNoSelectedItemChangedHandler()
    {
        // The tempting one-line fix, and the wrong one: arrow keys move TreeView.SelectedItem, so
        // loading on selection change would fetch every folder the user arrows past. Enter commits;
        // the mouse click is its own gesture.
        var xaml = Source("Views/MainWindow.xaml");
        var folderTree = Between(xaml, "x:Name=\"FolderList\"", "</TreeView>");

        Assert.DoesNotContain("SelectedItemChanged", folderTree, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolderClickDoesNotReadTheTreesSelection()
    {
        // Same reason the message list stopped: the chevron and the scroll bar do not move the
        // selection, so SelectedItem is the wrong question to ask about a click.
        Assert.DoesNotContain("SelectedItem", FolderClickHandler(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageListNoLongerActivatesWhateverIsSelected()
    {
        // Regression: the handler read MessageList.SelectedItem, so a click on the empty space below
        // the last row re-opened the selected message — a second window in Window mode.
        Assert.DoesNotContain("SelectedItem", MessageListClickHandler(), StringComparison.Ordinal);
    }

    private static string MessageListClickHandler() =>
        Between(Source("Views/MainWindow.xaml.cs"),
                "private async void MessageList_MouseLeftButtonUp", "\n    }");

    private static string FolderClickHandler() =>
        Between(Source("Views/MainWindow.xaml.cs"),
                "private async void FolderList_MouseLeftButtonUp", "\n    }");

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the folder tree in a shown window — containers only exist once laid out — hands it to
    /// <paramref name="test"/>, and closes the window afterwards.
    /// </summary>
    private static void InFolderTree(Action<TreeView, List<FolderTreeNode>> test)
    {
        var roots = Nodes();
        var tree = new TreeView { ItemsSource = roots };
        tree.Resources.Add(new DataTemplateKey(typeof(FolderTreeNode)), RowTemplate());

        InWindow(tree, () => test(tree, roots));
    }

    // Shaped like the real one: a Run inside a TextBlock, so the content-element hop is exercised by
    // every test here and not only by the one that names it.
    private static HierarchicalDataTemplate RowTemplate() =>
        (HierarchicalDataTemplate)XamlReader.Parse(
            "<HierarchicalDataTemplate " +
            "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "ItemsSource=\"{Binding Children}\">" +
            "<StackPanel Orientation=\"Horizontal\">" +
            "<TextBlock><Run Text=\"{Binding Label, Mode=OneWay}\"/></TextBlock>" +
            "</StackPanel></HierarchicalDataTemplate>");

    private static void InWindow(FrameworkElement content, Action test)
    {
        // ThemedControls carries the TreeViewItem template that owns the expander chevron — the
        // chrome one of these tests clicks on.
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 300, Height = 400, Content = content,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Drain();
            test();
        }
        finally { window.Close(); }
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static TreeViewItem Row(ItemsControl parent, FolderTreeNode node)
    {
        parent.UpdateLayout();
        var row = parent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
        Assert.True(row is not null, "no container was realized for '" + node.Label + "'.");
        row!.ApplyTemplate();
        row.UpdateLayout();
        return row;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            // Stop at a nested row: a parent's descendants include its children's whole subtrees,
            // and returning one of those would test the wrong row.
            if (child is TreeViewItem or ListBoxItem) continue;
            if (child is T hit) return hit;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "'" + start + "' is gone from the source.");
        var rest = text[from..];
        var to = rest.IndexOf(end, StringComparison.Ordinal);
        Assert.True(to >= 0, "'" + end + "' never follows '" + start + "'.");
        return rest[..to];
    }

    private static string Source(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), path + " not found.");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "QuickMail.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "QuickMail.sln not found above " + AppContext.BaseDirectory);
        return dir!;
    }
}
