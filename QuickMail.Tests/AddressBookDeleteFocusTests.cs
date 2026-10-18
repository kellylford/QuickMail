// Issue #265: deleting a contact in the address book left keyboard focus nowhere, so the next
// arrow key started again from the top of the list. The delete cleared the selection and never
// moved focus. It now selects the neighbouring row and focuses it BEFORE the deleted rows are
// removed — the ordering the message list uses (#667), so the focused row is never the one going.
//
// The ViewModel tests pin down which row is landed on and when focus is asked for (state captured
// at the moment of the request, since every end-state assertion would pass either way). The
// window tests put a real AddressBookWindow on screen and check where keyboard focus actually is.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

public class AddressBookDeleteFocusTests
{
    // ── FindLandingRow ───────────────────────────────────────────────────────

    private sealed class Row(string name)
    {
        public string Name { get; } = name;
        public override string ToString() => Name;
    }

    private static List<Row> Rows(params string[] names) => names.Select(n => new Row(n)).ToList();

    [Fact]
    public void FindLandingRow_PicksTheRowThatFollowed()
    {
        var rows = Rows("a", "b", "c", "d");
        Assert.Same(rows[2], AddressBookViewModel.FindLandingRow(rows, [rows[1]]));
    }

    [Fact]
    public void FindLandingRow_LastRowDeleted_PicksThePreviousRow()
    {
        var rows = Rows("a", "b", "c");
        Assert.Same(rows[1], AddressBookViewModel.FindLandingRow(rows, [rows[2]]));
    }

    [Fact]
    public void FindLandingRow_ScatteredSelection_PicksTheFirstSurvivorAfterTheFirstDeletedRow()
    {
        var rows = Rows("a", "b", "c", "d", "e");
        // b and d leave: the survivor after b is c.
        Assert.Same(rows[2], AddressBookViewModel.FindLandingRow(rows, [rows[3], rows[1]]));
    }

    [Fact]
    public void FindLandingRow_TrailingBlockDeleted_PicksTheLastSurvivorBeforeIt()
    {
        var rows = Rows("a", "b", "c", "d");
        Assert.Same(rows[1], AddressBookViewModel.FindLandingRow(rows, [rows[2], rows[3]]));
    }

    [Fact]
    public void FindLandingRow_EverythingDeleted_IsNull()
    {
        var rows = Rows("a", "b");
        Assert.Null(AddressBookViewModel.FindLandingRow(rows, [rows[0], rows[1]]));
    }

    [Fact]
    public void FindLandingRow_NothingDeletedIsInTheList_IsNull()
    {
        var rows = Rows("a", "b");
        Assert.Null(AddressBookViewModel.FindLandingRow(rows, [new Row("elsewhere")]));
    }

    // ── Contact delete ───────────────────────────────────────────────────────

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-AddrBookDeleteFocus-{Guid.NewGuid():N}");

    private static int _nextId;

    private static async Task<(AddressBookViewModel vm, ContactService svc, string dir)> MakeViewModel(
        params string[] names)
    {
        var dir = TempDir();
        var svc = new ContactService(new ProfileContext(dir));
        foreach (var name in names)
        {
            var id = Interlocked.Increment(ref _nextId);
            await svc.UpsertContactAsync(new ContactModel
            {
                DisplayName  = name,
                EmailAddress = $"{name.ToLowerInvariant()}{id}@local.test",
            });
        }
        var vm = new AddressBookViewModel(svc);
        await vm.LoadAsync();
        return (vm, svc, dir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>What the list looked like at the moment focus was asked for.</summary>
    private sealed record FocusRequest(ContactModel? Selected, List<ContactModel> Visible);

    [Fact]
    public async Task DeleteOne_SelectsTheNextContact_AndAsksForFocusBeforeItIsRemoved()
    {
        var (vm, _, dir) = await MakeViewModel("Alice", "Bob", "Carol", "Dave");
        try
        {
            var before   = vm.FilteredContacts.ToList();
            var doomed   = before[1];
            var expected = before[2];
            vm.SelectedContact = doomed;

            var requests = new List<FocusRequest>();
            var queued = 0;
            vm.ContactListFocusNowRequested += () =>
            {
                requests.Add(new FocusRequest(vm.SelectedContact, vm.FilteredContacts.ToList()));
                return true;
            };
            vm.ContactListFocusRequested += () => queued++;

            await vm.DeleteMultipleAsync([doomed]);

            var request = Assert.Single(requests);
            Assert.Same(expected, request.Selected);
            // The ordering is the fix: the deleted row is still in the list when focus moves.
            Assert.Contains(doomed, request.Visible);
            // Focus landed, so no second (queued) focus move follows the removal.
            Assert.Equal(0, queued);

            Assert.Same(expected, vm.SelectedContact);
            Assert.DoesNotContain(doomed, vm.FilteredContacts);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteConfirmed_LastContact_SelectsThePreviousOne()
    {
        var (vm, _, dir) = await MakeViewModel("Alice", "Bob", "Carol");
        try
        {
            var before = vm.FilteredContacts.ToList();
            vm.SelectedContact = before[^1];

            await vm.DeleteConfirmedAsync();

            Assert.Same(before[^2], vm.SelectedContact);
            Assert.Equal(2, vm.FilteredContacts.Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteSeveral_SelectsTheContactAfterTheFirstDeletedOne()
    {
        var (vm, _, dir) = await MakeViewModel("Alice", "Bob", "Carol", "Dave", "Erin");
        try
        {
            var before = vm.FilteredContacts.ToList();
            vm.SelectedContact = before[1];

            await vm.DeleteMultipleAsync([before[1], before[2], before[4]]);

            Assert.Same(before[3], vm.SelectedContact);
            Assert.Equal([before[0], before[3]], vm.FilteredContacts.ToList());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteEveryContact_LeavesNoSelection_AndQueuesFocusOntoTheList()
    {
        var (vm, _, dir) = await MakeViewModel("Alice", "Bob");
        try
        {
            var all = vm.FilteredContacts.ToList();
            vm.SelectedContact = all[0];
            var now = 0;
            var queued = 0;
            vm.ContactListFocusNowRequested += () => { now++; return true; };
            vm.ContactListFocusRequested += () => queued++;

            await vm.DeleteMultipleAsync(all);

            Assert.Null(vm.SelectedContact);
            Assert.Empty(vm.FilteredContacts);
            Assert.Equal(0, now);      // nowhere to land beforehand
            Assert.Equal(1, queued);   // so the list itself takes focus afterwards
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task FocusDidNotLand_TheQueuedFallbackStillRuns()
    {
        var (vm, _, dir) = await MakeViewModel("Alice", "Bob", "Carol");
        try
        {
            var before = vm.FilteredContacts.ToList();
            vm.SelectedContact = before[0];
            var queued = 0;
            vm.ContactListFocusNowRequested += () => false;
            vm.ContactListFocusRequested += () => queued++;

            await vm.DeleteMultipleAsync([before[0]]);

            Assert.Same(before[1], vm.SelectedContact);
            Assert.Equal(1, queued);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SearchFilterActive_LandsOnTheNextVisibleContact()
    {
        // With "Kim" typed, Lee Cole is filtered out of view; the landing row must come from what
        // is visible, never from a contact the user cannot see.
        var (vm, _, dir) = await MakeViewModel("Kim Ash", "Lee Cole", "Kim Bell", "Kim Dale");
        try
        {
            vm.SearchText = "Kim";
            var shown = vm.FilteredContacts.ToList();
            Assert.Equal(3, shown.Count);
            Assert.DoesNotContain(shown, c => c.DisplayName == "Lee Cole");

            vm.SelectedContact = shown[0];
            await vm.DeleteMultipleAsync([shown[0]]);

            Assert.Same(shown[1], vm.SelectedContact);
            Assert.Equal([shown[1], shown[2]], vm.FilteredContacts.ToList());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeletingAnUnselectedContact_KeepsTheCurrentSelection()
    {
        var (vm, _, dir) = await MakeViewModel("Alice", "Bob", "Carol");
        try
        {
            var before = vm.FilteredContacts.ToList();
            vm.SelectedContact = before[0];

            await vm.DeleteMultipleAsync([before[2]]);

            Assert.Same(before[0], vm.SelectedContact);
        }
        finally { Cleanup(dir); }
    }

    // ── Group delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteGroup_SelectsTheNextGroup_AndAsksForFocusBeforeItIsRemoved()
    {
        var (vm, svc, dir) = await MakeViewModel();
        try
        {
            await svc.CreateGroupAsync("One");
            await svc.CreateGroupAsync("Two");
            await svc.CreateGroupAsync("Three");
            await vm.LoadAsync();
            vm.ConfirmRequested += (_, _) => Task.FromResult(true);

            var before = vm.Groups.ToList();
            var doomed = before[1];
            vm.SelectedGroup = doomed;

            GroupModel? selectedAtRequest = null;
            var stillListedAtRequest = false;
            var queued = 0;
            vm.GroupsListFocusNowRequested += () =>
            {
                selectedAtRequest = vm.SelectedGroup;
                stillListedAtRequest = vm.Groups.Contains(doomed);
                return true;
            };
            vm.GroupsListFocusRequested += () => queued++;

            await ((IAsyncRelayCommand)vm.DeleteGroupCommand).ExecuteAsync(null);

            Assert.Same(before[2], selectedAtRequest);
            Assert.True(stillListedAtRequest);
            Assert.Equal(0, queued);
            Assert.Same(before[2], vm.SelectedGroup);
            Assert.Equal([before[0], before[2]], vm.Groups.ToList());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteGroup_FocusLanded_SaysNothing_NothingToLand_SaysDeleted()
    {
        // Once focus has landed on the next group, its reading is the confirmation; an
        // announcement would cut it off (#667). With nowhere to land, the announcement is all
        // the user gets.
        var (vm, svc, dir) = await MakeViewModel();
        try
        {
            await svc.CreateGroupAsync("One");
            await svc.CreateGroupAsync("Two");
            await vm.LoadAsync();
            vm.ConfirmRequested += (_, _) => Task.FromResult(true);
            vm.GroupsListFocusNowRequested += () => true;
            var spoken = new List<string>();
            vm.AnnouncementRequested += (text, _) => spoken.Add(text);

            vm.SelectedGroup = vm.Groups[0];
            await ((IAsyncRelayCommand)vm.DeleteGroupCommand).ExecuteAsync(null);
            Assert.Empty(spoken);

            await ((IAsyncRelayCommand)vm.DeleteGroupCommand).ExecuteAsync(null);
            Assert.Single(spoken);
            Assert.Contains("deleted", spoken[0], StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RemoveMember_LandsOnTheNextMember_BeforeRemovingOnlyThatRow()
    {
        var (vm, svc, dir) = await MakeViewModel("Alice", "Bob", "Carol");
        try
        {
            var groupId = await svc.CreateGroupAsync("Team");
            foreach (var c in vm.FilteredContacts)
                await svc.AddMemberAsync(groupId, c.Id);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups.First(g => g.Id == groupId);

            var before = vm.SelectedGroupMembers.ToList();
            Assert.Equal(3, before.Count);
            ContactModel? landedOn = null;
            var stillListed = false;
            var queued = 0;
            vm.GroupMemberFocusNowRequested += landing =>
            {
                landedOn = landing;
                stillListed = vm.SelectedGroupMembers.Contains(before[1]);
                return true;
            };
            vm.GroupMembersListFocusRequested += () => queued++;
            var resets = 0;
            vm.SelectedGroupMembers.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
            };
            var spoken = new List<string>();
            vm.AnnouncementRequested += (text, _) => spoken.Add(text);

            await vm.RemoveContactFromGroupAsync(before[1]);

            Assert.Same(before[2], landedOn);
            Assert.True(stillListed);
            Assert.Equal(0, queued);
            Assert.Equal(0, resets);   // the list was not cleared and rebuilt
            Assert.Equal([before[0], before[2]], vm.SelectedGroupMembers.ToList());
            Assert.Equal(2, vm.SelectedGroup!.ResolvedMemberCount);
            Assert.Empty(spoken);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RemoveLastMember_QueuesFocus_AndSaysRemoved()
    {
        var (vm, svc, dir) = await MakeViewModel("Alice");
        try
        {
            var groupId = await svc.CreateGroupAsync("Solo");
            await svc.AddMemberAsync(groupId, vm.FilteredContacts[0].Id);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups.First(g => g.Id == groupId);
            var queued = 0;
            vm.GroupMembersListFocusRequested += () => queued++;
            var spoken = new List<string>();
            vm.AnnouncementRequested += (text, _) => spoken.Add(text);

            await vm.RemoveContactFromGroupAsync(vm.SelectedGroupMembers[0]);

            Assert.Empty(vm.SelectedGroupMembers);
            Assert.Equal(1, queued);
            Assert.Single(spoken);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteOnlyGroup_LeavesNoSelection_AndQueuesFocus()
    {
        var (vm, svc, dir) = await MakeViewModel();
        try
        {
            await svc.CreateGroupAsync("Only");
            await vm.LoadAsync();
            vm.ConfirmRequested += (_, _) => Task.FromResult(true);
            vm.SelectedGroup = vm.Groups[0];
            var queued = 0;
            vm.GroupsListFocusRequested += () => queued++;

            await ((IAsyncRelayCommand)vm.DeleteGroupCommand).ExecuteAsync(null);

            Assert.Empty(vm.Groups);
            Assert.Null(vm.SelectedGroup);
            Assert.Equal(1, queued);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteGroup_Declined_ChangesNothing()
    {
        var (vm, svc, dir) = await MakeViewModel();
        try
        {
            await svc.CreateGroupAsync("One");
            await svc.CreateGroupAsync("Two");
            await vm.LoadAsync();
            vm.ConfirmRequested += (_, _) => Task.FromResult(false);
            var first = vm.Groups[0];
            vm.SelectedGroup = first;
            var asked = 0;
            vm.GroupsListFocusNowRequested += () => { asked++; return true; };
            vm.GroupsListFocusRequested += () => asked++;

            await ((IAsyncRelayCommand)vm.DeleteGroupCommand).ExecuteAsync(null);

            Assert.Equal(2, vm.Groups.Count);
            Assert.Same(first, vm.SelectedGroup);
            Assert.Equal(0, asked);
        }
        finally { Cleanup(dir); }
    }
}

/// <summary>
/// Where keyboard focus really is after a delete, in a shown and activated AddressBookWindow.
/// </summary>
[Collection("WpfTests")]
public class AddressBookDeleteFocusWindowTests
{
    [StaFact]
    public void DeleteFocusedContact_FocusMovesToTheNextRow_BeforeTheRowIsRemoved()
    {
        var (window, vm, dir) = BuildWindow();
        try
        {
            var list = window.FindName("ContactList") as ListView;
            Assert.NotNull(list);
            var before = vm.FilteredContacts.ToList();
            var doomed = before[1];
            var expected = before[2];
            FocusRow(list!, doomed);

            // Record where focus is at the instant the doomed row leaves the collection.
            object? focusedAtRemoval = null;
            void OnChanged(object? s, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action == NotifyCollectionChangedAction.Remove)
                    focusedAtRemoval = (Keyboard.FocusedElement as ListViewItem)?.DataContext;
            }
            vm.FilteredContacts.CollectionChanged += OnChanged;
            Pump(vm.DeleteMultipleAsync([doomed]));
            vm.FilteredContacts.CollectionChanged -= OnChanged;
            Drain();

            Assert.Same(expected, focusedAtRemoval);
            var focused = Keyboard.FocusedElement as ListViewItem;
            Assert.NotNull(focused);
            Assert.Same(expected, focused!.DataContext);
            Assert.Same(expected, list!.SelectedItem);
            Assert.Single(list.SelectedItems);
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void DeleteLastContact_FocusMovesToThePreviousRow()
    {
        var (window, vm, dir) = BuildWindow();
        try
        {
            var list = window.FindName("ContactList") as ListView;
            Assert.NotNull(list);
            var before = vm.FilteredContacts.ToList();
            FocusRow(list!, before[^1]);

            Pump(vm.DeleteMultipleAsync([before[^1]]));
            Drain();

            var focused = Keyboard.FocusedElement as ListViewItem;
            Assert.NotNull(focused);
            Assert.Same(before[^2], focused!.DataContext);
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void DeleteSeveralContacts_FocusMovesToTheRowAfterTheFirstDeleted()
    {
        var (window, vm, dir) = BuildWindow();
        try
        {
            var list = window.FindName("ContactList") as ListView;
            Assert.NotNull(list);
            var before = vm.FilteredContacts.ToList();
            FocusRow(list!, before[0]);
            list!.SelectedItems.Add(before[1]);

            Pump(vm.DeleteMultipleAsync([before[0], before[1]]));
            Drain();

            var focused = Keyboard.FocusedElement as ListViewItem;
            Assert.NotNull(focused);
            Assert.Same(before[2], focused!.DataContext);
            Assert.Single(list.SelectedItems);
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void DeleteEveryContact_FocusStaysInTheList()
    {
        var (window, vm, dir) = BuildWindow();
        try
        {
            var list = window.FindName("ContactList") as ListView;
            Assert.NotNull(list);
            var all = vm.FilteredContacts.ToList();
            FocusRow(list!, all[0]);

            Pump(vm.DeleteMultipleAsync(all));
            Drain();

            Assert.Empty(list!.Items);
            Assert.True(list.IsKeyboardFocusWithin,
                $"Focus should stay in the empty contact list; it is on {Keyboard.FocusedElement}.");
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void DeleteFocusedGroup_FocusMovesToTheNextGroup()
    {
        var (window, vm, dir) = BuildWindow();
        try
        {
            var tabs = window.FindName("MainTabs") as TabControl;
            Assert.NotNull(tabs);
            tabs!.SelectedIndex = 1;
            window.UpdateLayout();
            Drain();

            var list = window.FindName("GroupsList") as ListView;
            Assert.NotNull(list);
            var before = vm.Groups.ToList();
            FocusRow(list!, before[0]);

            // The confirmed half of Delete Group: the command would raise ConfirmRequested, which
            // this window answers with a real MessageBox that nothing here could dismiss.
            Pump(vm.DeleteGroupConfirmedAsync(before[0]));
            Drain();

            var focused = Keyboard.FocusedElement as ListViewItem;
            Assert.NotNull(focused);
            Assert.Same(before[1], focused!.DataContext);
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void DeleteTheLastGroupLeft_FocusMovesToNewGroup()
    {
        var (window, vm, dir) = BuildWindow();
        try
        {
            var tabs = window.FindName("MainTabs") as TabControl;
            Assert.NotNull(tabs);
            tabs!.SelectedIndex = 1;
            window.UpdateLayout();
            Drain();

            var list = window.FindName("GroupsList") as ListView;
            var newGroup = window.FindName("NewGroupButton") as Button;
            Assert.NotNull(list);
            Assert.NotNull(newGroup);

            foreach (var _ in vm.Groups.ToList())
            {
                var group = vm.Groups[0];
                FocusRow(list!, group);
                Pump(vm.DeleteGroupConfirmedAsync(group));
            }

            Assert.Empty(vm.Groups);
            Assert.True(newGroup!.IsKeyboardFocused,
                $"With no groups left, focus belongs on New Group; it is on {Keyboard.FocusedElement}.");
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void RemoveGroupMember_FocusMovesToTheNextMember()
    {
        var (window, vm, dir) = BuildWindow();
        try
        {
            var tabs = window.FindName("MainTabs") as TabControl;
            Assert.NotNull(tabs);
            tabs!.SelectedIndex = 1;
            window.UpdateLayout();
            Drain();
            vm.SelectedGroup = vm.Groups.First(g => g.Name == "Work");
            window.UpdateLayout();
            Drain();

            var members = window.FindName("GroupMembersList") as ListView;
            Assert.NotNull(members);
            var before = members!.Items.Cast<ContactModel>().ToList();
            Assert.Equal(3, before.Count);
            FocusRow(members, before[1]);

            // Where focus is at the instant the removed member leaves the collection.
            object? focusedAtRemoval = null;
            var resets = 0;
            void OnChanged(object? s, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action == NotifyCollectionChangedAction.Remove)
                    focusedAtRemoval = (Keyboard.FocusedElement as ListViewItem)?.DataContext;
                if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
            }
            vm.SelectedGroupMembers.CollectionChanged += OnChanged;

            // Delete as the key handler receives it. Not synthesized OS input, so not gated.
            var row = (ListViewItem)Keyboard.FocusedElement;
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(row)!, 0, Key.Delete)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            row.RaiseEvent(key);
            Assert.True(key.Handled);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!(Keyboard.FocusedElement is ListViewItem { DataContext: ContactModel c } && c.Id == before[2].Id)
                   && DateTime.UtcNow < deadline)
            {
                Drain();
                Thread.Sleep(5);
            }

            vm.SelectedGroupMembers.CollectionChanged -= OnChanged;

            Assert.Equal(2, members.Items.Count);
            Assert.Same(before[2], focusedAtRemoval);
            // Only the one row went: the list was not cleared and rebuilt under the focused row.
            Assert.Equal(0, resets);
            var focused = Keyboard.FocusedElement as ListViewItem;
            Assert.NotNull(focused);
            Assert.Same(before[2], focused!.DataContext);
            Assert.True(members.IsKeyboardFocusWithin);
        }
        finally { Cleanup(window, dir); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // xUnit builds a fresh instance per test, so this belongs to the one test running.
    private SynchronizationContext? _previousContext;

    private (AddressBookWindow window, AddressBookViewModel vm, string dir) BuildWindow()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

        var dir = Path.Combine(Path.GetTempPath(), $"QM-AddressBookDeleteFocus-{Guid.NewGuid():N}");
        var svc = new ContactService(new ProfileContext(dir));
        foreach (var (name, email) in new[]
                 {
                     ("Alice Adams", "alice@example.com"),
                     ("Bob Baker",   "bob@example.com"),
                     ("Carol Cole",  "carol@example.com"),
                     ("Dave Dunn",   "dave@example.com"),
                 })
        {
            svc.UpsertContactAsync(new ContactModel { DisplayName = name, EmailAddress = email })
               .GetAwaiter().GetResult();
        }
        var workId = svc.CreateGroupAsync("Work").GetAwaiter().GetResult();
        foreach (var c in svc.LoadAllContactsAsync().GetAwaiter().GetResult()
                             .Where(c => c.DisplayName != "Dave Dunn"))
            svc.AddMemberAsync(workId, c.Id).GetAwaiter().GetResult();
        svc.CreateGroupAsync("Holiday").GetAwaiter().GetResult();

        // The app runs the ViewModel on the UI thread, where awaits resume on the dispatcher; the
        // focus handlers touch WPF objects, so the test has to reproduce that.
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var vm = new AddressBookViewModel(svc);
        var window = new AddressBookWindow(vm) { WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.Activate();
            // Loaded subscribes the focus handlers, runs LoadAsync, and then focuses the search
            // box. Wait for that last step so it cannot land in the middle of a test.
            var search = window.FindName("SearchBox") as TextBox;
            Assert.NotNull(search);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((vm.FilteredContacts.Count < 4 || !search!.IsKeyboardFocused) && DateTime.UtcNow < deadline)
            {
                Drain();
                Thread.Sleep(10);
            }
            Assert.Equal(4, vm.FilteredContacts.Count);
            Assert.True(window.IsActive && search!.IsKeyboardFocused,
                "The address book window never became active, so no keyboard-focus assertion " +
                "in this class can mean anything.");
            window.UpdateLayout();
            Drain();
            return (window, vm, dir);
        }
        catch
        {
            Cleanup(window, dir);
            throw;
        }
    }

    private static void FocusRow(ListView list, object item)
    {
        list.SelectedItem = item;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        var row = list.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem;
        Assert.NotNull(row);
        Assert.True(row!.Focus(), "Could not put keyboard focus on the row under test.");
        Drain();
    }

    /// <summary>Runs the dispatcher until <paramref name="task"/> finishes, then rethrows its failure.</summary>
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Drain();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "The delete did not finish.");
        task.GetAwaiter().GetResult();
        Drain();
    }

    private void Cleanup(Window window, string dir)
    {
        window.Close();
        SynchronizationContext.SetSynchronizationContext(_previousContext);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
