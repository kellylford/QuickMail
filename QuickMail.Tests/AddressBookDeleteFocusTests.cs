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
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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

    // ── Synced contacts are read-only (issue #827) ───────────────────────────
    // The Delete key used to delete whatever was selected, synced contacts included, while the
    // Delete button refused them. Both now run DeleteSelectedContactsAsync, so these tests are
    // the rules for both.

    /// <summary>A ViewModel over local contacts and contacts synced from a Microsoft account.</summary>
    private static async Task<(AddressBookViewModel vm, ContactService svc, string dir)> MakeMixedViewModel(
        string[] local, string[] synced)
    {
        var (_, svc, dir) = await MakeViewModel(local);
        var account = Guid.NewGuid();
        await svc.ReplaceSyncedContactsAsync(account, ContactSource.Microsoft,
            synced.Select((name, i) => new ContactModel
            {
                SourceId     = $"m{i}",
                DisplayName  = name,
                EmailAddress = $"{name.ToLowerInvariant().Replace(' ', '.')}@synced.test",
            }).ToList());
        var vm = new AddressBookViewModel(svc);
        await vm.LoadAsync();
        return (vm, svc, dir);
    }

    private static ContactModel Named(AddressBookViewModel vm, string name) =>
        vm.FilteredContacts.Single(c => c.DisplayName == name);

    /// <summary>Selects <paramref name="contacts"/> the way the list does: the first is SelectedItem.</summary>
    private static void Select(AddressBookViewModel vm, params ContactModel[] contacts)
    {
        vm.SelectedContact = contacts[0];
        vm.SetSelectedContacts(contacts);
    }

    private sealed class DeleteProbe
    {
        public readonly List<string> Prompts = [];
        public readonly List<(string Text, AnnouncementCategory Category)> Spoken = [];
        public int FocusRequests;

        public DeleteProbe(AddressBookViewModel vm, bool confirm = true, bool focusLands = true)
        {
            vm.ConfirmRequested += (_, body) => { Prompts.Add(body); return Task.FromResult(confirm); };
            vm.AnnouncementRequested += (text, category) => Spoken.Add((text, category));
            vm.ContactListFocusNowRequested += () => { FocusRequests++; return focusLands; };
            vm.ContactListFocusRequested += () => FocusRequests++;
        }
    }

    [Fact]
    public async Task SyncedContact_IsRefused_WithNoConfirmation_AndNothingMoves()
    {
        var (vm, svc, dir) = await MakeMixedViewModel(["Alice", "Carol"], ["Bob Server"]);
        try
        {
            var synced = Named(vm, "Bob Server");
            Select(vm, synced);
            Assert.False(vm.CanDeleteContact);   // the button is unavailable…
            var probe = new DeleteProbe(vm);

            await vm.DeleteSelectedContactsAsync();   // …and the key is refused

            Assert.Empty(probe.Prompts);
            Assert.Contains(synced, vm.FilteredContacts);
            Assert.Contains(await svc.LoadAllContactsAsync(), c => c.Id == synced.Id);
            Assert.Same(synced, vm.SelectedContact);
            Assert.Equal(0, probe.FocusRequests);   // focus stays on the row it was on
            var (text, category) = Assert.Single(probe.Spoken);
            Assert.Equal(AnnouncementCategory.Result, category);
            Assert.Contains("Bob Server", text, StringComparison.Ordinal);
            Assert.Contains("read-only", text, StringComparison.Ordinal);
            Assert.Equal(text, vm.ContactError);   // the same words are on screen
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SeveralSyncedContacts_AreRefusedTogether()
    {
        var (vm, svc, dir) = await MakeMixedViewModel(["Alice"], ["Bob Server", "Dan Server"]);
        try
        {
            Select(vm, Named(vm, "Bob Server"), Named(vm, "Dan Server"));
            Assert.False(vm.CanDeleteContact);
            var probe = new DeleteProbe(vm);

            await vm.DeleteSelectedContactsAsync();

            Assert.Empty(probe.Prompts);
            Assert.Equal(3, vm.FilteredContacts.Count);
            Assert.Equal(3, (await svc.LoadAllContactsAsync()).Count);
            Assert.Contains("All 2 selected contacts are synced", Assert.Single(probe.Spoken).Text,
                StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LocalContact_IsConfirmedAndDeleted_WithNoReadOnlyMessage()
    {
        var (vm, svc, dir) = await MakeMixedViewModel(["Alice", "Carol"], ["Bob Server"]);
        try
        {
            var alice = Named(vm, "Alice");
            Select(vm, alice);
            Assert.True(vm.CanDeleteContact);
            var probe = new DeleteProbe(vm);

            await vm.DeleteSelectedContactsAsync();

            Assert.Equal($"Delete {alice.Display}?", Assert.Single(probe.Prompts));
            Assert.DoesNotContain(alice, vm.FilteredContacts);
            Assert.DoesNotContain(await svc.LoadAllContactsAsync(), c => c.Id == alice.Id);
            // #265 is untouched: focus landed on the neighbour, and that is the only feedback.
            Assert.Equal(1, probe.FocusRequests);
            Assert.NotNull(vm.SelectedContact);
            Assert.Empty(probe.Spoken);
            Assert.Equal(string.Empty, vm.ContactError);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task MixedSelection_DeletesOnlyTheLocalContacts_AndSaysTheSyncedOnesStayed()
    {
        var (vm, svc, dir) = await MakeMixedViewModel(["Alice", "Carol", "Erin"], ["Bob Server", "Dan Server"]);
        try
        {
            var alice  = Named(vm, "Alice");
            var bob    = Named(vm, "Bob Server");
            var carol  = Named(vm, "Carol");
            var dan    = Named(vm, "Dan Server");
            var erin   = Named(vm, "Erin");
            // A synced contact first, as when the selection was started on one.
            Select(vm, bob, alice, carol, dan);
            Assert.True(vm.CanDeleteContact);   // the button is available: something can go
            var probe = new DeleteProbe(vm);

            await vm.DeleteSelectedContactsAsync();

            var prompt = Assert.Single(probe.Prompts);
            Assert.StartsWith("Delete 2 contacts?", prompt, StringComparison.Ordinal);
            Assert.Contains("2 selected contacts are synced", prompt, StringComparison.Ordinal);

            Assert.Equal([bob, dan, erin], vm.FilteredContacts.OrderBy(c => c.DisplayName).ToList());
            var stored = await svc.LoadAllContactsAsync();
            Assert.Contains(stored, c => c.Id == bob.Id);
            Assert.Contains(stored, c => c.Id == dan.Id);
            Assert.DoesNotContain(stored, c => c.Id == alice.Id || c.Id == carol.Id);

            // Shown, but not spoken: focus landed on the next row, and that row is being read. An
            // announcement now would cut it off (#265, #667); the confirmation already said it.
            Assert.Equal("2 synced contacts were not deleted. Synced contacts are read-only.", vm.ContactError);
            Assert.Empty(probe.Spoken);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task MixedSelection_FocusDidNotLand_TheSyncedCountIsSpoken()
    {
        var (vm, _, dir) = await MakeMixedViewModel(["Alice", "Carol"], ["Bob Server"]);
        try
        {
            Select(vm, Named(vm, "Alice"), Named(vm, "Bob Server"));
            var probe = new DeleteProbe(vm, focusLands: false);

            await vm.DeleteSelectedContactsAsync();

            var (text, category) = Assert.Single(probe.Spoken);
            Assert.Equal(AnnouncementCategory.Result, category);
            Assert.Equal("1 synced contact was not deleted. Synced contacts are read-only.", text);
            Assert.Equal(text, vm.ContactError);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task ASecondDeleteWhileOneIsRunning_IsIgnored()
    {
        var (vm, svc, dir) = await MakeMixedViewModel(["Alice", "Carol", "Dave"], []);
        try
        {
            Select(vm, Named(vm, "Alice"));
            var prompts = 0;
            var answer = new TaskCompletionSource<bool>();
            vm.ConfirmRequested += (_, _) => { prompts++; return answer.Task; };

            var first = vm.DeleteSelectedContactsAsync();     // waiting on its confirmation
            Assert.True(vm.CanDeleteContact);                  // held while the delete runs
            await vm.DeleteSelectedContactsAsync();            // the second press
            Assert.Equal(1, prompts);

            answer.SetResult(true);
            await first;
            Assert.Equal(2, (await svc.LoadAllContactsAsync()).Count);

            // Finished, so the next press is honoured again.
            await vm.DeleteSelectedContactsAsync();
            Assert.Equal(2, prompts);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task MixedSelection_OneSyncedContact_IsNamedInTheSingular()
    {
        var (vm, _, dir) = await MakeMixedViewModel(["Alice", "Carol"], ["Bob Server"]);
        try
        {
            var alice = Named(vm, "Alice");
            Select(vm, alice, Named(vm, "Bob Server"));
            var probe = new DeleteProbe(vm);

            await vm.DeleteSelectedContactsAsync();

            Assert.Equal(
                $"Delete {alice.Display}? 1 selected contact is synced from an account and read-only, so it will be kept.",
                Assert.Single(probe.Prompts));
            Assert.Equal("1 synced contact was not deleted. Synced contacts are read-only.", vm.ContactError);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task MixedSelection_Declined_DeletesNothing_AndSaysNothing()
    {
        var (vm, svc, dir) = await MakeMixedViewModel(["Alice", "Carol"], ["Bob Server"]);
        try
        {
            var alice = Named(vm, "Alice");
            Select(vm, alice, Named(vm, "Bob Server"));
            var probe = new DeleteProbe(vm, confirm: false);

            await vm.DeleteSelectedContactsAsync();

            Assert.Single(probe.Prompts);
            Assert.Equal(3, vm.FilteredContacts.Count);
            Assert.Equal(3, (await svc.LoadAllContactsAsync()).Count);
            Assert.Same(alice, vm.SelectedContact);
            Assert.Equal(0, probe.FocusRequests);
            Assert.Empty(probe.Spoken);
            Assert.Equal(string.Empty, vm.ContactError);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task CanDeleteContact_FollowsTheWholeSelection()
    {
        var (vm, _, dir) = await MakeMixedViewModel(["Alice"], ["Bob Server"]);
        try
        {
            var alice = Named(vm, "Alice");
            var bob   = Named(vm, "Bob Server");

            Select(vm, bob);
            Assert.False(vm.CanDeleteContact);
            Select(vm, bob, alice);
            Assert.True(vm.CanDeleteContact);
            Select(vm, alice);
            Assert.True(vm.CanDeleteContact);

            vm.BeginEditContactCommand.Execute(null);
            Assert.False(vm.CanDeleteContact);   // as before: not while a contact is being edited
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task WhileEditing_DeleteDoesNothing()
    {
        var (vm, svc, dir) = await MakeMixedViewModel(["Alice", "Carol"], []);
        try
        {
            Select(vm, Named(vm, "Alice"));
            vm.BeginEditContactCommand.Execute(null);
            var probe = new DeleteProbe(vm);

            await vm.DeleteSelectedContactsAsync();

            Assert.Empty(probe.Prompts);
            Assert.Equal(2, (await svc.LoadAllContactsAsync()).Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SelectedContactSetOutsideTheList_IsWhatDeleteActsOn()
    {
        // A selection pushed earlier that no longer holds SelectedContact is stale; Delete must act
        // on the contact the user is on, never on rows they have moved away from.
        var (vm, _, dir) = await MakeMixedViewModel(["Alice", "Carol"], ["Bob Server"]);
        try
        {
            vm.SetSelectedContacts([Named(vm, "Alice"), Named(vm, "Carol")]);
            vm.SelectedContact = Named(vm, "Bob Server");
            Assert.False(vm.CanDeleteContact);
            var probe = new DeleteProbe(vm);

            await vm.DeleteSelectedContactsAsync();

            Assert.Empty(probe.Prompts);
            Assert.Equal(3, vm.FilteredContacts.Count);
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

    // ── Synced contacts (issue #827) ─────────────────────────────────────────

    [StaFact]
    public void DeleteKeyOnASyncedContact_IsRefused_AndFocusStaysOnItsRow()
    {
        var (window, vm, dir) = BuildWindow(withSynced: true);
        try
        {
            var list = window.FindName("ContactList") as ListView;
            var error = window.FindName("ContactErrorText") as TextBlock;
            var button = window.FindName("DeleteContactButton") as Button;
            Assert.NotNull(list);
            Assert.NotNull(error);
            Assert.NotNull(button);
            var synced = vm.FilteredContacts.Single(c => !c.IsLocal);
            FocusRow(list!, synced);
            Assert.False(button!.IsEnabled);

            // Delete as the key handler receives it. Not synthesized OS input, so not gated. A
            // refusal asks for no confirmation, so no MessageBox can stall the test.
            var row = (ListViewItem)Keyboard.FocusedElement;
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(row)!, 0, Key.Delete)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            row.RaiseEvent(key);
            Assert.True(key.Handled);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (vm.ContactError.Length == 0 && DateTime.UtcNow < deadline)
            {
                Drain();
                Thread.Sleep(5);
            }

            Assert.Contains(synced, vm.FilteredContacts);
            var focused = Keyboard.FocusedElement as ListViewItem;
            Assert.NotNull(focused);
            Assert.Same(synced, focused!.DataContext);
            Assert.Same(synced, list!.SelectedItem);
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, error!.Visibility);
            Assert.Contains("read-only", error.Text, StringComparison.Ordinal);
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void DeleteButton_FollowsTheWholeSelection()
    {
        var (window, vm, dir) = BuildWindow(withSynced: true);
        try
        {
            var list = window.FindName("ContactList") as ListView;
            var button = window.FindName("DeleteContactButton") as Button;
            Assert.NotNull(list);
            Assert.NotNull(button);

            var synced = vm.FilteredContacts.Single(c => !c.IsLocal);
            var local = vm.FilteredContacts.First(c => c.IsLocal);
            FocusRow(list!, synced);
            Assert.False(button!.IsEnabled);

            list!.SelectedItems.Add(local);   // a mixed selection, started on the synced contact
            Drain();
            Assert.Same(synced, list.SelectedItem);
            Assert.True(button.IsEnabled);

            list.SelectedItems.Remove(local);
            Drain();
            Assert.False(button.IsEnabled);
        }
        finally { Cleanup(window, dir); }
    }

    [StaFact]
    public void DeleteButton_FocusGoesFromTheButtonStraightToTheLandedRow()
    {
        // The button holds focus through the delete. If it went disabled meanwhile (as an async
        // command's button does while it runs), WPF would move focus somewhere of its own first and
        // a screen reader would announce that on the way to the row.
        var (window, vm, dir) = BuildWindow(withSynced: true);
        try
        {
            var list = window.FindName("ContactList") as ListView;
            var button = window.FindName("DeleteContactButton") as Button;
            Assert.NotNull(list);
            Assert.NotNull(button);
            var doomed = vm.FilteredContacts.First(c => c.IsLocal);
            var expected = vm.FilteredContacts[vm.FilteredContacts.IndexOf(doomed) + 1];
            FocusRow(list!, doomed);
            Assert.True(button!.Focus(), "Could not put keyboard focus on the Delete button.");
            Drain();
            Assert.True(button.IsEnabled);

            var prompts = new List<string>();
            window.ConfirmForTests = body => { prompts.Add(body); return true; };
            var focusPath = new List<object>();
            KeyboardFocusChangedEventHandler onFocus = (_, e) => focusPath.Add(e.NewFocus);
            window.AddHandler(Keyboard.GotKeyboardFocusEvent, onFocus, handledEventsToo: true);

            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (vm.FilteredContacts.Contains(doomed) && DateTime.UtcNow < until)
            {
                Drain();
                Thread.Sleep(5);
            }
            Drain();
            window.RemoveHandler(Keyboard.GotKeyboardFocusEvent, onFocus);

            Assert.Single(prompts);
            Assert.DoesNotContain(doomed, vm.FilteredContacts);
            var landedRow = Assert.Single(focusPath);
            Assert.Same(expected, (landedRow as ListViewItem)?.DataContext);
            Assert.Same(expected, (Keyboard.FocusedElement as ListViewItem)?.DataContext);
        }
        finally { Cleanup(window, dir); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // xUnit builds a fresh instance per test, so this belongs to the one test running.
    private SynchronizationContext? _previousContext;

    private (AddressBookWindow window, AddressBookViewModel vm, string dir) BuildWindow(bool withSynced = false)
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
        if (withSynced)
        {
            svc.ReplaceSyncedContactsAsync(Guid.NewGuid(), ContactSource.Microsoft,
                [new ContactModel { SourceId = "m1", DisplayName = "Bert Server", EmailAddress = "bert@synced.test" }])
               .GetAwaiter().GetResult();
        }
        var expectedCount = withSynced ? 5 : 4;

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
            while ((vm.FilteredContacts.Count < expectedCount || !search!.IsKeyboardFocused) && DateTime.UtcNow < deadline)
            {
                Drain();
                Thread.Sleep(10);
            }
            Assert.Equal(expectedCount, vm.FilteredContacts.Count);
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
