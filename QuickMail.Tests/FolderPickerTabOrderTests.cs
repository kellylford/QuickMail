using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tab and Shift+Tab around the folder picker's tree.
///
/// <para>Tabbing from the folder tree to "New Folder" and pressing Shift+Tab to go back landed on
/// Cancel: WPF's reverse traversal cannot enter a TreeView, because TreeViewItem leaves IsTabStop
/// false and reverse entry looks for a tab stop. Forward entry works by another route — into a
/// TabNavigation="Once" container it goes to the container's focused descendant — so the picker
/// tabbed forward correctly and could not be tabbed back into at all.</para>
///
/// <para>Two halves are pinned here. The ring itself is measured through WPF's real forward
/// traversal, which is what the layout decides: the buttons used to be declared before the list and
/// the tree, so the folders came last in tab order. The boundary crossings are pressed as real key
/// events, because that is where the reverse direction is now wired by hand.</para>
/// </summary>
[Collection("WpfTests")]
public class FolderPickerTabOrderTests : IDisposable
{
    private static readonly Guid AccountId = Guid.NewGuid();

    /// <summary>What the picker's Tab handler sees as the held modifiers; see
    /// FolderPickerWindow.ModifiersOf. Per instance, and xUnit builds one instance per test, so
    /// nothing here is shared between tests even though the seam it feeds is static.</summary>
    private ModifierKeys _modifiersWhenPressed = ModifierKeys.None;

    /// <summary>The shipped delegate, put back in Dispose. The seam lives on the window class,
    /// which six other suites construct: leaving it pointing here would make every later Shift+Tab
    /// in this process read as a plain Tab, and the failure would look like a handler regression.</summary>
    private readonly Func<KeyEventArgs, ModifierKeys> _shippedModifiers = FolderPickerWindow.ModifiersOf;

    public FolderPickerTabOrderTests()
    {
        WpfTestHost.EnsureApplication();
        FolderPickerWindow.ModifiersOf = _ => _modifiersWhenPressed;
        _modifiersWhenPressed = ModifierKeys.None;
    }

    public void Dispose()
    {
        FolderPickerWindow.ModifiersOf = _shippedModifiers;
        _modifiersWhenPressed = ModifierKeys.None;
        GC.SuppressFinalize(this);
    }

    private static AccountModel Account() => new()
    {
        Id = AccountId, AccountName = "Work", Username = "kelly@example.com",
    };

    private static Dictionary<Guid, List<MailFolderModel>> Folders() => new()
    {
        [AccountId] =
        [
            new MailFolderModel
            {
                FullName = "INBOX", DisplayName = "INBOX",
                AccountId = AccountId, Kind = SpecialFolderKind.Inbox,
            },
            new MailFolderModel { FullName = "Archive", DisplayName = "Archive", AccountId = AccountId },
        ],
    };

    /// <summary>
    /// An IMAP account whose hierarchy lives in the separator-delimited FullName, with "Projects"
    /// present only as a path segment: no mailbox of that name, so its node carries no folder and
    /// Open greys out while it is selected.
    /// </summary>
    private static Dictionary<Guid, List<MailFolderModel>> FoldersWithAPathSegment() => new()
    {
        [AccountId] =
        [
            new MailFolderModel
            {
                FullName = "INBOX", DisplayName = "INBOX",
                AccountId = AccountId, Kind = SpecialFolderKind.Inbox,
            },
            new MailFolderModel { FullName = "Projects/2026", DisplayName = "2026", AccountId = AccountId },
        ],
    };

    /// <summary>A shown picker, so containers and layout are real, without stealing the foreground.</summary>
    private static FolderPickerWindow Shown(
        bool tree = true,
        bool canCreateFolders = true,
        Dictionary<Guid, List<MailFolderModel>>? folders = null)
    {
        var window = new FolderPickerWindow(
            [Account()],
            folders ?? Folders(),
            title: "Move to Folder",
            useTreeView: tree,
            folderCreator: canCreateFolders
                ? (_, _, _) => Task.FromResult<IReadOnlyList<MailFolderModel>?>(null)
                : null)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();
        TabOrderWalker.Drain();
        return window;
    }

    private static T Named<T>(Window window, string name) where T : class
    {
        var element = window.FindName(name) as T;
        Assert.True(element is not null, $"{name} is missing from {window.GetType().Name}.");
        return element!;
    }

    /// <summary>The container of the tree's selected folder — where focus belongs after Shift+Tab.</summary>
    private static TreeViewItem SelectedContainer(Window window)
    {
        var tree = Named<TreeView>(window, "FolderTreeView");
        var selected = tree.SelectedItem as FolderTreeNode;
        Assert.True(selected is not null, "The picker opened with nothing selected in the tree.");

        var container = Containers(tree).FirstOrDefault(c => ReferenceEquals(c.Header, selected));
        Assert.True(container is not null, "No container for the selected node.");
        return container!;
    }

    private static IEnumerable<TreeViewItem> Containers(ItemsControl parent)
    {
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem item) continue;
            yield return item;
            foreach (var child in Containers(item)) yield return child;
        }
    }

    /// <summary>Selects the node with <paramref name="label"/> and returns its container.</summary>
    private static TreeViewItem Select(Window window, string label)
    {
        var tree = Named<TreeView>(window, "FolderTreeView");
        var container = Containers(tree)
            .FirstOrDefault(c => (c.Header as FolderTreeNode)?.Label == label);
        Assert.True(container is not null, $"No node labelled '{label}' in the tree.");

        container!.IsSelected = true;
        TabOrderWalker.Drain();
        return container;
    }

    private static KeyEventArgs TabPress(Window window, FrameworkElement from) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Tab)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
            Source      = from,
        };

    /// <summary>Presses Tab at <paramref name="from"/> and returns where focus went.</summary>
    private FrameworkElement? Press(Window window, FrameworkElement from, ModifierKeys modifiers)
    {
        _modifiersWhenPressed = modifiers;
        TabOrderWalker.StartAt(window, from, TabOrderWalker.Describe(from));

        var args = TabPress(window, from);
        from.RaiseEvent(args);
        TabOrderWalker.Drain();

        Assert.True(args.Handled, $"{modifiers}+Tab at '{TabOrderWalker.Describe(from)}' was not handled.");
        return FocusManager.GetFocusedElement(window) as FrameworkElement;
    }

    [StaFact]
    public void ShiftTabFromNewFolderReturnsToTheSelectedFolder()
    {
        var window = Shown();
        try
        {
            var tree = Named<TreeView>(window, "FolderTreeView");
            var chosen = tree.SelectedItem;
            var expected = SelectedContainer(window);

            var landed = Press(window, Named<Button>(window, "NewFolderButton"), ModifierKeys.Shift);

            Assert.Same(expected, landed);

            // The destination has to survive the trip to the buttons and back: coming into the tree
            // moves focus, never the selection. Filing mail somewhere the user did not choose,
            // silently, is the worst thing this dialog could do.
            Assert.Same(chosen, tree.SelectedItem);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The rule-target and startup pickers show no New Folder button, so Open is the first stop
    /// after the tree there. Shift+Tab has to come back from whichever button that is.
    /// </summary>
    [StaFact]
    public void ShiftTabFromOpenReturnsToTheTreeWhenNewFolderIsHidden()
    {
        var window = Shown(canCreateFolders: false);
        try
        {
            Assert.Equal(Visibility.Collapsed, Named<Button>(window, "NewFolderButton").Visibility);

            var expected = SelectedContainer(window);
            var landed = Press(window, Named<Button>(window, "OpenButton"), ModifierKeys.Shift);

            Assert.Same(expected, landed);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ShiftTabFromOpenIsLeftToWpfWhenNewFolderIsShown()
    {
        var window = Shown();
        try
        {
            // With New Folder in front of it, Open's Shift+Tab is an ordinary step WPF already
            // makes correctly, and the handler must leave it alone.
            _modifiersWhenPressed = ModifierKeys.Shift;
            var open = Named<Button>(window, "OpenButton");
            TabOrderWalker.StartAt(window, open, "OpenButton");

            var args = TabPress(window, open);
            open.RaiseEvent(args);

            Assert.False(args.Handled, "Shift+Tab on Open was intercepted; it belongs to WPF.");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TabFromTheTreeGoesToNewFolderAndShiftTabWrapsToCancel()
    {
        var window = Shown();
        try
        {
            var selected = SelectedContainer(window);

            Assert.Same(Named<Button>(window, "NewFolderButton"),
                        Press(window, selected, ModifierKeys.None));
            Assert.Same(Named<Button>(window, "CancelButton"),
                        Press(window, selected, ModifierKeys.Shift));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TabFromCancelWrapsBackToTheSelectedFolder()
    {
        var window = Shown();
        try
        {
            var expected = SelectedContainer(window);
            var landed = Press(window, Named<Button>(window, "CancelButton"), ModifierKeys.None);

            Assert.Same(expected, landed);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Open greys out whenever the selection carries no folder — an account header, or an IMAP path
    /// segment that is not itself a mailbox — and the rule-target, startup and POP3 pickers show no
    /// New Folder button at all. Focusing a disabled button does nothing and reports nothing, so
    /// handing Tab to one after marking the key handled left the user in the tree with no way out
    /// of the dialog: exactly the fault this change set out to fix, in the other direction.
    /// </summary>
    [StaFact]
    public void TabLeavesTheTreeWhenOpenIsDisabledAndNewFolderIsHidden()
    {
        var window = Shown(canCreateFolders: false, folders: FoldersWithAPathSegment());
        try
        {
            var segment = Select(window, "Projects");
            Assert.False(Named<Button>(window, "OpenButton").IsEnabled,
                         "The premise of this test is that Open is disabled on a path segment.");

            Assert.Same(Named<Button>(window, "CancelButton"),
                        Press(window, segment, ModifierKeys.None));

            // And back: Cancel is the first button focus can reach, so Shift+Tab there returns to
            // the tree rather than dead-ending on the disabled Open.
            Assert.Same(segment, Press(window, Named<Button>(window, "CancelButton"), ModifierKeys.Shift));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The forward ring in tree mode, measured through WPF's own traversal.
    ///
    /// <para>This does not pin the declaration order, and must not be read as if it did: in tree
    /// mode the search box and the flat list are both collapsed, leaving two groups, and a cycle of
    /// two has only one order — the old DockPanel layout produces the same list. What pins the
    /// layout is <see cref="TheFlatListTraversesBothWaysWithoutHelp"/>, where the buttons genuinely
    /// did come before the list.</para>
    /// </summary>
    [StaFact]
    public void TheTreeModeRingRunsFromTheTreeThroughTheButtons()
    {
        var window = Shown();
        try
        {
            TabOrderWalker.StartAt(window, SelectedContainer(window), "the selected folder");

            Assert.Equal(
                ["NewFolderButton", "OpenButton", "CancelButton", "FolderTreeView"],
                TabOrderWalker.Walk(window));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// "Go to Folder" keeps the flat list, whose items are tab stops: it traverses correctly in both
    /// directions on its own and none of the key handling above applies to it.
    ///
    /// <para>This is also what pins the declaration order. With the buttons declared before the
    /// list, as they had to be under the DockPanel, the forward ring here reads OpenButton,
    /// CancelButton, FolderListBox, SearchBox — the search box's own list came after the buttons
    /// that act on it.</para>
    /// </summary>
    [StaFact]
    public void TheFlatListTraversesBothWaysWithoutHelp()
    {
        var window = Shown(tree: false);
        try
        {
            var search = Named<TextBox>(window, "SearchBox");

            // The walk ends back on the search box: the ring closes, in both directions.
            TabOrderWalker.StartAt(window, search, "SearchBox");
            Assert.Equal(
                ["FolderListBox", "OpenButton", "CancelButton", "SearchBox"],
                TabOrderWalker.Walk(window));

            TabOrderWalker.StartAt(window, search, "SearchBox");
            Assert.Equal(
                ["CancelButton", "OpenButton", "FolderListBox", "SearchBox"],
                TabOrderWalker.WalkBackward(window));
        }
        finally { window.Close(); }
    }
}
