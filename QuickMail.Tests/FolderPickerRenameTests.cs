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
/// Rename Folder inside the folder pickers (#1083): the picker is modal over the main window's
/// WebView2, so it renames in a box of its own rather than opening another window from inside its
/// loop. These drive that box in both presentations — the tree the destination pickers use and the
/// flat list Go to Folder uses — with the rename itself faked, since what happens on the server and
/// to the stored references is MainViewModel's (RenameFolderTests).
/// </summary>
[Collection("WpfTests")]
public class FolderPickerRenameTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private static AccountModel Account(string label = "Work", Guid? id = null) => new()
    {
        Id = id ?? AccountId, AccountName = label, Username = $"{label.ToLowerInvariant()}@example.com",
    };

    private static MailFolderModel Folder(string fullName, string display, SpecialFolderKind kind = SpecialFolderKind.None) => new()
    {
        FullName = fullName, DisplayName = display, AccountId = AccountId, Kind = kind,
    };

    private static List<MailFolderModel> FolderList(string projects = "Projects") =>
    [
        Folder("INBOX", "Inbox", SpecialFolderKind.Inbox),
        Folder($"INBOX/{projects}", projects),
        Folder("Archive", "Archive"),
    ];

    /// <summary>Records what the picker asks, refuses what the app would, and answers with <see cref="Answer"/>.</summary>
    private sealed class FakeRenamer
    {
        public List<(MailFolderModel Folder, string Name)> Calls { get; } = [];
        public Func<MailFolderModel, string, FolderRenameResult> Answer { get; set; } =
            (folder, name) => new FolderRenameResult(
                true, $"Renamed {folder.DisplayName} to {name}.", string.Empty,
                new FolderRename(AccountId, folder.FullName, $"INBOX/{name}", '/', folder.DisplayName, name),
                FolderList(name));

        public FolderRenameSupport Support => new(
            folder => folder.Kind != SpecialFolderKind.None ? $"{folder.DisplayName} is a standard folder, so it cannot be renamed." : null,
            (folder, name) =>
            {
                Calls.Add((folder, name));
                return Task.FromResult(Answer(folder, name));
            });
    }

    private static FolderPickerWindow TreePicker(FakeRenamer renamer, IEnumerable<AccountModel>? accounts = null) =>
        Shown(new FolderPickerWindow(
            accounts ?? [Account()],
            new Dictionary<Guid, List<MailFolderModel>> { [AccountId] = FolderList() },
            title: "Move to Folder", useTreeView: true, folderRenamer: renamer.Support));

    private static FolderPickerWindow ListPicker(FakeRenamer renamer) =>
        Shown(new FolderPickerWindow(
            [Account()],
            new Dictionary<Guid, List<MailFolderModel>> { [AccountId] = FolderList() },
            virtualFolders: [ViewModels.MainViewModel.AllInboxesFolder],
            folderRenamer: renamer.Support));

    private static FolderPickerWindow Shown(FolderPickerWindow window)
    {
        WpfTestHost.EnsureApplication();
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
        Assert.IsAssignableFrom<IEnumerable<FolderTreeNode>>(Named<TreeView>(window, "FolderTreeView").ItemsSource).ToList();

    private static IEnumerable<FolderTreeNode> Flatten(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private static FolderTreeNode NodeLabelled(Window window, string label) =>
        Flatten(Roots(window)).Single(n => n.Label == label);

    private static List<(string Text, AnnouncementCategory Category)> Listen()
    {
        var heard = new List<(string, AnnouncementCategory)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));
        return heard;
    }

    private static void PressInRenameBox(Window window, Key key)
    {
        var box = Named<TextBox>(window, "RenameBox");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        box.RaiseEvent(args);
        Assert.True(args.Handled, $"{key} in the rename box was not handled.");
    }

    // ── Opening the box ──────────────────────────────────────────────────────

    [StaFact]
    public void ARenamablePicker_HasTheContextMenu_AndOneWithoutRenameHasNone()
    {
        var with = TreePicker(new FakeRenamer());
        var without = Shown(new FolderPickerWindow(
            [Account()], new Dictionary<Guid, List<MailFolderModel>> { [AccountId] = FolderList() },
            title: "Choose Startup Folder", useTreeView: true));
        try
        {
            Assert.NotNull(with.TryFindResource("PickerItemContextMenu") as ContextMenu);
            Assert.Null(without.TryFindResource("PickerItemContextMenu"));
        }
        finally { with.Close(); without.Close(); }
    }

    [StaFact]
    public void BeginRename_OpensTheBoxWithTheName_AndSetsTheFoldersAside()
    {
        var window = TreePicker(new FakeRenamer());
        try
        {
            Assert.True(window.BeginRename(NodeLabelled(window, "Projects")));
            Drain();

            Assert.True(window.IsRenaming);
            Assert.Equal(Visibility.Visible, Named<StackPanel>(window, "RenamePanel").Visibility);
            Assert.Equal("Projects", Named<TextBox>(window, "RenameBox").Text);
            Assert.Equal("New name for Projects:", Named<Label>(window, "RenameLabel").Content);
            Assert.False(Named<TreeView>(window, "FolderTreeView").IsEnabled);
            Assert.False(Named<Button>(window, "OpenButton").IsEnabled);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void AStandardFolder_IsRefused_WithTheReason()
    {
        var heard = Listen();
        var window = TreePicker(new FakeRenamer());
        try
        {
            Assert.False(window.BeginRename(NodeLabelled(window, "Inbox")));
            Assert.False(window.IsRenaming);
            Assert.Contains(("Inbox is a standard folder, so it cannot be renamed.", AnnouncementCategory.Result), heard);
        }
        finally { AccessibilityHelper.AnnouncementObserver = null; window.Close(); }
    }

    [StaFact]
    public void AnAccount_IsRefused_WithTheReason()
    {
        var heard = Listen();
        // Two accounts, so each shows as a header above its folders.
        var window = TreePicker(new FakeRenamer(), [Account(), Account("Home", Guid.NewGuid())]);
        try
        {
            var header = Roots(window).First(n => n.IsHeader);
            Assert.False(window.BeginRename(header));
            Assert.Contains(($"'{header.Label}' is an account, not a folder. To rename an account, use Manage Accounts.",
                             AnnouncementCategory.Result), heard);
        }
        finally { AccessibilityHelper.AnnouncementObserver = null; window.Close(); }
    }

    // ── Renaming ─────────────────────────────────────────────────────────────

    [StaFact]
    public async Task Enter_Renames_RebuildsTheTree_AndLandsOnTheRenamedFolder()
    {
        var renamer = new FakeRenamer();
        var window = TreePicker(renamer);
        try
        {
            var projects = NodeLabelled(window, "Projects");
            window.BeginRename(projects);
            Named<TextBox>(window, "RenameBox").Text = "Clients";

            await window.CommitRenameAsync();
            Drain();

            var call = Assert.Single(renamer.Calls);
            Assert.Equal(("INBOX/Projects", "Clients"), (call.Folder.FullName, call.Name));
            Assert.False(window.IsRenaming);
            Assert.Equal(Visibility.Collapsed, Named<StackPanel>(window, "RenamePanel").Visibility);
            Assert.True(Named<TreeView>(window, "FolderTreeView").IsEnabled);
            Assert.DoesNotContain(Flatten(Roots(window)), n => n.Label == "Projects");
            var selected = Assert.IsType<FolderTreeNode>(Named<TreeView>(window, "FolderTreeView").SelectedItem);
            Assert.Equal(("Clients", "INBOX/Clients"), (selected.Label, selected.Folder?.FullName));
            Assert.True(Named<Button>(window, "OpenButton").IsEnabled);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public async Task WhatElseChanged_IsSaidAfterTheRename()
    {
        var heard = Listen();
        var renamer = new FakeRenamer();
        var plain = renamer.Answer;
        renamer.Answer = (folder, name) => plain(folder, name) with { Announcement = "1 rule updated." };
        var window = TreePicker(renamer);
        try
        {
            window.BeginRename(NodeLabelled(window, "Projects"));
            Named<TextBox>(window, "RenameBox").Text = "Clients";
            await window.CommitRenameAsync();
            Drain();
            Drain();

            Assert.Contains(("1 rule updated.", AnnouncementCategory.Result), heard);
        }
        finally { AccessibilityHelper.AnnouncementObserver = null; window.Close(); }
    }

    [StaFact]
    public async Task ARefusal_KeepsTheBoxOpen_AndSaysWhy()
    {
        var heard = Listen();
        var renamer = new FakeRenamer { Answer = (_, _) => FolderRenameResult.Refused("There is already a folder named Archive there.") };
        var window = TreePicker(renamer);
        try
        {
            window.BeginRename(NodeLabelled(window, "Projects"));
            Named<TextBox>(window, "RenameBox").Text = "Archive";

            await window.CommitRenameAsync();
            Drain();

            Assert.True(window.IsRenaming);
            Assert.Equal("Archive", Named<TextBox>(window, "RenameBox").Text);
            Assert.False(Named<TextBox>(window, "RenameBox").IsReadOnly);
            Assert.Contains(("There is already a folder named Archive there.", AnnouncementCategory.Result), heard);
            Assert.NotNull(NodeLabelled(window, "Projects"));
        }
        finally { AccessibilityHelper.AnnouncementObserver = null; window.Close(); }
    }

    [StaFact]
    public void Escape_ClosesTheBox_NotThePicker_AndRenamesNothing()
    {
        var renamer = new FakeRenamer();
        var window = TreePicker(renamer);
        try
        {
            var projects = NodeLabelled(window, "Projects");
            window.BeginRename(projects);
            Named<TextBox>(window, "RenameBox").Text = "Clients";

            PressInRenameBox(window, Key.Escape);
            Drain();

            Assert.False(window.IsRenaming);
            Assert.True(window.IsVisible);
            Assert.Empty(renamer.Calls);
            Assert.Same(projects, Named<TreeView>(window, "FolderTreeView").SelectedItem);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Enter_InTheBox_IsTheRenamesNotOpens()
    {
        var renamer = new FakeRenamer();
        var window = TreePicker(renamer);
        try
        {
            window.BeginRename(NodeLabelled(window, "Projects"));
            Named<TextBox>(window, "RenameBox").Text = "Clients";

            PressInRenameBox(window, Key.Enter);
            Drain();

            Assert.Single(renamer.Calls);
            Assert.Null(window.SelectedFolder);   // the picker did not take Enter as Open
        }
        finally { window.Close(); }
    }

    // ── Go to Folder's list ──────────────────────────────────────────────────

    [StaFact]
    public async Task TheList_RenamesToo_AndKeepsItsVirtualFolders()
    {
        var renamer = new FakeRenamer();
        var window = ListPicker(renamer);
        try
        {
            var list = Named<ListBox>(window, "FolderListBox");
            var items = list.Items.Cast<FolderPickerWindow.FolderPickerItem>().ToList();
            var allInboxes = items.Single(i => i.Folder == ViewModels.MainViewModel.AllInboxesFolder);

            var projects = items.Single(i => i.Folder.FullName == "INBOX/Projects");
            list.SelectedItem = projects;
            Assert.True(window.BeginRename(projects));
            Assert.Equal(Visibility.Collapsed, Named<TextBox>(window, "SearchBox").Visibility);
            Named<TextBox>(window, "RenameBox").Text = "Clients";

            await window.CommitRenameAsync();
            Drain();

            Assert.Equal(Visibility.Visible, Named<TextBox>(window, "SearchBox").Visibility);
            var selected = Assert.IsType<FolderPickerWindow.FolderPickerItem>(list.SelectedItem);
            Assert.Equal("INBOX/Clients", selected.Folder.FullName);
            Assert.DoesNotContain(list.Items.Cast<FolderPickerWindow.FolderPickerItem>(), i => i.Folder.FullName == "INBOX/Projects");
            Assert.Contains(list.Items.Cast<FolderPickerWindow.FolderPickerItem>(), i => ReferenceEquals(i.Folder, allInboxes.Folder));
        }
        finally { window.Close(); }
    }
}
