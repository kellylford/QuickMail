using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// "Move Folder…" and "Copy Folder…" on the folder tree's context menu opened a long flat list of
/// every folder in every account, while every other place a destination folder is chosen — the
/// folder tree itself, and the move/copy-message picker — shows a tree (issue #431).
///
/// <para>These tests assert the presentation by inspecting the controls the picker shows rather than
/// the arguments it was handed: asserting the flag would keep passing if the flag stopped driving
/// the presentation. The other half — that the two commands actually reach this factory, which no
/// test here can see because none of them touch MainWindow — is
/// <see cref="FolderMoveCopyCallSiteTests"/>.</para>
/// </summary>
[Collection("WpfTests")]
public class FolderPickerTreeTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private static AccountModel Account(string label = "Work", Guid? id = null) => new()
    {
        Id = id ?? AccountId,
        AccountName = label,
        Username = "kelly@example.com",
    };

    private static MailFolderModel Folder(string fullName, string display, Guid? account = null) => new()
    {
        FullName = fullName, DisplayName = display, AccountId = account ?? AccountId,
    };

    // An IMAP-shaped account: hierarchy lives in the separator-delimited FullName.
    private static Dictionary<Guid, List<MailFolderModel>> Folders() => new()
    {
        [AccountId] =
        [
            new MailFolderModel
            {
                FullName = "INBOX", DisplayName = "INBOX",
                AccountId = AccountId, Kind = SpecialFolderKind.Inbox,
            },
            Folder("INBOX/Projects", "Projects"),
            Folder("INBOX/Projects/2026", "2026"),
            Folder("Archive", "Archive"),
        ],
    };

    private static MailFolderModel SourceFolder() => Folder("Archive", "Archive");

    private static FolderPickerWindow Picker(
        MailFolderModel? source = null,
        IEnumerable<AccountModel>? accounts = null,
        Dictionary<Guid, List<MailFolderModel>>? folders = null)
    {
        var picker = FolderPickerWindow.ForFolderMoveCopy(
            accounts ?? [Account()], folders ?? Folders(), source ?? SourceFolder(), "Move Folder To");
        Assert.True(picker is not null, "ForFolderMoveCopy found no destination folder to offer.");
        return picker!;
    }

    /// <summary>Realizes the window so item containers exist, without stealing focus from the desktop.</summary>
    private static FolderPickerWindow Shown(FolderPickerWindow window)
    {
        window.WindowStyle    = WindowStyle.None;
        window.ShowInTaskbar  = false;
        window.ShowActivated  = false;
        window.Show();
        window.UpdateLayout();
        Drain();
        return window;
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T Named<T>(Window window, string name) where T : class
    {
        var element = window.FindName(name) as T;
        Assert.True(element is not null, $"{name} is missing from {window.GetType().Name}.");
        return element!;
    }

    private static List<FolderTreeNode> Roots(Window window) =>
        Assert.IsAssignableFrom<IEnumerable<FolderTreeNode>>(
            Named<TreeView>(window, "FolderTreeView").ItemsSource).ToList();

    private static IEnumerable<FolderTreeNode> Flatten(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    [StaFact]
    public void ForFolderMoveCopy_ShowsTheTreeAndHidesTheFlatList()
    {
        var window = Picker();
        try
        {
            Assert.Equal("Move Folder To", window.Title);
            Assert.Equal(Visibility.Visible,   Named<TreeView>(window, "FolderTreeView").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<ListBox>(window, "FolderListBox").Visibility);
            // No search box: the issue asked for tree navigation, and arrow keys plus type-ahead
            // are how the folder tree is navigated everywhere else in QuickMail.
            Assert.Equal(Visibility.Collapsed, Named<TextBox>(window, "SearchBox").Visibility);
            // No New Folder button either — this picker is given no way to create one, so neither
            // the button nor its Alt+N must be offered. (The message picker is; that is the
            // difference between the two, and the user guide says so.)
            Assert.Equal(Visibility.Collapsed, Named<Button>(window, "NewFolderButton").Visibility);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ForFolderMoveCopy_NestsSubfoldersUnderTheirParent()
    {
        var window = Picker();
        try
        {
            var roots = Roots(window);

            // Single account → no account header wrapper; INBOX is a root.
            var inbox = roots.FirstOrDefault(n => n.Label == "INBOX");
            Assert.True(inbox is not null, "INBOX is not a root of the picker tree.");

            var projects = inbox!.Children.FirstOrDefault(n => n.Label == "Projects");
            Assert.True(projects is not null, "Projects is not nested under INBOX — the picker is still flat.");
            Assert.Contains(projects!.Children, n => n.Label == "2026");

            // Everything expanded, so type-ahead (which only searches visible nodes) can reach any
            // folder without the user expanding anything first.
            Assert.True(inbox.IsExpanded && projects.IsExpanded, "picker tree nodes are not expanded.");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The backends move and copy by name over the source account's connection and never look at
    /// the destination folder's account (<c>MainViewModel.MoveFolderToAsync</c> →
    /// <c>ImapMailService.RenameFolderAsync</c>). Two accounts with an "Archive" apiece would then
    /// act on the wrong one, and in a tree the account name is only on a header rows away — so the
    /// picker must not offer another account's folders at all.
    /// </summary>
    [StaFact]
    public void ForFolderMoveCopy_OffersOnlyTheSourceFoldersOwnAccount()
    {
        var other = Guid.NewGuid();
        var folders = Folders();
        folders[other] = [Folder("Archive", "Archive", other), Folder("Receipts", "Receipts", other)];

        var window = Picker(accounts: [Account(), Account("Personal", other)], folders: folders);
        try
        {
            var all = Flatten(Roots(window)).ToList();
            Assert.All(all, n => Assert.NotEqual(other, n.Folder?.AccountId ?? AccountId));
            Assert.DoesNotContain(all, n => n.Label == "Receipts");
            // Scoped to one account, so no account header either.
            Assert.DoesNotContain(all, n => n.IsHeader);
        }
        finally { window.Close(); }
    }

    /// <summary>A folder cannot be moved or copied into itself or into one of its own subfolders.</summary>
    [StaFact]
    public void ForFolderMoveCopy_LeavesOutTheFolderBeingMovedAndEverythingUnderIt()
    {
        var window = Picker(source: Folder("INBOX/Projects", "Projects"));
        try
        {
            var labels = Flatten(Roots(window)).Select(n => n.Label).ToList();

            Assert.DoesNotContain("Projects", labels);
            Assert.DoesNotContain("2026", labels);   // the subfolder goes with it
            Assert.Contains("INBOX", labels);        // its parent is still a valid destination
            Assert.Contains("Archive", labels);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Removing the source folder can strip the last child from an IMAP path segment that is not
    /// itself a mailbox. Such a node has no folder behind it, so it can be arrowed onto and never
    /// opened — it must go too.
    /// </summary>
    [StaFact]
    public void ForFolderMoveCopy_DropsPathSegmentsLeftEmptyByTheExclusion()
    {
        // "Clients" is never listed as a folder of its own — only as a path segment of its child.
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [AccountId] = [Folder("INBOX", "INBOX"), Folder("Clients/Acme", "Acme")],
        };

        var window = Picker(source: Folder("Clients/Acme", "Acme"), folders: folders);
        try
        {
            var labels = Flatten(Roots(window)).Select(n => n.Label).ToList();
            Assert.DoesNotContain("Acme", labels);
            Assert.DoesNotContain("Clients", labels);
            Assert.Contains("INBOX", labels);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Scoping and exclusion can between them leave nothing to pick — an account whose only folder
    /// is the one being moved. The factory returns null so the caller can say why instead of putting
    /// up an empty dialog.
    ///
    /// <para>Returning null is also what keeps the window it built from leaking: a WPF Window joins
    /// <c>Application.Current.Windows</c> at construction and leaves only on Close, and one that is
    /// built and dropped stops <c>OnLastWindowClose</c> from ever firing — the zombie process that
    /// keeps holding the single-instance mutex (issue #252). A caller cannot abandon a window it was
    /// never handed. The <c>Close()</c> itself is not asserted here: <c>Application.Windows</c> has
    /// thread affinity to whichever STA thread first created the Application, so a test running on
    /// any other one cannot read it.</para>
    /// </summary>
    [StaFact]
    public void ForFolderMoveCopy_ReturnsNullWhenThereIsNoDestinationToOffer()
    {
        var only = Folder("INBOX", "INBOX");
        var folders = new Dictionary<Guid, List<MailFolderModel>> { [AccountId] = [only] };

        Assert.Null(FolderPickerWindow.ForFolderMoveCopy(
            [Account()], folders, only, "Move Folder To"));
    }

    /// <summary>
    /// The picker opens on the folder the user came from. For a folder move that folder is itself
    /// excluded, so the stand-in is the parent it is being moved out of — the nearest thing to
    /// where the user was that still exists as a destination.
    /// </summary>
    [StaFact]
    public void OpensOnTheParentOfTheFolderBeingMoved()
    {
        var window = Shown(Picker(source: Folder("INBOX/Projects", "Projects")));
        try
        {
            AssertOpenedOn(window, "INBOX");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The parent standing in for the source can itself be unopenable: an IMAP path segment that is
    /// not a mailbox keeps its node when a sibling survives the exclusion. Landing there is the same
    /// failure as landing on nothing, so the nearest real folder beneath it is used instead.
    /// </summary>
    [StaFact]
    public void OpensOnARealFolderWhenTheParentIsOnlyAPathSegment()
    {
        // "Clients" is never a folder of its own; Beta survives the exclusion so its node stays.
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [AccountId] =
            [
                Folder("INBOX", "INBOX"), Folder("Clients/Acme", "Acme"), Folder("Clients/Beta", "Beta"),
            ],
        };

        var window = Shown(Picker(source: Folder("Clients/Acme", "Acme"), folders: folders));
        try
        {
            AssertOpenedOn(window, "Beta");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Landing on nothing is never right, so a source whose parent cannot stand in — a top-level
    /// folder, whose parent is the account root this picker does not offer — still opens on a real
    /// folder rather than on an empty tree.
    /// </summary>
    [StaFact]
    public void OpensOnAFolderEvenWhenTheSourceHasNoParentToFallBackTo()
    {
        var window = Shown(Picker(source: SourceFolder()));   // "Archive", top level
        try
        {
            var selected = Assert.IsType<FolderTreeNode>(
                Named<TreeView>(window, "FolderTreeView").SelectedItem);
            Assert.False(selected.IsHeader);
            AssertOpenedOn(window, selected.Label);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Selection alone is not the fix: a screen reader announces the item that has keyboard focus,
    /// so asserting only <c>SelectedItem</c> would stay green if the opening selection stopped
    /// focusing the container — which is the whole point of the change. Open must be enabled too,
    /// or the picker has opened somewhere the user cannot act.
    /// </summary>
    private static void AssertOpenedOn(Window window, string label)
    {
        var selected = Assert.IsType<FolderTreeNode>(
            Named<TreeView>(window, "FolderTreeView").SelectedItem);
        Assert.Equal(label, selected.Label);

        var focused = Assert.IsType<TreeViewItem>(Keyboard.FocusedElement);
        Assert.Same(selected, focused.DataContext);

        Assert.True(Named<Button>(window, "OpenButton").IsEnabled,
                    $"Open is disabled although the picker opened on '{label}'.");
    }

    /// <summary>
    /// Open follows the selection, and an IMAP path segment that is not itself a mailbox carries no
    /// folder — arrowing onto one must report that there is nothing to open, both through the
    /// control's enabled state and, since Enter is handled by the tree before it can reach the
    /// default button, through an announcement.
    /// </summary>
    [StaFact]
    public void SelectingANodeThatIsNotAFolderDisablesOpenAndSaysSoOnEnter()
    {
        // "Clients" exists only as a path segment of its child, so its node has no folder.
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [AccountId] = [Folder("INBOX", "INBOX"), Folder("Clients/Acme", "Acme")],
        };

        var heard = new List<(string Text, AnnouncementCategory Category)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));

        var window = Shown(Picker(source: Folder("INBOX", "INBOX"), folders: folders));
        try
        {
            var tree    = Named<TreeView>(window, "FolderTreeView");
            var clients = Flatten(Roots(window)).First(n => n.Label == "Clients");
            Assert.Null(clients.Folder);

            Assert.True(TreeViewFocusHelper.SelectTreeViewNode(tree, clients, focusNode: false),
                        "could not select the Clients path segment.");
            Drain();
            Assert.False(Named<Button>(window, "OpenButton").IsEnabled,
                         "Open is enabled on a node with no folder behind it.");

            heard.Clear();
            tree.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
            });
            Drain();

            // Enter must not commit and must not stay silent. (DialogResult would throw on a window
            // never shown as a dialog, so a commit here would fail loudly rather than pass.)
            var spoken = Assert.Single(heard.Where(h => h.Category == AnnouncementCategory.Result));
            Assert.Contains("folder", spoken.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    // ── ForStartupFolder (#516) ──────────────────────────────────────────────────

    private static FolderPickerWindow StartupPicker(string? currentKey = null, Guid? currentAccount = null) =>
        Shown(FolderPickerWindow.ForStartupFolder(
            [Account()], Folders(), MainViewModel.AllVirtualFolders, currentKey, currentAccount));

    [StaFact]
    public void ForStartupFolder_OffersTheVirtualAggregatesAsRoots()
    {
        // The regression this pins: tree mode returned before virtualFolders was ever read, so every
        // aggregate was silently dropped and Settings could not select All Inboxes at all — the most
        // asked-for value of the whole setting. The call-site test that only checked the argument
        // appeared in MainWindow's source passed the entire time.
        var window = StartupPicker();
        try
        {
            var roots = Roots(window);
            Assert.Contains(roots, r => r.Folder?.FullName == MainViewModel.AllInboxesFolder.FullName);
            Assert.Contains(roots, r => r.Folder?.FullName == MainViewModel.AllMailFolder.FullName);

            // Aggregates come first, matching the main folder tree's order.
            Assert.Equal(MainViewModel.AllMailFolder.FullName, roots[0].Folder?.FullName);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ForStartupFolder_StillOffersTheRealFolders()
    {
        var window = StartupPicker();
        try
        {
            Assert.Contains(Flatten(Roots(window)),
                            n => n.Folder is { IsHeader: false } f && f.FullName == "INBOX");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ForStartupFolder_OpensOnTheCurrentVirtualChoice()
    {
        // Stored without the NUL sentinel prefix, so the factory has to restore it to match.
        var window = StartupPicker(currentKey: "AllInboxes");
        try
        {
            var selected = Assert.IsType<FolderTreeNode>(
                Named<TreeView>(window, "FolderTreeView").SelectedItem);
            Assert.Equal(MainViewModel.AllInboxesFolder.FullName, selected.Folder?.FullName);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ForStartupFolder_OpensOnTheCurrentRealFolder()
    {
        var window = StartupPicker(currentKey: "INBOX", currentAccount: AccountId);
        try
        {
            var selected = Assert.IsType<FolderTreeNode>(
                Named<TreeView>(window, "FolderTreeView").SelectedItem);
            Assert.Equal("INBOX", selected.Folder?.FullName);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ForStartupFolder_NeverOpensWithNothingSelected()
    {
        // The picker rule: a tree with no selection announces the tree and no item, leaving the user
        // to work out that Down is what starts things.
        var window = StartupPicker();
        try
        {
            Assert.NotNull(Named<TreeView>(window, "FolderTreeView").SelectedItem);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void MoveCopyPicker_StillOffersNoAggregates()
    {
        // The other side of the same change: an aggregate is not a legal move/copy destination, and
        // adding virtual-folder support to tree mode must not have leaked them in here.
        var window = Picker();
        try
        {
            Assert.DoesNotContain(Flatten(Roots(window)),
                                  n => n.Folder is { } f && f.FullName.StartsWith('\0'));
        }
        finally { window.Close(); }
    }
}
