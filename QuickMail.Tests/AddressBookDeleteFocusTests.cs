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
            vm.ConfirmRequested += (_, _) => Task.FromResult(true);
            FocusRow(list!, before[0]);

            Pump(((IAsyncRelayCommand)vm.DeleteGroupCommand).ExecuteAsync(null));
            Drain();

            var focused = Keyboard.FocusedElement as ListViewItem;
            Assert.NotNull(focused);
            Assert.Same(before[1], focused!.DataContext);
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

            Assert.Equal(2, members.Items.Count);
            var focused = Keyboard.FocusedElement as ListViewItem;
            Assert.NotNull(focused);
            // The members list is rebuilt, so compare by contact id rather than by reference.
            Assert.Equal(before[2].Id, (focused!.DataContext as ContactModel)?.Id);
            Assert.True(members.IsKeyboardFocusWithin);
        }
        finally { Cleanup(window, dir); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (AddressBookWindow window, AddressBookViewModel vm, string dir) BuildWindow()
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
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var vm = new AddressBookViewModel(svc);
        var window = new AddressBookWindow(vm) { WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        window.Show();
        window.Activate();
        // Loaded subscribes the focus handlers, runs LoadAsync, and then focuses the search box.
        // Wait for that last step so it cannot land in the middle of a test.
        var search = window.FindName("SearchBox") as TextBox;
        Assert.NotNull(search);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((vm.FilteredContacts.Count < 4 || !search!.IsKeyboardFocused) && DateTime.UtcNow < deadline)
        {
            Drain();
            Thread.Sleep(10);
        }
        Assert.Equal(4, vm.FilteredContacts.Count);
        window.UpdateLayout();
        Drain();
        return (window, vm, dir);
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

    private static void Cleanup(Window window, string dir)
    {
        window.Close();
        SynchronizationContext.SetSynchronizationContext(null);
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
