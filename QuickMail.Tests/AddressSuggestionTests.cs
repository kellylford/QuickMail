using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
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

        public Task<List<ContactModel>> SearchContactsAsync(string prefix, CancellationToken ct = default)
            => Task.FromResult(People.Where(p => p.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                              || p.EmailAddress.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList());
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
    [InlineData("", 0)]
    [InlineData("an", 0)]
    [InlineData("ann@example.com; bo", 16)]
    [InlineData("ann@example.com,bo", 16)]
    [InlineData("\"Lee; Ann\" <ann@example.com>; bo", 29)] // a separator inside quotes is part of a name
    public void TailStart_IsAfterTheLastSeparatorOutsideQuotes(string text, int expected)
        => Assert.Equal(expected, TextBoxAddressEntry.TailStart(text));

    [StaFact]
    public void TextBoxEntry_AcceptingReplacesOnlyWhatIsBeingTyped()
    {
        EnsureApplication();
        var box = new TextBox { Text = "ann@example.com; bo" };
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
    public void TextBoxEntry_ReadOnly_IsNotOfferedSuggestions()
    {
        EnsureApplication();
        Assert.False(new TextBoxAddressEntry(new TextBox { IsReadOnly = true }).CanEdit);
        Assert.True(new TextBoxAddressEntry(new TextBox()).CanEdit);
    }

    [StaFact]
    public async Task Editor_TypingInInvite_SuggestsContacts_AndEnterChoosesTheFirst()
    {
        EnsureApplication();
        var vm = new EventEditorViewModel(new DateTime(2026, 11, 3, 9, 0, 0));
        var window = new EventEditorWindow(vm, new Contacts());
        try
        {
            var invite = window.FindName("InviteBox") as TextBox;
            var popup = window.FindName("InviteSuggestionPopup") as System.Windows.Controls.Primitives.Popup;
            var list = window.FindName("InviteSuggestionList") as ListBox;
            Assert.NotNull(invite);
            Assert.NotNull(popup);
            Assert.NotNull(list);

            invite.Text = "an";
            await WaitUntilAsync(() => popup.IsOpen);

            var only = Assert.Single(list.Items.Cast<object>());
            Assert.Equal("Ann Lee ann@example.com", only.ToString()); // what a screen reader is given

            // Enter with the list showing picks the suggestion instead of saving the appointment
            // (the window calls AcceptFirst for Enter in Invite).
            var saved = false;
            vm.Saved += _ => saved = true;
            Assert.True(window.Suggestions!.AcceptFirst());
            Assert.Equal("Ann Lee <ann@example.com>; ", vm.InviteText);
            Assert.False(popup.IsOpen);
            Assert.False(saved);
            // With the list closed there is nothing to accept, so Enter keeps meaning Save.
            Assert.False(window.Suggestions.AcceptFirst());
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public async Task Editor_AGroup_ExpandsToItsMembers()
    {
        EnsureApplication();
        var contacts = new Contacts();
        contacts.Groups.Add(new GroupModel { Id = 9, Name = "Team", MemberContactIds = [1, 2] });
        var vm = new EventEditorViewModel(new DateTime(2026, 11, 3, 9, 0, 0));
        var window = new EventEditorWindow(vm, contacts);
        try
        {
            var invite = (TextBox)window.FindName("InviteBox");
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("InviteSuggestionPopup");
            invite.Text = "Tea";
            await WaitUntilAsync(() => popup.IsOpen);

            Assert.True(window.Suggestions!.AcceptFirst());
            await WaitUntilAsync(() => vm.InviteText.Contains("bob@example.com", StringComparison.Ordinal));

            // Most recently used first, as compose orders a group's members.
            Assert.Equal("Ann Lee <ann@example.com>; Bob Ray <bob@example.com>; ", vm.InviteText);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public async Task Editor_SomeoneElsesMeeting_SuggestsNothing()
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
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(popup.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds, or fails after two seconds.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Condition was not met within two seconds.");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
