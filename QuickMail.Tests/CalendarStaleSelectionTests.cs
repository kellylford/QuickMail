using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #1014: Delete in the calendar list could delete the email that was selected before the
/// calendar opened.
///
/// <para>Delete is shared by <c>mail.delete</c> and <c>calendar.deleteEvent</c>, and
/// <see cref="CommandRegistry.FindByGesture"/> returns the first registered command whose
/// availability holds. <c>mail.delete</c> is registered first (the ViewModel registers it before the
/// window's calendar commands exist), and its availability asked only for a selected message and
/// that the folder tree not have focus. So whenever <see cref="MainViewModel.SelectedMessage"/> was
/// still set while the user was in the calendar, Delete went to the mail command, which deleted a
/// message the user could not see.</para>
///
/// <para>Two things now prevent it, and each is tested here: entering the calendar clears the mail
/// selection in the ViewModel, rather than relying on the message list's binding to do it as a side
/// effect; and every window command that acts on the mail selection stands aside while focus is in
/// the calendar pane, which covers the selection that opening an invitation's source message sets
/// while the calendar is showing.</para>
/// </summary>
public class CalendarStaleSelectionTests
{
    private static MainViewModel MakeVm(ICommandRegistry? registry = null) =>
        new(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            registry ?? new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
            calendarService: new StubCalendarService());

    private static MailMessageSummary Message(string id = "m1") => new()
    {
        MessageId  = id,
        AccountId  = Guid.NewGuid(),
        FolderName = "INBOX",
        Subject    = "The email selected before the calendar opened",
    };

    /// <summary>
    /// The ViewModel itself lets go of the mail selection on entering the calendar. Nothing in the
    /// ViewModel did before, so the only thing that could clear it was the message list's two-way
    /// SelectedItem binding reacting to the list being emptied.
    /// </summary>
    [Fact]
    public async Task EnteringTheCalendar_ClearsTheMailSelection()
    {
        var registry = new CommandRegistry();
        var vm = MakeVm(registry);
        var msg = Message();
        vm.Messages = [msg];
        vm.SelectedMessage = msg;
        Assert.True(vm.CanActOnSelection());

        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.CalendarFolder);

        Assert.True(vm.IsCalendarView);
        Assert.Null(vm.SelectedMessage);
        Assert.False(vm.CanActOnSelection());

        // And so Delete reaches no mail command that would act: the ViewModel's mail.delete, the
        // one the window overrides, is unavailable.
        var delete = registry.FindByGesture(Key.Delete, ModifierKeys.None);
        Assert.NotNull(delete);
        Assert.Equal("mail.delete", delete!.Id);
        Assert.False(delete.IsAvailable?.Invoke() ?? true);
    }

    /// <summary>
    /// Opening one of the calendar's per-source children goes through the same path as the
    /// Calendar node itself.
    /// </summary>
    [Fact]
    public async Task EnteringACalendarSource_ClearsTheMailSelectionToo()
    {
        var vm = MakeVm();
        var msg = Message();
        vm.Messages = [msg];
        vm.SelectedMessage = msg;

        await vm.SelectFolderCommand.ExecuteAsync(new MailFolderModel
        {
            FullName    = MainViewModel.CalendarSourcePrefix + "local",
            DisplayName = "Local Calendar",
        });

        Assert.True(vm.IsCalendarView);
        Assert.Null(vm.SelectedMessage);
    }

    /// <summary>
    /// The Message menu, the toolbar and the Command Palette run Delete and Archive without the
    /// window's focus gate. A selection that comes back while the calendar shows (returning to the
    /// list tab from a message tab, or closing an invitation's source message) must not be deleted.
    /// </summary>
    [Fact]
    public async Task DeleteAndArchive_InTheCalendar_DoNotActOnAHiddenSelection()
    {
        var accountId = Guid.NewGuid();
        var msg = new MailMessageSummary
        {
            MessageId = "m1", AccountId = accountId, FolderName = "INBOX", Subject = "Hidden",
        };
        var mail = new FolderedMailService([], []);
        var vm = new MainViewModel(mail, new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
            calendarService: new StubCalendarService());
        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.CalendarFolder);

        vm.SelectedMessage = msg;   // as the list tab or a closed invitation leaves it
        await vm.DeleteMessageCommand.ExecuteAsync(null);
        Assert.Empty(mail.Trashed);
        Assert.Null(vm.SelectedMessage);
        Assert.Equal("No message is selected.", vm.StatusText);

        vm.SelectedMessage = msg;
        await vm.ArchiveMessageCommand.ExecuteAsync(null);
        Assert.Null(vm.SelectedMessage);
    }

    /// <summary>A message open in the reading pane over the calendar is still the user's to delete.</summary>
    [Fact]
    public async Task Delete_InTheCalendar_WithTheMessageOpen_StillDeletesIt()
    {
        var msg = new MailMessageSummary
        {
            MessageId = "m1", AccountId = Guid.NewGuid(), FolderName = "INBOX", Subject = "Opened invitation",
        };
        var mail = new FolderedMailService([], []);
        var vm = new MainViewModel(mail, new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
            calendarService: new StubCalendarService());
        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.CalendarFolder);

        vm.SelectedMessage = msg;
        vm.IsMessageOpen = true;
        await vm.DeleteMessageCommand.ExecuteAsync(null);

        Assert.Contains(mail.Trashed, t => t.Id == "m1");
    }

    /// <summary>
    /// The registry's rule that the fix depends on, with the two commands' real shapes: of the
    /// commands sharing Delete, the first available one wins. With the calendar list focused and a
    /// stale mail selection, the old mail.delete condition was available and was registered first,
    /// so it won; with the calendar exclusion it stands aside and calendar.deleteEvent gets the key.
    /// <para>This documents the registry rule with stand-in conditions; it does not read MainWindow.
    /// The window's own registrations are checked by the source test below.</para>
    /// </summary>
    [Theory]
    [InlineData(false, "mail.delete")]          // the condition as it was: the email is deleted
    [InlineData(true,  "calendar.deleteEvent")] // with the calendar excluded: the appointment is
    public void DeleteInTheCalendarList_WithAStaleMailSelection_Reaches(bool excludeCalendar, string expected)
    {
        var hasStaleMessage = true;   // SelectedMessage still set from the Inbox
        var calendarFocused = true;   // focus in the calendar list, an appointment selected
        var hasEvent        = true;

        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition(
            id: "mail.delete", category: "Mail", title: "Delete", execute: () => { },
            defaultKey: Key.Delete, defaultModifiers: ModifierKeys.None,
            isAvailable: () => hasStaleMessage && (!excludeCalendar || !calendarFocused)));
        registry.Register(new CommandDefinition(
            id: "calendar.deleteEvent", category: "Calendar", title: "Delete Appointment", execute: () => { },
            defaultKey: Key.Delete, defaultModifiers: ModifierKeys.None,
            isAvailable: () => calendarFocused && hasEvent));

        Assert.Equal(expected, registry.FindByGesture(Key.Delete, ModifierKeys.None)!.Id);
    }

    /// <summary>
    /// Every window command that acts on the mail selection must stand aside in the calendar pane,
    /// not only Delete: Ctrl+Q there marked the stale message read, Ctrl+Shift+M archived it, and
    /// Ctrl+P printed it. The window's registrations cannot be exercised without a shown window and
    /// real keyboard focus, so this reads them, as <see cref="MoveToFolderGroupTargetTests"/> does.
    /// </summary>
    [Theory]
    [InlineData("mail.delete")]
    [InlineData("mail.archive")]
    [InlineData("mail.markRead")]
    [InlineData("mail.pickFlag")]
    [InlineData("mail.moveToFolder")]
    [InlineData("mail.copyToFolder")]
    [InlineData("mail.openInNewTab")]
    [InlineData("mail.openInWindow")]
    [InlineData("mail.save")]
    [InlineData("mail.saveAs")]
    [InlineData("mail.saveAttachments")]
    [InlineData("mail.print")]
    public void WindowSelectionCommands_StandAsideInTheCalendar(string commandId)
    {
        var registration = RegistrationOf(commandId);
        Assert.True(
            registration.Contains("CanActOnSelectionOutsideCalendar", StringComparison.Ordinal)
            || registration.Contains("!CalendarHasFocus", StringComparison.Ordinal),
            $"{commandId} does not stand aside while the calendar has focus.");
    }

    /// <summary>The calendar's own Delete must still be offered there.</summary>
    [Fact]
    public void CalendarDelete_IsStillAvailableInTheCalendarList()
    {
        var registration = RegistrationOf("calendar.deleteEvent");
        Assert.Contains("CalendarList.IsKeyboardFocusWithin", registration, StringComparison.Ordinal);
    }

    private static string RegistrationOf(string commandId)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml.cs"));
        var idAt   = source.IndexOf($"id: \"{commandId}\"", StringComparison.Ordinal);
        Assert.True(idAt >= 0, $"{commandId} is not registered in MainWindow.xaml.cs — renamed or removed?");
        var end = source.IndexOf("));", idAt, StringComparison.Ordinal);
        Assert.True(end > idAt, $"could not find the end of the {commandId} registration.");
        return source.Substring(idAt, end - idAt);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
