// A folder refresh must not rebuild the folder tree under the user — issue #719.
//
// Reported as: focus in the folder tree "moves on its own" to All Mail about a second after a sync
// starts. BuildFolderTree used to swap in a brand-new collection of brand-new nodes on every folder
// refresh (an account connecting, a calendar pull), which threw away every TreeViewItem — including
// the one with keyboard focus. It now merges the fresh tree into the live one, keeping every node
// that is still there, so the focused item's container survives and focus never moves.
//
// Pinned here:
//   * a rebuild keeps the live collection and its node objects,
//   * a kept node takes on the refreshed folder object and label (so counts and names stay live),
//   * a vanished folder is a Remove, not a Move of the siblings after it — a moved item's container
//     is regenerated, which is the focus loss this exists to prevent,
//   * a new folder is inserted where it belongs, and a node that changed kind is replaced,
//   * and, against a real TreeView, the kept node's TreeViewItem is the same object afterwards.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

// Shows a window, so it must not run alongside the focus tests (WindowTestCollectionGuard.cs).
[Collection("WpfTests")]
public class FolderTreeMergeTests
{
    private static readonly Guid AccountA = Guid.Parse("71971971-0000-0000-0000-000000000001");

    private static MailFolderModel Folder(string fullName, string display, int unread = 0) =>
        new() { AccountId = AccountA, FullName = fullName, DisplayName = display, UnreadCount = unread };

    private static FolderTreeNode Node(MailFolderModel f, params FolderTreeNode[] children)
    {
        var n = new FolderTreeNode { Folder = f, Label = f.DisplayName };
        foreach (var c in children) n.Children.Add(c);
        return n;
    }

    private static FolderTreeNode Header(string label, params FolderTreeNode[] children)
    {
        var n = new FolderTreeNode { IsHeader = true, Label = label, AccountId = AccountA, IsExpanded = true };
        foreach (var c in children) n.Children.Add(c);
        return n;
    }

    private static IEnumerable<FolderTreeNode> Flatten(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    // ── Through the view model ───────────────────────────────────────────────

    private static async Task<MainViewModel> MakeVmAsync()
    {
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [AccountA] =
            [
                new MailFolderModel { AccountId = AccountA, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox },
                new MailFolderModel { AccountId = AccountA, FullName = "Archive", DisplayName = "Archive" },
            ],
        };

        var vm = new MainViewModel(
            new FolderedMailService(folders, []), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

        vm.Accounts.Add(new AccountModel
        {
            Id = AccountA, AccountName = "Work", Username = "work@example.com",
            AuthType = AuthType.OAuth2Microsoft,
        });
        await vm.ConnectAllAccountsAsync();
        return vm;
    }

    [Fact]
    public async Task ARefresh_KeepsTheLiveTreeAndItsNodes()
    {
        var vm = await MakeVmAsync();
        var tree = vm.FolderTree;
        var before = Flatten(tree).ToList();
        Assert.Contains(before, n => n.Folder?.FullName == "INBOX");

        var replaced = false;
        vm.PropertyChanged += (_, e) => replaced |= e.PropertyName == nameof(MainViewModel.FolderTree);

        await vm.ConnectAllAccountsAsync();   // reloads folders, rebuilding the tree

        Assert.Same(tree, vm.FolderTree);
        Assert.False(replaced, "the rebuild replaced FolderTree, which drops every TreeViewItem and focus with it.");
        var after = Flatten(vm.FolderTree).ToList();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Same(before[i], after[i]);
    }

    // ── The merge itself ─────────────────────────────────────────────────────

    [Fact]
    public void AKeptNode_TakesTheRefreshedFolderAndLabel_AndSaysSo()
    {
        var inbox = Node(Folder("INBOX", "Inbox", unread: 3));
        var live = new ObservableCollection<FolderTreeNode> { Header("Work", inbox) };

        var refreshed = Folder("INBOX", "Inbox (renamed)", unread: 7);
        var raised = new List<string?>();
        inbox.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        MainViewModel.MergeFolderNodes(live, [Header("Work", Node(refreshed))]);

        Assert.Same(inbox, live[0].Children[0]);
        Assert.Same(refreshed, inbox.Folder);
        Assert.Equal("Inbox (renamed)", inbox.Label);
        Assert.Equal("Inbox (renamed), 7 unread", inbox.AutomationName);
        // The accessible name and the badge are bound; without these the tree shows the old count.
        Assert.Contains(nameof(FolderTreeNode.AutomationName), raised);
        Assert.Contains(nameof(FolderTreeNode.UnreadDisplay), raised);
        Assert.Contains(nameof(FolderTreeNode.Label), raised);
    }

    [Fact]
    public void AnUnchangedNode_RaisesNothing()
    {
        var f = Folder("INBOX", "Inbox");
        var inbox = Node(f);
        var live = new ObservableCollection<FolderTreeNode> { Header("Work", inbox) };
        var raised = 0;
        inbox.PropertyChanged += (_, _) => raised++;

        MainViewModel.MergeFolderNodes(live, [Header("Work", Node(f))]);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void AVanishedFolder_IsRemoved_WithoutMovingTheSiblingsAfterIt()
    {
        var a = Node(Folder("A", "A"));
        var b = Node(Folder("B", "B"));
        var c = Node(Folder("C", "C"));
        var header = Header("Work", a, b, c);
        var live = new ObservableCollection<FolderTreeNode> { header };

        var actions = new List<NotifyCollectionChangedAction>();
        header.Children.CollectionChanged += (_, e) => actions.Add(e.Action);

        MainViewModel.MergeFolderNodes(live, [Header("Work", Node(Folder("A", "A")), Node(Folder("C", "C")))]);

        Assert.Equal([a, c], header.Children);
        Assert.Equal([NotifyCollectionChangedAction.Remove], actions);
    }

    [Fact]
    public void ANewFolder_IsInsertedInPlace_AndTheOthersKept()
    {
        var a = Node(Folder("A", "A"));
        var c = Node(Folder("C", "C"));
        var header = Header("Work", a, c);
        var live = new ObservableCollection<FolderTreeNode> { header };
        var newB = Node(Folder("B", "B"));

        MainViewModel.MergeFolderNodes(live, [Header("Work", Node(Folder("A", "A")), newB, Node(Folder("C", "C")))]);

        Assert.Equal([a, newB, c], header.Children);
    }

    [Fact]
    public void ChildrenOfAKeptNode_AreMergedToo()
    {
        var sub = Node(Folder("INBOX/Sub", "Sub"));
        var inbox = Node(Folder("INBOX", "Inbox"), sub);
        var live = new ObservableCollection<FolderTreeNode> { Header("Work", inbox) };
        var sub2 = Node(Folder("INBOX/Sub2", "Sub2"));

        MainViewModel.MergeFolderNodes(live,
            [Header("Work", Node(Folder("INBOX", "Inbox"), Node(Folder("INBOX/Sub", "Sub")), sub2))]);

        Assert.Same(inbox, live[0].Children[0]);
        Assert.Equal([sub, sub2], inbox.Children);
    }

    [Fact]
    public void ANodeThatChangedKind_IsReplaced()
    {
        // A placeholder header and a real folder can never share a key, but a header flag is part
        // of how the row is drawn, so a node is only kept when its kind is unchanged.
        var live = new ObservableCollection<FolderTreeNode> { new() { Label = "Views", IsHeader = true } };
        var fresh = new FolderTreeNode { Label = "Views" };

        MainViewModel.MergeFolderNodes(live, [fresh]);

        Assert.Same(fresh, Assert.Single(live));
    }

    [Fact]
    public void AReorder_EndsInTheFreshOrder()
    {
        var a = Node(Folder("A", "A"));
        var b = Node(Folder("B", "B"));
        var live = new ObservableCollection<FolderTreeNode> { a, b };

        MainViewModel.MergeFolderNodes(live, [Node(Folder("B", "B")), Node(Folder("A", "A"))]);

        Assert.Equal([b, a], live);
    }

    [Fact]
    public void KeptNodes_KeepTheirExpansion()
    {
        var inbox = Node(Folder("INBOX", "Inbox"), Node(Folder("INBOX/Sub", "Sub")));
        inbox.IsExpanded = true;
        var live = new ObservableCollection<FolderTreeNode> { inbox };

        MainViewModel.MergeFolderNodes(live, [Node(Folder("INBOX", "Inbox"), Node(Folder("INBOX/Sub", "Sub")))]);

        Assert.True(inbox.IsExpanded);
    }

    [Fact]
    public void AnAccountPlaceholder_IsKept_WhenItsFoldersArrive()
    {
        // The "mailbox connecting" refresh from the issue: an account shown as a bare header until
        // its folders load. The header is the same node before and after, so a user sitting on it
        // is not moved when its folders appear under it.
        var placeholder = new FolderTreeNode { IsHeader = true, Label = "Shared", AccountId = AccountA };
        var live = new ObservableCollection<FolderTreeNode> { placeholder };
        var loaded = new FolderTreeNode { IsHeader = true, Label = "Shared", AccountId = AccountA, IsExpanded = true };
        loaded.Children.Add(Node(Folder("INBOX", "Inbox")));

        MainViewModel.MergeFolderNodes(live, [loaded]);

        Assert.Same(placeholder, Assert.Single(live));
        Assert.Equal("INBOX", Assert.Single(placeholder.Children).Folder!.FullName);
    }

    [Fact]
    public void AnInsertARemovalAndAReorder_InOneMerge_EndInTheFreshOrder()
    {
        var a = Node(Folder("A", "A"));
        var b = Node(Folder("B", "B"));
        var c = Node(Folder("C", "C"));
        var live = new ObservableCollection<FolderTreeNode> { a, b, c };
        var d = Node(Folder("D", "D"));

        MainViewModel.MergeFolderNodes(live, [Node(Folder("C", "C")), d, Node(Folder("A", "A"))]);

        Assert.Equal([c, d, a], live);
    }

    [Fact]
    public void TwoSiblingsWithOneKey_AreTrimmedToWhatTheFreshTreeHolds()
    {
        var first  = new FolderTreeNode { Label = "Segment" };
        var second = new FolderTreeNode { Label = "Segment" };
        var live = new ObservableCollection<FolderTreeNode> { first, second };

        MainViewModel.MergeFolderNodes(live, [new FolderTreeNode { Label = "Segment" }]);

        Assert.Same(first, Assert.Single(live));
    }

    [Fact]
    public void ANewFolderObject_WithTheSameValues_RaisesOnlyFolder()
    {
        // Calendar and saved-view nodes get a new folder object on every build. Nothing shown or
        // spoken changed, so nothing but the reference is announced.
        var inbox = Node(Folder("INBOX", "Inbox", unread: 2));
        var live = new ObservableCollection<FolderTreeNode> { inbox };
        var raised = new List<string?>();
        inbox.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        MainViewModel.MergeFolderNodes(live, [Node(Folder("INBOX", "Inbox", unread: 2))]);

        Assert.Equal([nameof(FolderTreeNode.Folder)], raised);
    }

    // ── Against a real TreeView ──────────────────────────────────────────────

    [StaFact]
    public void TheKeptNodesTreeViewItem_SurvivesARefresh()
    {
        // The point of the whole change: the container — the thing that holds keyboard focus — is
        // the same object after a refresh, so there is nothing for focus to fall off.
        var inbox = Node(Folder("INBOX", "Inbox", unread: 1));
        var archive = Node(Folder("Archive", "Archive"));
        var live = new ObservableCollection<FolderTreeNode> { Header("Work", inbox, archive) };

        var tree = new TreeView { ItemsSource = live, ItemTemplate = RowTemplate() };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 300, Height = 400, Content = tree,
        };
        window.Show();
        try
        {
            var before = Container(tree, inbox);

            // An unread count changed and Archive went away — a typical refresh.
            MainViewModel.MergeFolderNodes(live,
                [Header("Work", Node(Folder("INBOX", "Inbox", unread: 4)))]);

            Assert.Same(before, Container(tree, inbox));
            Assert.Equal("Inbox, 4 unread", inbox.AutomationName);
        }
        finally { window.Close(); }
    }

    private static TreeViewItem Container(TreeView tree, FolderTreeNode node)
    {
        tree.UpdateLayout();
        Drain();
        var header = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
        header.IsExpanded = true;
        header.UpdateLayout();
        Drain();
        var tvi = header.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
        Assert.NotNull(tvi);
        return tvi!;
    }

    private static HierarchicalDataTemplate RowTemplate() =>
        (HierarchicalDataTemplate)XamlReader.Parse(
            "<HierarchicalDataTemplate " +
            "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "ItemsSource=\"{Binding Children}\">" +
            "<TextBlock Text=\"{Binding Label}\"/></HierarchicalDataTemplate>");

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
