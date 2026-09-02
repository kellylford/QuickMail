// Issue #644: the address book's contact rows were announced as
// "QuickMail.Models.ContactModel 1 of 35".
//
// The composed accessible name was set on a Grid *inside* the DataTemplate. A screen
// reader reads the item's automation peer, whose name comes from the ROW CONTAINER
// (the ListViewItem) and, failing that, from the item's ToString() — never from an
// element inside the template. Every other list in the app puts the name on the
// container via an ItemContainerStyle setter; these three did not.
//
// These tests read the live ListViewItemAutomationPeer names — the ground truth a
// screen reader speaks — rather than eyeballing the template, which is what let this
// ship: on screen the rows looked (and still look) perfect.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

// Part of the WpfTests collection so it never runs concurrently with another WPF/STA
// test: this class Show()s a real Window on its own STA thread (issue #211).
[Collection("WpfTests")]
public class AddressBookRowSpeechTests
{
    [StaFact]
    public void ContactList_ItemPeerNames_SpeakTheContact()
    {
        var (window, dir) = BuildWindow();
        try
        {
            var names = ItemPeerNames(window, "ContactList");

            // The stamped AccessibleName: name, address, and which book it came from.
            Assert.Contains("Alice Adams, alice@example.com, Local address book", names);

            // A prior-recipient contact has no display name; the address carries the row.
            Assert.Contains("zeta@example.com, Local address book", names);

            AssertNoTypeNames(names);
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void GroupsList_ItemPeerNames_SpeakTheGroup()
    {
        var (window, dir) = BuildWindow();
        try
        {
            SelectGroupsTab(window);
            var names = ItemPeerNames(window, "GroupsList");

            Assert.Contains(names, n => n.StartsWith("Work", StringComparison.Ordinal));
            AssertNoTypeNames(names);
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void GroupMembersList_ItemPeerNames_SpeakTheMember()
    {
        var (window, dir) = BuildWindow();
        try
        {
            SelectGroupsTab(window);

            var groups = window.FindName("GroupsList") as ListView;
            Assert.NotNull(groups);
            groups!.SelectedItem = groups.Items.Cast<GroupModel>().First(g => g.Name == "Work");
            window.UpdateLayout();
            Drain();

            var names = ItemPeerNames(window, "GroupMembersList");
            Assert.Contains("Alice Adams <alice@example.com>", names);
            AssertNoTypeNames(names);
        }
        finally { Cleanup(window, dir); }
    }

    /// <summary>
    /// The mechanism, not just the outcome. For the Groups and group-members lists the composed
    /// name happens to equal the item's ToString(), so the peer-name tests above still pass if
    /// someone moves AutomationProperties.Name back inside the DataTemplate — the ToString()
    /// fallback silently covers for it, and #644 comes back the next time a list gains a richer
    /// name than its ToString(). This asserts what actually has to be true: every realized row
    /// CONTAINER carries the name itself.
    /// </summary>
    [StaFact]
    public void EveryList_PutsTheNameOnTheRowContainer_NotInsideTheTemplate()
    {
        var (window, dir) = BuildWindow();
        try
        {
            AssertContainersAreNamed(window, "ContactList");

            SelectGroupsTab(window);
            AssertContainersAreNamed(window, "GroupsList");

            var groups = window.FindName("GroupsList") as ListView;
            Assert.NotNull(groups);
            groups!.SelectedItem = groups.Items.Cast<GroupModel>().First(g => g.Name == "Work");
            window.UpdateLayout();
            Drain();

            AssertContainersAreNamed(window, "GroupMembersList");
        }
        finally { Cleanup(window, dir); }
    }

    private static void AssertContainersAreNamed(Window window, string listName)
    {
        var list = window.FindName(listName) as ListView;
        Assert.NotNull(list);
        list!.UpdateLayout();
        Drain();

        var containers = list.Items
            .Cast<object>()
            .Select(item => list.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem)
            .ToList();

        Assert.NotEmpty(containers);
        foreach (var container in containers)
        {
            Assert.NotNull(container);
            Assert.False(
                string.IsNullOrWhiteSpace(AutomationProperties.GetName(container!)),
                $"A row container in {listName} has no AutomationProperties.Name — the name is " +
                "inside the DataTemplate again, where a screen reader never reads it (issue #644).");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (AddressBookWindow window, string dir) BuildWindow()
    {
        // MainWindow-derived item container styles resolve against the implicit styles in
        // ThemedControls, merged at runtime by ThemeService before the first window exists.
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

        var dir = Path.Combine(Path.GetTempPath(), $"QM-AddressBookSpeech-{Guid.NewGuid():N}");
        var svc = new ContactService(new ProfileContext(dir));

        foreach (var (name, email) in new[]
                 {
                     ("Alice Adams", "alice@example.com"),
                     ("Bob Baker",   "bob@example.com"),
                     ("",            "zeta@example.com"),
                 })
        {
            svc.UpsertContactAsync(new ContactModel { DisplayName = name, EmailAddress = email })
               .GetAwaiter().GetResult();
        }

        var workId = svc.CreateGroupAsync("Work").GetAwaiter().GetResult();
        var alice  = svc.LoadAllContactsAsync().GetAwaiter().GetResult()
                        .First(c => c.EmailAddress == "alice@example.com");
        svc.AddMemberAsync(workId, alice.Id).GetAwaiter().GetResult();

        var vm = new AddressBookViewModel(svc);
        var window = new AddressBookWindow(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        vm.LoadAsync().GetAwaiter().GetResult();
        window.Show();
        window.UpdateLayout();
        Drain();
        return (window, dir);
    }

    private static void SelectGroupsTab(Window window)
    {
        var tabs = window.FindName("MainTabs") as TabControl;
        Assert.NotNull(tabs);
        tabs!.SelectedIndex = 1;
        window.UpdateLayout();
        Drain();
    }

    /// <summary>
    /// The names a screen reader speaks for the rows of the named list: the live item
    /// automation peers, not the template's rendered text.
    /// </summary>
    private static List<string> ItemPeerNames(Window window, string listName)
    {
        var list = window.FindName(listName) as ListView;
        Assert.NotNull(list);
        list!.UpdateLayout();
        Drain();

        // ListView's item peer type varies with GridView, so read every child peer's name
        // rather than filtering on a concrete peer type.
        return (UIElementAutomationPeer.CreatePeerForElement(list).GetChildren() ?? [])
            .Select(p => p.GetName())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    /// <summary>The failure mode of #644: the row announced as its CLR type name.</summary>
    private static void AssertNoTypeNames(List<string> names)
    {
        Assert.NotEmpty(names);
        foreach (var n in names)
            Assert.DoesNotContain("QuickMail.", n, StringComparison.Ordinal);
    }

    private static void Cleanup(Window window, string dir)
    {
        window.Close();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    // Pumps the STA dispatcher until all queued work down to SystemIdle priority (layout,
    // container generation, peer realization) has run.
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
