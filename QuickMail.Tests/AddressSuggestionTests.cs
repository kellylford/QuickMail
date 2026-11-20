using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Address book suggestions in the appointment editor's Invite field (#869): the text-box side of
/// the controller compose's To field now shares — what counts as the address being typed, what a
/// chosen suggestion leaves behind — and the controller itself, against the real editor window.
/// </summary>
// Shows windows, so it must not run alongside the focus tests (WindowTestCollectionGuard.cs).
[Collection("WpfTests")]
public class AddressSuggestionTests
{
    private static void EnsureApplication() => WpfTestHost.EnsureApplication();

    /// <summary>Contacts and one group, searched by prefix of name or address.</summary>
    private sealed class Contacts : IContactService
    {
        public List<ContactModel> People { get; } =
        [
            new() { Id = 1, DisplayName = "Ann Lee", EmailAddress = "ann@example.com", LastUsedTicks = 2 },
            new() { Id = 2, DisplayName = "Bob Ray", EmailAddress = "bob@example.com", LastUsedTicks = 1 },
        ];
        public List<GroupModel> Groups { get; } = [];

        /// <summary>When set, a search waits for it: a search still running when the next key comes.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Match anywhere in the name or address, as the real ContactService does.</summary>
        public bool MatchContains { get; set; }

        public async Task<List<ContactModel>> SearchContactsAsync(string prefix, CancellationToken ct = default)
        {
            if (Gate is { } gate) await gate.Task;
            return MatchContains
                ? People.Where(p => p.DisplayName.Contains(prefix, StringComparison.OrdinalIgnoreCase)
                                 || p.EmailAddress.Contains(prefix, StringComparison.OrdinalIgnoreCase)).ToList()
                : People.Where(p => p.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                 || p.EmailAddress.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        public Task<List<GroupModel>> SearchGroupsAsync(string prefix, CancellationToken ct = default)
            => Task.FromResult(Groups.Where(g => g.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<List<ContactModel>> LoadAllContactsAsync() => Task.FromResult(People);
        public Task TouchGroupAsync(int groupId) => Task.CompletedTask;

        public Task UpsertContactAsync(ContactModel contact) => Task.CompletedTask;
        public Task DeleteContactAsync(int id) => Task.CompletedTask;
        public Task<bool> UpdateContactAsync(int id, string displayName, string emailAddress) => Task.FromResult(true);
        public Task ReplaceSyncedContactsAsync(Guid accountId, ContactSource source, IReadOnlyList<ContactModel> serverContacts) => Task.CompletedTask;
        public Task RemoveSyncedContactsAsync(Guid accountId) => Task.CompletedTask;
        public Task<List<GroupModel>> LoadAllGroupsAsync() => Task.FromResult(Groups);
        public Task<int> CreateGroupAsync(string name) => Task.FromResult(0);
        public Task RenameGroupAsync(int id, string newName) => Task.CompletedTask;
        public Task DeleteGroupAsync(int id) => Task.CompletedTask;
        public Task AddMemberAsync(int groupId, int contactId) => Task.CompletedTask;
        public Task RemoveMemberAsync(int groupId, int contactId) => Task.CompletedTask;
        public Task<List<int>> ListGroupsForContactAsync(int contactId) => Task.FromResult(new List<int>());
    }

    [Theory]
    [InlineData("", 0, 0, 0)]
    [InlineData("an", 2, 0, 2)]
    [InlineData("ann@example.com; bo", 19, 16, 19)]
    [InlineData("ann@example.com,bo", 18, 16, 18)]
    [InlineData("\"Lee; Ann\" <ann@example.com>; bo", 32, 29, 32)] // a separator inside quotes is part of a name
    [InlineData("ann@example.com; b; carol@example.com", 18, 16, 18)] // the caret in a middle entry
    [InlineData("ann@example.com; b; carol@example.com", 3, 0, 15)]
    public void EntryBounds_IsTheEntryAroundTheCaret(string text, int caret, int start, int end)
        => Assert.Equal((start, end), TextBoxAddressEntry.EntryBounds(text, caret));

    [StaFact]
    public void TextBoxEntry_AcceptingReplacesOnlyWhatIsBeingTyped()
    {
        EnsureApplication();
        var box = new TextBox { Text = "ann@example.com; bo" };
        box.CaretIndex = box.Text.Length; // typing at the end
        var entry = new TextBoxAddressEntry(box);
        Assert.Equal("bo", entry.CurrentInput);

        entry.AcceptSuggestion("Bob Ray", "bob@example.com");

        Assert.Equal("ann@example.com; Bob Ray <bob@example.com>; ", box.Text);
        Assert.Equal(box.Text.Length, box.CaretIndex);
        Assert.Equal(string.Empty, entry.CurrentInput); // ready for the next address

        entry.AddAddress("", "carol@example.com");
        Assert.Equal("ann@example.com; Bob Ray <bob@example.com>; carol@example.com; ", box.Text);
        // And it all reads back as three invitees.
        Assert.True(EventAttendees.TryParse(box.Text, null, out var parsed, out _));
        Assert.Equal(3, parsed.Count);
    }

    [StaFact]
    public void TextBoxEntry_InTheMiddle_ReplacesOnlyThatEntry()
    {
        // Where a refused save leaves the user: the bad entry in the middle, retyped.
        EnsureApplication();
        var box = new TextBox { Text = "ann@example.com; b; carol@example.com" };
        box.CaretIndex = 18; // just after the "b"
        var entry = new TextBoxAddressEntry(box);
        Assert.Equal("b", entry.CurrentInput);

        entry.AcceptSuggestion("Bob Ray", "bob@example.com");

        Assert.Equal("ann@example.com; Bob Ray <bob@example.com>; carol@example.com", box.Text);
        Assert.Equal("ann@example.com; Bob Ray <bob@example.com>".Length, box.CaretIndex);

        // A group's next member goes in right after it, not at the end.
        entry.AddAddress("", "dave@example.com");
        Assert.Equal("ann@example.com; Bob Ray <bob@example.com>; dave@example.com; carol@example.com", box.Text);
        Assert.True(EventAttendees.TryParse(box.Text, null, out var parsed, out _));
        Assert.Equal(4, parsed.Count);
    }

    [StaFact]
    public void TextBoxEntry_ReadOnly_IsNotOfferedSuggestions()
    {
        EnsureApplication();
        Assert.False(new TextBoxAddressEntry(new TextBox { IsReadOnly = true }).CanEdit);
        Assert.True(new TextBoxAddressEntry(new TextBox()).CanEdit);
    }

    [StaFact]
    public void Editor_TypingInInvite_SuggestsContacts_AndEnterChoosesTheFirst()
    {
        EnsureApplication();
        var vm = new EventEditorViewModel(new DateTime(2026, 11, 3, 9, 0, 0));
        var window = new EventEditorWindow(vm, new Contacts()) { ShowInTaskbar = false };
        window.Show(); // a popup opens only over a window that is on screen
        try
        {
            var invite = window.FindName("InviteBox") as TextBox;
            var popup = window.FindName("InviteSuggestionPopup") as System.Windows.Controls.Primitives.Popup;
            var list = window.FindName("InviteSuggestionList") as ListBox;
            Assert.NotNull(invite);
            Assert.NotNull(popup);
            Assert.NotNull(list);

            invite.Text = "an";
            invite.CaretIndex = invite.Text.Length;
            PumpUntil(() => popup.IsOpen);

            var only = Assert.Single(list.Items.Cast<object>());
            Assert.Equal("Ann Lee ann@example.com", only.ToString()); // what a screen reader is given

            // Ctrl+Enter, or Enter outside Invite, keeps its meaning and takes nothing.
            Assert.False(window.TryChooseSuggestionOnEnter(Key.Enter, ModifierKeys.Control, inInvite: true));
            Assert.False(window.TryChooseSuggestionOnEnter(Key.Enter, ModifierKeys.None, inInvite: false));
            Assert.True(popup.IsOpen);

            // Enter in Invite with the list showing picks the suggestion instead of saving.
            var saved = false;
            vm.Saved += _ => saved = true;
            Assert.True(window.TryChooseSuggestionOnEnter(Key.Enter, ModifierKeys.None, inInvite: true));
            Assert.Equal("Ann Lee <ann@example.com>; ", vm.InviteText);
            Assert.False(popup.IsOpen);
            Assert.False(saved);
            // With the list closed there is nothing to accept, so Enter keeps meaning Save.
            Assert.False(window.TryChooseSuggestionOnEnter(Key.Enter, ModifierKeys.None, inInvite: true));
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Editor_AGroup_ExpandsToItsMembers()
    {
        EnsureApplication();
        var contacts = new Contacts();
        contacts.Groups.Add(new GroupModel { Id = 9, Name = "Team", MemberContactIds = [1, 2] });
        var vm = new EventEditorViewModel(new DateTime(2026, 11, 3, 9, 0, 0));
        var window = new EventEditorWindow(vm, contacts) { ShowInTaskbar = false };
        window.Show();
        try
        {
            var invite = (TextBox)window.FindName("InviteBox");
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("InviteSuggestionPopup");
            invite.Text = "Tea";
            invite.CaretIndex = invite.Text.Length;
            PumpUntil(() => popup.IsOpen);

            Assert.True(window.Suggestions!.AcceptFirst());
            PumpUntil(() => vm.InviteText.Contains("bob@example.com", StringComparison.Ordinal));

            // Most recently used first, as compose orders a group's members.
            Assert.Equal("Ann Lee <ann@example.com>; Bob Ray <bob@example.com>; ", vm.InviteText);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Editor_SomeoneElsesMeeting_SuggestsNothing()
    {
        EnsureApplication();
        var meeting = new CalendarEvent
        {
            Uid = "m1", AccountId = Guid.NewGuid(), IsGraph = true, Summary = "Theirs",
            StartTimeTicks = DateTime.UtcNow.Ticks, EndTimeTicks = DateTime.UtcNow.AddHours(1).Ticks,
            Organizer = "boss@example.com", Attendees = [new() { Email = "x@example.com" }],
        };
        var vm = new EventEditorViewModel(meeting, userOrganizes: false);
        var window = new EventEditorWindow(vm, new Contacts());
        try
        {
            var invite = (TextBox)window.FindName("InviteBox");
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("InviteSuggestionPopup");
            invite.Text = "an"; // cannot happen through a read-only box, but must not open the list either
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.False(popup.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Compose's side of the shared controller ──────────────────────────────

    private sealed record ComposeRig(System.Windows.Window Window, QuickMail.Controls.TokenizedAddressBox Box,
        System.Windows.Controls.Primitives.Popup Popup, ListBox List, AddressSuggestionController Controller);

    /// <summary>A shown window with one chip box, wired the way ComposeWindow wires To.</summary>
    private static ComposeRig BuildComposeRig(Contacts contacts, IRecipientHistory? recents = null, Func<SuggestionSources>? sources = null)
    {
        var box = new QuickMail.Controls.TokenizedAddressBox();
        var list = new ListBox();
        var popup = new System.Windows.Controls.Primitives.Popup { Child = list, StaysOpen = true };
        var window = new System.Windows.Window
        {
            Content = new StackPanel { Children = { box } },
            Width = 400, Height = 200, ShowInTaskbar = false,
        };
        window.Show();
        var controller = new AddressSuggestionController(window, contacts, popup, list, recents, sources);
        controller.Attach(new TokenizedAddressEntry(box));
        return new ComposeRig(window, box, popup, list, controller);
    }

    private static KeyEventArgs Key_(System.Windows.Media.Visual source, Key key)
        => new(Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(source)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

    [StaFact]
    public void Compose_DownIntoTheList_HoldsTheFieldsCommit_UntilTheListCloses()
    {
        EnsureApplication();
        var rig = BuildComposeRig(new Contacts());
        try
        {
            var entering = 0;
            rig.Controller.EnteringSuggestions += () => entering++;
            rig.Box.InputBox.Text = "an";
            PumpUntil(() => rig.Popup.IsOpen);

            rig.Box.InputBox.RaiseEvent(Key_(rig.Window, Key.Down));

            // ComposeWindow listens for this to skip reading the chips again on the way back.
            Assert.Equal(1, entering);
            Assert.True(rig.Box.SuppressLostFocusCommit);

            // Escape in the list closes it, and the field commits normally again.
            var escape = Key_(rig.Window, Key.Escape);
            rig.Controller.List_PreviewKeyDown(rig.List, escape);
            Assert.True(escape.Handled);
            Assert.False(rig.Popup.IsOpen);
            // The popup reports Closed once its window has gone, a moment later; that is what
            // hands the field back its normal commit-on-leaving.
            PumpUntil(() => !rig.Box.SuppressLostFocusCommit);
        }
        finally
        {
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    [StaFact]
    public void Compose_EnterInTheList_AddsTheChip_AndAGroupAddsAChipPerMember()
    {
        EnsureApplication();
        var contacts = new Contacts();
        contacts.Groups.Add(new GroupModel { Id = 9, Name = "Team", MemberContactIds = [1, 2] });
        var rig = BuildComposeRig(contacts);
        try
        {
            rig.Box.InputBox.Text = "bo";
            PumpUntil(() => rig.Popup.IsOpen);
            rig.List.SelectedIndex = 0;
            var enter = Key_(rig.Window, Key.Enter);
            rig.Controller.List_PreviewKeyDown(rig.List, enter);
            Assert.True(enter.Handled); // never reaches a default button
            PumpUntil(() => rig.Box.GetChips().Count == 1);
            Assert.Equal("bob@example.com", rig.Box.GetChips()[0].EmailAddress);

            rig.Box.InputBox.Text = "Tea";
            PumpUntil(() => rig.Popup.IsOpen);
            rig.List.SelectedIndex = 0;
            rig.Controller.List_PreviewKeyDown(rig.List, Key_(rig.Window, Key.Tab));
            PumpUntil(() => rig.Box.GetChips().Count == 3);
            Assert.Equal(["bob@example.com", "ann@example.com", "bob@example.com"],
                         rig.Box.GetChips().Select(c => c.EmailAddress));
        }
        finally
        {
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    [StaFact]
    public void Compose_UpFromTheTop_ReturnsToTheField()
    {
        EnsureApplication();
        var rig = BuildComposeRig(new Contacts());
        try
        {
            rig.Box.InputBox.Text = "an";
            PumpUntil(() => rig.Popup.IsOpen);
            rig.List.SelectedIndex = 0;
            var up = Key_(rig.Window, Key.Up);
            rig.Controller.List_PreviewKeyDown(rig.List, up);
            Assert.True(up.Handled);
            Assert.False(rig.Popup.IsOpen);
            Assert.Empty(rig.Box.GetChips()); // nothing chosen
        }
        finally
        {
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    // ── Tab enters the first suggestion (#1037) ──────────────────────────────

    [StaFact]
    public void Compose_TabInTheField_EntersTheFirstSuggestion_AndStaysForTheNext()
    {
        EnsureApplication();
        var rig = BuildComposeRig(new Contacts());
        try
        {
            rig.Box.InputBox.Text = "bo";
            PumpUntil(() => rig.Popup.IsOpen);

            var tab = Key_(rig.Window, Key.Tab);
            rig.Box.InputBox.RaiseEvent(tab);

            Assert.True(tab.Handled); // focus does not move on
            Assert.Equal("bob@example.com", Assert.Single(rig.Box.GetChips()).EmailAddress);
            Assert.False(rig.Popup.IsOpen);
            Assert.Equal(string.Empty, rig.Box.InputBox.Text);

            // With no list showing, Tab is the field's own again.
            var second = Key_(rig.Window, Key.Tab);
            rig.Box.InputBox.RaiseEvent(second);
            Assert.False(second.Handled);
        }
        finally
        {
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    [StaFact]
    public void Compose_TabBeforeTheSearchCatchesUp_EntersWhatTheLastLetterFound_NotTheStaleFirst()
    {
        EnsureApplication();
        var contacts = new Contacts();
        contacts.People.Add(new ContactModel { Id = 3, DisplayName = "Bea Moe", EmailAddress = "bea@example.com" });
        var rig = BuildComposeRig(contacts);
        try
        {
            rig.Box.InputBox.Text = "b";                 // Bob first, then Bea
            PumpUntil(() => rig.Popup.IsOpen);
            Assert.Equal(2, rig.List.Items.Count);

            contacts.Gate = new TaskCompletionSource();
            rig.Box.InputBox.Text = "be";                // its search is held up
            DoEvents();
            Assert.Equal(2, rig.List.Items.Count);       // still the list for "b", Bob first

            var tab = Key_(rig.Window, Key.Tab);
            rig.Box.InputBox.RaiseEvent(tab);
            Assert.True(tab.Handled);
            Assert.Empty(rig.Box.GetChips());            // Bob was not entered

            contacts.Gate.SetResult();
            PumpUntil(() => rig.Box.GetChips().Count == 1);
            Assert.Equal("bea@example.com", rig.Box.GetChips()[0].EmailAddress);
        }
        finally
        {
            contacts.Gate?.TrySetResult();
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    [StaFact]
    public void Compose_TabWithOnlyAContainsMatch_KeepsWhatWasTyped_AndMovesOn()
    {
        EnsureApplication();
        var contacts = new Contacts { MatchContains = true };
        contacts.People.Clear(); // only joann, so nothing matches the typed address exactly
        contacts.People.Add(new ContactModel { Id = 3, DisplayName = "", EmailAddress = "joann@example.com" });
        var rig = BuildComposeRig(contacts);
        try
        {
            rig.Box.InputBox.Text = "ann@example.com";   // a new address, typed in full
            PumpUntil(() => rig.Popup.IsOpen);            // joann@example.com contains it

            var tab = Key_(rig.Window, Key.Tab);
            rig.Box.InputBox.RaiseEvent(tab);

            Assert.False(tab.Handled);                    // Tab is the field's own: it commits and moves on
            Assert.False(rig.Popup.IsOpen);
            Assert.Equal("ann@example.com", Assert.Single(rig.Box.GetChips()).EmailAddress);
        }
        finally
        {
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    [StaFact]
    public void Editor_TabAfterTheCaretMovesToAnotherEntry_IsNotTrapped()
    {
        EnsureApplication();
        var vm = new EventEditorViewModel(new DateTime(2026, 11, 3, 9, 0, 0));
        var window = new EventEditorWindow(vm, new Contacts()) { ShowInTaskbar = false };
        window.Show();
        try
        {
            var invite = (TextBox)window.FindName("InviteBox");
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("InviteSuggestionPopup");
            // Typed the way a person would: setting Text leaves the caret at the start, so the
            // search would run for carol's entry. Put the caret at the end, then type "an".
            invite.Text = "carol@example.com; ";
            invite.CaretIndex = invite.Text.Length;
            invite.SelectedText = "an";
            invite.CaretIndex = invite.Text.Length;
            PumpUntil(() => popup.IsOpen);

            invite.CaretIndex = 2;                         // Home: into carol's entry, no search starts
            var tab = Key_(window, Key.Tab);
            invite.RaiseEvent(tab);

            Assert.False(tab.Handled);                     // Tab moves on rather than being swallowed
            Assert.False(popup.IsOpen);
            Assert.Equal("carol@example.com; an", invite.Text); // and nothing was entered over carol
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Editor_TabWithTheListShowing_EntersTheFirst()
    {
        EnsureApplication();
        var vm = new EventEditorViewModel(new DateTime(2026, 11, 3, 9, 0, 0));
        var window = new EventEditorWindow(vm, new Contacts()) { ShowInTaskbar = false };
        window.Show();
        try
        {
            var invite = (TextBox)window.FindName("InviteBox");
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("InviteSuggestionPopup");
            invite.Text = "bo";
            invite.CaretIndex = invite.Text.Length;
            PumpUntil(() => popup.IsOpen);

            var tab = Key_(window, Key.Tab);
            invite.RaiseEvent(tab);

            Assert.True(tab.Handled);
            Assert.Equal("Bob Ray <bob@example.com>; ", vm.InviteText);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Compose_TabWaitingForASearchThatFindsNothing_ClosesTheList_SoTheNextTabMovesOn()
    {
        EnsureApplication();
        var contacts = new Contacts();
        var rig = BuildComposeRig(contacts);
        try
        {
            rig.Box.InputBox.Text = "b";
            PumpUntil(() => rig.Popup.IsOpen);
            contacts.Gate = new TaskCompletionSource();
            rig.Box.InputBox.Text = "bz";                 // will find nothing
            DoEvents();
            var tab = Key_(rig.Window, Key.Tab);
            rig.Box.InputBox.RaiseEvent(tab);
            Assert.True(tab.Handled);                     // held for the search

            contacts.Gate.SetResult();
            PumpUntil(() => !rig.Popup.IsOpen);
            Assert.DoesNotContain(rig.Box.GetChips(), c => c.EmailAddress == "bob@example.com"); // the stale first was not entered

            var next = Key_(rig.Window, Key.Tab);
            rig.Box.InputBox.RaiseEvent(next);
            Assert.False(next.Handled);
        }
        finally
        {
            contacts.Gate?.TrySetResult();
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    private sealed class Remembered : IRecipientHistory
    {
        public List<ContactModel> People { get; } = [new() { DisplayName = "Prof Bell", EmailAddress = "bell@uni.edu" }];
        public Task RecordAsync(IReadOnlyList<(string DisplayName, string EmailAddress)> recipients) => Task.CompletedTask;
        public Task<List<ContactModel>> SearchAsync(string text, int max, CancellationToken ct = default)
            => Task.FromResult(People.Where(p => p.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                                              || p.EmailAddress.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<int> CountAsync() => Task.FromResult(People.Count);
        public Task ClearAsync() => Task.CompletedTask;
        public Task<List<ContactModel>> ListAsync() => Task.FromResult(People.ToList());
        public Task<bool> RemoveAsync(string emailAddress) => Task.FromResult(false);
    }

    [StaFact]
    public void Compose_EachSuggestionSwitch_IsReadAsYouType()
    {
        EnsureApplication();
        var contacts = new Contacts();
        contacts.People.Add(new ContactModel { Id = 3, DisplayName = "Ben Hart", EmailAddress = "ben@example.com" });
        var config = new ConfigModel { RememberRecipients = true };
        var rig = BuildComposeRig(contacts, new Remembered(), () => SuggestionSources.From(config));
        try
        {
            // Both sources: Ben from the address book, Prof Bell remembered.
            rig.Box.InputBox.Text = "be";
            PumpUntil(() => rig.Popup.IsOpen);
            Assert.Equal(["Ben Hart", "Prof Bell"], rig.List.Items.Cast<AddressSuggestion>().Select(s => s.PrimaryText));

            // Address book off: only the remembered.
            config.SuggestFromAddressBook = false;
            rig.Box.InputBox.Text = "b";
            PumpUntil(() => rig.List.Items.Count == 1);
            Assert.True(rig.List.Items.Cast<AddressSuggestion>().Single().IsRecent);

            // People I've sent to off as well: nothing to suggest, so no list.
            config.SuggestPeopleEmailed = false;
            rig.Box.InputBox.Text = "be";
            PumpUntil(() => !rig.Popup.IsOpen);

            // Both back on, but the master switch off: still no list, and Tab keeps what was typed.
            config.SuggestFromAddressBook = config.SuggestPeopleEmailed = true;
            config.SuggestAddresses = false;
            rig.Box.InputBox.Text = "ben@example.com";
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.False(rig.Popup.IsOpen);
            var tab = Key_(rig.Window, Key.Tab);
            rig.Box.InputBox.RaiseEvent(tab);
            Assert.False(tab.Handled);
            Assert.Equal("ben@example.com", Assert.Single(rig.Box.GetChips()).EmailAddress);
        }
        finally
        {
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    [StaFact]
    public void Editor_FollowsTheSuggestionSettings_AndOffersRememberedPeople()
    {
        EnsureApplication();
        var configService = new StubConfigService();
        var vm = new EventEditorViewModel(new DateTime(2026, 11, 3, 9, 0, 0));
        var window = new EventEditorWindow(vm, new Contacts(), configService, new Remembered()) { ShowInTaskbar = false };
        window.Show();
        try
        {
            var invite = (TextBox)window.FindName("InviteBox");
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("InviteSuggestionPopup");

            invite.Text = "bel";
            invite.CaretIndex = invite.Text.Length;
            PumpUntil(() => popup.IsOpen);
            Assert.True(window.Suggestions!.AcceptFirst());
            Assert.Equal("Prof Bell <bell@uni.edu>; ", vm.InviteText);

            configService.Load().SuggestAddresses = false;
            invite.SelectedText = "an";
            invite.CaretIndex = invite.Text.Length;
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.False(popup.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Compose_RememberedPeople_AreSuggested_OnlyWhileTheSettingIsOn()
    {
        EnsureApplication();
        var on = false;
        var rig = BuildComposeRig(new Contacts(), new Remembered(), () => new SuggestionSources(AddressBook: true, PeopleEmailed: on));
        try
        {
            rig.Box.InputBox.Text = "bel";
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.False(rig.Popup.IsOpen);               // off: nothing remembered is offered

            on = true;
            rig.Box.InputBox.Text = "be";
            PumpUntil(() => rig.Popup.IsOpen);
            var only = Assert.Single(rig.List.Items.Cast<AddressSuggestion>());
            Assert.True(only.IsRecent);
            Assert.Equal("Prof Bell bell@uni.edu", only.ToString());

            rig.Box.InputBox.RaiseEvent(Key_(rig.Window, Key.Tab));
            Assert.Equal("bell@uni.edu", Assert.Single(rig.Box.GetChips()).EmailAddress);
        }
        finally
        {
            rig.Controller.Dispose();
            rig.Window.Close();
        }
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="condition"/> holds, failing after five seconds.
    /// Synchronous on purpose, like the other WPF tests here: an [StaFact] thread has no
    /// dispatcher loop of its own, so awaiting the dispatcher would wait forever.
    /// </summary>
    private static void PumpUntil(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DoEvents();
        while (!condition())
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(5))
                Assert.Fail("Condition was not met within five seconds.");
            Thread.Sleep(10);
            DoEvents();
        }
    }

    private static void PumpFor(TimeSpan time)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do { Thread.Sleep(10); DoEvents(); } while (clock.Elapsed < time);
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
