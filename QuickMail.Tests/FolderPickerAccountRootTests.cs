using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
/// The folder picker's New Folder could not create a top-level folder when it showed only one
/// account (issue #950) — the rule-target picker and moving or copying messages from one account.
/// The account header, which is where "create it at the top level" lives, was added only for
/// several accounts, and the picker always opens on a real folder, so whatever the user selected
/// the new folder went inside it.
///
/// <para>A picker that offers New Folder now shows the account as its root even with one account.
/// These tests pin that, that the picker still opens on the folder it was asked to, that New Folder
/// on the root hands the creator a null parent and selects the result, and that pickers which
/// cannot create folders keep the flat single-account tree they had.</para>
/// </summary>
[Collection("WpfTests")]
public class FolderPickerAccountRootTests
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

    private static List<MailFolderModel> FolderList() =>
    [
        new MailFolderModel
        {
            FullName = "INBOX", DisplayName = "INBOX",
            AccountId = AccountId, Kind = SpecialFolderKind.Inbox,
        },
        Folder("INBOX/Projects", "Projects"),
        Folder("Archive", "Archive"),
    ];

    private static Dictionary<Guid, List<MailFolderModel>> Folders() => new() { [AccountId] = FolderList() };

    /// <summary>Records what the picker hands the creator, and answers with <see cref="Result"/>.</summary>
    private sealed class RecordingCreator
    {
        public List<(Guid AccountId, string? Parent, string Name)> Calls { get; } = [];
        public Func<string?, string, IReadOnlyList<MailFolderModel>?> Result { get; set; } = (_, _) => null;

        public Task<IReadOnlyList<MailFolderModel>?> CreateAsync(Guid accountId, string? parent, string name)
        {
            Calls.Add((accountId, parent, name));
            return Task.FromResult(Result(parent, name));
        }
    }

    private static Task<IReadOnlyList<MailFolderModel>?> NeverCreates(Guid _, string? __, string ___)
        => Task.FromResult<IReadOnlyList<MailFolderModel>?>(null);

    /// <summary>The shape of MainWindow.BuildMessageFolderPicker for messages from one account.</summary>
    private static FolderPickerWindow MessagePicker(
        Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>>? creator,
        MailFolderModel? initialFolder = null) =>
        new([Account()], Folders(), title: "Move to Folder", useTreeView: true,
            initialFolder: initialFolder, folderCreator: creator);

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

    /// <summary>The tree's only root, asserted to be this account's header.</summary>
    private static FolderTreeNode AccountRoot(Window window)
    {
        var root = Assert.Single(Roots(window));
        Assert.True(root.IsHeader, $"The root '{root.Label}' is not an account header.");
        Assert.Equal(AccountId, root.AccountId);
        Assert.Equal("Work", root.Label);
        Assert.Null(root.Folder);
        return root;
    }

    /// <summary>Selected, focused, and Open enabled: what "the picker opened on it" means to a user.</summary>
    private static void AssertOpenedOn(Window window, string label)
    {
        var selected = Assert.IsType<FolderTreeNode>(
            Named<TreeView>(window, "FolderTreeView").SelectedItem);
        Assert.Equal(label, selected.Label);
        Assert.False(selected.IsHeader);

        var focused = Assert.IsType<TreeViewItem>(Keyboard.FocusedElement);
        Assert.Same(selected, focused.DataContext);

        Assert.True(Named<Button>(window, "OpenButton").IsEnabled,
                    $"Open is disabled although the picker opened on '{label}'.");
    }

    private static void SelectNode(Window window, FolderTreeNode node)
    {
        Assert.True(TreeViewFocusHelper.SelectTreeViewNode(Named<TreeView>(window, "FolderTreeView"), node),
                    $"could not select '{node.Label}'.");
        Drain();
    }

    // ── The root is there when New Folder is ──────────────────────────────────────

    [StaFact]
    public void RuleTargetPickerWithACreatorShowsTheAccountAsItsRoot()
    {
        var window = FolderPickerWindow.ForRuleTarget(
            [Account()], Folders(), AccountId, currentFolderKey: null, "Choose Target Folder", NeverCreates);
        try
        {
            var root = AccountRoot(window);
            // Every folder sits beneath it, nested as before.
            Assert.Contains(root.Children, n => n.Label == "INBOX");
            Assert.Contains(root.Children, n => n.Label == "Archive");
            Assert.Contains(root.Children.First(n => n.Label == "INBOX").Children, n => n.Label == "Projects");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void SingleAccountMessagePickerWithACreatorShowsTheAccountAsItsRoot()
    {
        var window = MessagePicker(NeverCreates);
        try
        {
            AccountRoot(window);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The extra level must not move the opening selection: the picker still opens on the folder the
    /// rule already files into, never on the new header.
    /// </summary>
    [StaFact]
    public void RuleTargetPickerStillOpensOnTheRulesFolder()
    {
        var window = Shown(FolderPickerWindow.ForRuleTarget(
            [Account()], Folders(), AccountId, "INBOX/Projects", "Choose Target Folder", NeverCreates));
        try
        {
            AccountRoot(window);
            AssertOpenedOn(window, "Projects");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void MessagePickerStillOpensOnTheInitialFolder()
    {
        var archive = FolderList().First(f => f.FullName == "Archive");
        var window = Shown(MessagePicker(NeverCreates, initialFolder: archive));
        try
        {
            AssertOpenedOn(window, "Archive");
        }
        finally { window.Close(); }
    }

    /// <summary>With nothing to open on, the stand-in is a real folder under the header, not the header.</summary>
    [StaFact]
    public void WithNoInitialFolderItOpensOnARealFolderNotTheHeader()
    {
        var window = Shown(MessagePicker(NeverCreates));
        try
        {
            AssertOpenedOn(window, "INBOX");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void OpenIsDisabledOnTheAccountRoot()
    {
        var window = Shown(MessagePicker(NeverCreates));
        try
        {
            SelectNode(window, AccountRoot(window));
            Assert.False(Named<Button>(window, "OpenButton").IsEnabled,
                         "Open is enabled on the account header, which is not a folder.");
        }
        finally { window.Close(); }
    }

    // ── New Folder on the root ────────────────────────────────────────────────────

    [StaFact]
    public async Task NewFolderOnTheRootCreatesATopLevelFolderAndSelectsIt()
    {
        var creator = new RecordingCreator
        {
            Result = (_, name) => [.. FolderList(), Folder(name, name)],
        };
        var window = Shown(MessagePicker(creator.CreateAsync));
        try
        {
            SelectNode(window, AccountRoot(window));

            string? promptedWith = null;
            await window.CreateFolderAtSelectionAsync(parent => { promptedWith = parent; return "News"; });
            Drain();

            // The dialog names the account as where the folder goes, and the creator is told to put
            // it at the top level: a null parent.
            Assert.Equal("Work", promptedWith);
            var call = Assert.Single(creator.Calls);
            Assert.Equal(AccountId, call.AccountId);
            Assert.Null(call.Parent);
            Assert.Equal("News", call.Name);

            // The rebuilt tree keeps the root, the new folder sits directly beneath it, and it is
            // selected and focused so the user can Open it straight away.
            var root = AccountRoot(window);
            var news = root.Children.FirstOrDefault(n => n.Label == "News");
            Assert.True(news is not null, "The new folder is not a direct child of the account root.");
            Assert.Equal("News", news!.Folder?.FullName);
            AssertOpenedOn(window, "News");
        }
        finally { window.Close(); }
    }

    /// <summary>Unchanged: New Folder on a folder still creates inside it.</summary>
    [StaFact]
    public async Task NewFolderOnAFolderStillCreatesInsideIt()
    {
        var creator = new RecordingCreator
        {
            Result = (parent, name) => [.. FolderList(), Folder($"{parent}/{name}", name)],
        };
        var window = Shown(MessagePicker(creator.CreateAsync));
        try
        {
            SelectNode(window, Flatten(Roots(window)).First(n => n.Label == "Archive"));

            string? promptedWith = null;
            await window.CreateFolderAtSelectionAsync(parent => { promptedWith = parent; return "2026"; });
            Drain();

            Assert.Equal("Archive", promptedWith);
            var call = Assert.Single(creator.Calls);
            Assert.Equal("Archive", call.Parent);

            var archive = Flatten(Roots(window)).First(n => n.Label == "Archive");
            Assert.Contains(archive.Children, n => n.Label == "2026");
            AssertOpenedOn(window, "2026");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public async Task CancellingTheNamePromptCreatesNothing()
    {
        var creator = new RecordingCreator();
        var window = Shown(MessagePicker(creator.CreateAsync));
        try
        {
            SelectNode(window, AccountRoot(window));
            await window.CreateFolderAtSelectionAsync(_ => null);
            Drain();

            Assert.Empty(creator.Calls);
        }
        finally { window.Close(); }
    }

    /// <summary>A failed create leaves the tree as it was, root and all, and says so.</summary>
    [StaFact]
    public async Task AFailedCreateOnTheRootIsAnnouncedAndLeavesTheTreeAlone()
    {
        var creator = new RecordingCreator { Result = (_, _) => null };
        var heard = new List<(string Text, AnnouncementCategory Category)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));

        var window = Shown(MessagePicker(creator.CreateAsync));
        try
        {
            SelectNode(window, AccountRoot(window));
            await window.CreateFolderAtSelectionAsync(_ => "News");
            Drain();

            Assert.Null(Assert.Single(creator.Calls).Parent);
            Assert.DoesNotContain(Flatten(Roots(window)), n => n.Label == "News");
            AccountRoot(window);
            Assert.Contains(heard, h => h.Category == AnnouncementCategory.Result &&
                                        h.Text.Contains("News", StringComparison.Ordinal));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    // ── Pickers that cannot create folders are unchanged ──────────────────────────

    [StaFact]
    public void RuleTargetPickerWithoutACreatorHasNoAccountRoot()
    {
        var window = FolderPickerWindow.ForRuleTarget(
            [Account()], Folders(), AccountId, currentFolderKey: null, "Choose Target Folder");
        try
        {
            var roots = Roots(window);
            Assert.DoesNotContain(Flatten(roots), n => n.IsHeader);
            Assert.Contains(roots, n => n.Label == "INBOX");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void MessagePickerWithoutACreatorHasNoAccountRoot()
    {
        var window = MessagePicker(creator: null);
        try
        {
            Assert.DoesNotContain(Flatten(Roots(window)), n => n.IsHeader);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void FolderMoveCopyPickerHasNoAccountRoot()
    {
        var window = FolderPickerWindow.ForFolderMoveCopy(
            [Account()], Folders(), Folder("Archive", "Archive"), "Move Folder To");
        Assert.NotNull(window);
        try
        {
            Assert.DoesNotContain(Flatten(Roots(window!)), n => n.IsHeader);
        }
        finally { window!.Close(); }
    }

    [StaFact]
    public void StartupPickerHasNoAccountRoot()
    {
        var window = FolderPickerWindow.ForStartupFolder(
            [Account()], Folders(), MainViewModel.AllVirtualFolders, currentKey: null, currentAccountId: null);
        try
        {
            Assert.DoesNotContain(Flatten(Roots(window)), n => n.IsHeader);
        }
        finally { window.Close(); }
    }

    /// <summary>The flat list never offers New Folder, so a creator changes nothing there.</summary>
    [StaFact]
    public void FlatListIsUntouchedByACreator()
    {
        var window = new FolderPickerWindow(
            [Account()], Folders(), title: "Go to Folder", useTreeView: false, folderCreator: NeverCreates);
        try
        {
            Assert.Equal(Visibility.Collapsed, Named<TreeView>(window, "FolderTreeView").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<Button>(window, "NewFolderButton").Visibility);
        }
        finally { window.Close(); }
    }
}
