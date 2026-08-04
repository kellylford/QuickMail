using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
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
    /// The tree opens with nothing selected — deliberately, so a first Enter cannot commit a
    /// destination the user never chose. Open must report that it has nothing to act on through the
    /// control's own enabled state, which reaches the user whatever their announcement settings
    /// are, rather than only through the announcement below.
    /// </summary>
    [StaFact]
    public void OpenIsDisabledUntilARealFolderIsSelected()
    {
        var window = Shown(Picker());
        try
        {
            var open = Named<Button>(window, "OpenButton");
            Assert.False(open.IsEnabled, "Open is enabled with nothing selected.");

            var tree  = Named<TreeView>(window, "FolderTreeView");
            var inbox = Roots(window).First(n => n.Label == "INBOX");
            Assert.True(TreeViewFocusHelper.SelectTreeViewNode(tree, inbox, focusNode: false),
                        "could not select INBOX in the picker tree.");
            Drain();

            Assert.True(open.IsEnabled, "Open is still disabled after selecting a real folder.");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Enter is handled by the tree before it can reach the (disabled) default button, so it still
    /// has to say why nothing happened. Before the tree, this dialog had no unopenable rows at all.
    /// </summary>
    [StaFact]
    public void PressingEnterWithNoFolderSelectedSaysSoInsteadOfDoingNothing()
    {
        var heard = new List<(string Text, AnnouncementCategory Category)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));

        var window = Shown(Picker());
        try
        {
            var tree = Named<TreeView>(window, "FolderTreeView");
            // Nothing selected — the state the picker opens in. Enter must not commit, and must not
            // stay silent. (DialogResult would throw on a window that was never shown as a dialog,
            // so a commit here would fail loudly rather than pass.)
            tree.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
            });
            Drain();

            var spoken = Assert.Single(heard.Where(h => h.Category == AnnouncementCategory.Result));
            Assert.Contains("folder", spoken.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }
}
