using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The rule editors — the Rules Manager's "Choose Target Folder" and the server-rule editor's move
/// and copy folder — were the last destination pickers still opening the flat alphabetical list of
/// every folder in every account, after #250 and #431 moved the others to a tree.
///
/// <para>Same approach as <see cref="FolderPickerTreeTests"/>: assert what the picker shows and what
/// it opens on, not the arguments it was handed — asserting the <c>useTreeView</c> flag would keep
/// passing if the flag stopped driving the presentation. That the two editors actually reach this
/// factory is <see cref="RuleTargetPickerCallSiteTests"/>, since neither window can be stood up
/// here.</para>
/// </summary>
[Collection("WpfTests")]
public class RuleTargetPickerTests
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

    private static FolderPickerWindow Picker(
        Guid? accountId = null,
        string? currentFolderKey = null,
        IEnumerable<AccountModel>? accounts = null,
        Dictionary<Guid, List<MailFolderModel>>? folders = null,
        Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>>? folderCreator = null)
        => FolderPickerWindow.ForRuleTarget(
            accounts ?? [Account()], folders ?? Folders(),
            accountId ?? AccountId, currentFolderKey, "Choose Target Folder", folderCreator);

    private static AccountModel Pop3Account(Guid id) => new()
    {
        Id = id, AccountName = "Legacy", Username = "kelly@example.net",
        BackendKind = BackendKind.Pop3Smtp,
    };

    /// <summary>A creator that is never called — these tests only ask whether the button is offered.</summary>
    private static Task<IReadOnlyList<MailFolderModel>?> NeverCreates(Guid _, string? __, string ___)
        => Task.FromResult<IReadOnlyList<MailFolderModel>?>(null);

    /// <summary>Realizes the window so item containers exist, without stealing focus from the desktop.</summary>
    private static FolderPickerWindow Shown(FolderPickerWindow window)
    {
        window.WindowStyle   = WindowStyle.None;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
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

    /// <summary>
    /// Selection alone is not enough: a screen reader announces the item holding keyboard focus, and
    /// a row whose Open is disabled is the same dead end as opening on nothing.
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

    [StaFact]
    public void ShowsTheTreeAndHidesTheFlatList()
    {
        var window = Picker();
        try
        {
            Assert.Equal("Choose Target Folder", window.Title);
            Assert.Equal(Visibility.Visible,   Named<TreeView>(window, "FolderTreeView").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<ListBox>(window, "FolderListBox").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<TextBox>(window, "SearchBox").Visibility);
            // No creator handed in, so neither the button nor its Alt+N must be offered — the state
            // of a caller that has no way to make a folder. The editors do hand one in; see
            // OffersNewFolderWhenTheCallerCanCreateOne.
            Assert.Equal(Visibility.Collapsed, Named<Button>(window, "NewFolderButton").Visibility);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void NestsSubfoldersUnderTheirParent()
    {
        var window = Picker();
        try
        {
            var inbox = Roots(window).FirstOrDefault(n => n.Label == "INBOX");
            Assert.True(inbox is not null, "INBOX is not a root of the picker tree.");

            var projects = inbox!.Children.FirstOrDefault(n => n.Label == "Projects");
            Assert.True(projects is not null, "Projects is not nested under INBOX — the picker is still flat.");
            Assert.Contains(projects!.Children, n => n.Label == "2026");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A rule files mail within one mailbox, so another account's folder is not a second option: at
    /// best a name that does not exist there, at worst one that does — "Archive" resolves on both and
    /// the rule quietly files into the wrong mailbox. The flat list spelled the account into every
    /// row ("Personal - Archive"); a tree carries it on a header rows away, so scoping is what keeps
    /// them apart.
    /// </summary>
    [StaFact]
    public void OffersOnlyTheRulesOwnAccount()
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
        }
        finally { window.Close(); }
    }

    /// <summary>Editing a rule that already files somewhere opens on that folder, not at the top.</summary>
    [StaFact]
    public void OpensOnTheFolderTheRuleAlreadyTargets()
    {
        var window = Shown(Picker(currentFolderKey: "INBOX/Projects"));
        try
        {
            AssertOpenedOn(window, "Projects");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A Graph folder's <c>FullName</c> is an opaque id rather than a path, and that same id is what
    /// a server rule stores as its move/copy target — so the lookup must match on FullName, not on
    /// anything path-shaped.
    /// </summary>
    [StaFact]
    public void OpensOnTheTargetFolderForGraphShapedOpaqueIds()
    {
        var graphFolders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [AccountId] =
            [
                new MailFolderModel
                {
                    FullName = "AAMkAGI2T0AAA=", DisplayName = "Inbox",
                    AccountId = AccountId, Kind = SpecialFolderKind.Inbox,
                },
                new MailFolderModel
                {
                    FullName = "AAMkAGI2NEWSLETTERS=", DisplayName = "Newsletters", AccountId = AccountId,
                },
            ],
        };

        var window = Shown(Picker(currentFolderKey: "AAMkAGI2NEWSLETTERS=", folders: graphFolders));
        try
        {
            AssertOpenedOn(window, "Newsletters");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A new rule has no target yet. Opening with nothing selected announces the tree and no item and
    /// leaves the user to work out that Down is what starts things, so a real folder stands in.
    /// </summary>
    [StaFact]
    public void NewRuleWithNoTargetStillOpensOnARealFolder()
    {
        var window = Shown(Picker(currentFolderKey: null));
        try
        {
            var selected = Assert.IsType<FolderTreeNode>(
                Named<TreeView>(window, "FolderTreeView").SelectedItem);
            Assert.False(selected.IsHeader);
            Assert.NotNull(selected.Folder);
            AssertOpenedOn(window, selected.Label);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A target the account no longer has — the folder was deleted or renamed on the server since the
    /// rule was written — must not open on nothing either.
    /// </summary>
    [StaFact]
    public void TargetThatNoLongerExistsStillOpensOnARealFolder()
    {
        var window = Shown(Picker(currentFolderKey: "INBOX/Deleted Long Ago"));
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
    /// An unscoped picker beats an empty one. A rule with no account (nothing to scope to) and an
    /// account whose folders are not cached yet both fall back to every account rather than putting
    /// up a tree with no folder in it.
    /// </summary>
    [StaFact]
    public void FallsBackToEveryAccountWhenThereIsNothingToScopeTo()
    {
        var other = Guid.NewGuid();
        var folders = Folders();
        folders[other] = [Folder("Receipts", "Receipts", other)];
        var accounts = new[] { Account(), Account("Personal", other) };

        // Straight to the factory: Picker()'s "accountId ?? AccountId" default cannot express the
        // rule-has-no-account case, which is the whole point of this half of the test.
        var unscoped = FolderPickerWindow.ForRuleTarget(
            accounts, folders, accountId: null, currentFolderKey: null, "Choose Target Folder");
        try
        {
            Assert.Contains(Flatten(Roots(unscoped)), n => n.Label == "Receipts");
        }
        finally { unscoped.Close(); }

        var uncached = Picker(accountId: Guid.NewGuid(), accounts: accounts, folders: folders);
        try
        {
            var all = Flatten(Roots(uncached)).ToList();
            Assert.Contains(all, n => n.Label == "Receipts");
            Assert.Contains(all, n => n.Label == "Archive");
        }
        finally { uncached.Close(); }
    }

    /// <summary>
    /// Writing a rule is where a user decides a folder should exist ("file this newsletter under
    /// News"), so making them abandon the rule, create the folder in the main window and start over
    /// is the wrong order of work (issue #645). Given a creator, the picker offers the same New
    /// Folder button the move/copy-message picker has had since #250.
    /// </summary>
    [StaFact]
    public void OffersNewFolderWhenTheCallerCanCreateOne()
    {
        var window = Picker(folderCreator: NeverCreates);
        try
        {
            Assert.Equal(Visibility.Visible, Named<Button>(window, "NewFolderButton").Visibility);
        }
        finally { window.Close(); }
    }

    /// <summary>POP3 has no server folders to create (#128), so the account a rule files into being a
    /// POP3 one withholds the button even though the caller supplied a creator.</summary>
    [StaFact]
    public void WithholdsNewFolderForAnAccountThatCannotManageFolders()
    {
        var pop = Guid.NewGuid();
        var folders = Folders();
        folders[pop] = [Folder("INBOX", "Inbox", pop)];

        var window = Picker(
            accountId: pop, accounts: [Account(), Pop3Account(pop)], folders: folders,
            folderCreator: NeverCreates);
        try
        {
            Assert.Equal(Visibility.Collapsed, Named<Button>(window, "NewFolderButton").Visibility);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The unscoped fallback shows every account, and the button creates under whichever node is
    /// selected — so it would make the folder in one mailbox while the rule files into another. The
    /// user watches the folder appear and reasonably concludes the target is real, which is worse
    /// than the wrong-folder <em>pick</em> the fallback already allows. So the button is withheld
    /// there, even with every account able to manage folders.
    /// </summary>
    [StaFact]
    public void WithholdsNewFolderOnTheUnscopedFallbackTree()
    {
        var other = Guid.NewGuid();
        var folders = Folders();
        folders[other] = [Folder("Receipts", "Receipts", other)];
        var accounts = new[] { Account(), Account("Personal", other) };

        var noAccount = FolderPickerWindow.ForRuleTarget(
            accounts, folders, accountId: null, currentFolderKey: null, "Choose Target Folder", NeverCreates);
        try
        {
            Assert.Contains(Flatten(Roots(noAccount)), n => n.Label == "Receipts");
            Assert.Equal(Visibility.Collapsed, Named<Button>(noAccount, "NewFolderButton").Visibility);
        }
        finally { noAccount.Close(); }

        // Same fallback, reached the other way: the rule has an account, but its folders are not
        // cached yet, so there is nothing to scope to.
        var uncached = Picker(
            accountId: Guid.NewGuid(), accounts: accounts, folders: folders, folderCreator: NeverCreates);
        try
        {
            Assert.Equal(Visibility.Collapsed, Named<Button>(uncached, "NewFolderButton").Visibility);
        }
        finally { uncached.Close(); }
    }
}
