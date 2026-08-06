using System;
using System.Collections.Generic;
using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Default calendar for new appointments (issue #497): the calendar chosen from the folder tree's
/// context menu preselects the appointment editor's Calendar picker.
///
/// The three failure modes worth guarding are all about a default that cannot be honored exactly:
/// the tree offers a node per discovered calendar while a Microsoft or Google account contributes a
/// single save target, a saved default can name an account that has since been removed, and no
/// default at all must leave the editor exactly where it was before this feature existed (Local).
/// </summary>
public class DefaultCalendarTests
{
    // ── Config round trip ────────────────────────────────────────────────────────
    // Same reasoning as WindowingPreferencesTests: a property with no parse case or no writer block
    // resets silently on the next launch, and the setting looks wired up right up until relaunch.

    private static ProfileContext MakeTempProfile()
        => new(Path.Combine(Path.GetTempPath(), $"QM-CAL-{Guid.NewGuid():N}"));

    [Fact]
    public void DefaultCalendarSource_DefaultsToEmpty()
        => Assert.Equal(string.Empty, new ConfigModel().DefaultCalendarSource);

    [Theory]
    [InlineData("local")]
    [InlineData("2f9d3b7e-0000-4000-8000-000000000001")]
    [InlineData("2f9d3b7e-0000-4000-8000-000000000001|https%3A%2F%2Fp42-caldav.icloud.com%2F1%2Fcalendars%2Fhome%2F")]
    public void DefaultCalendarSource_RoundTrips(string tail)
    {
        var profile = MakeTempProfile();
        var config = new ConfigService(profile).Load();
        config.DefaultCalendarSource = tail;
        new ConfigService(profile).Save(config);

        Assert.Equal(tail, new ConfigService(profile).Load().DefaultCalendarSource);
    }

    [Fact]
    public void DefaultCalendarSource_ClearedValue_RoundTripsAsEmpty()
    {
        var profile = MakeTempProfile();
        var config = new ConfigService(profile).Load();
        config.DefaultCalendarSource = "local";
        new ConfigService(profile).Save(config);

        var reloaded = new ConfigService(profile).Load();
        reloaded.DefaultCalendarSource = string.Empty;
        new ConfigService(profile).Save(reloaded);

        Assert.Equal(string.Empty, new ConfigService(profile).Load().DefaultCalendarSource);
    }

    /// <summary>
    /// The stored value is the calendar tree node's tail, so it must survive the round trip the
    /// tree itself parses it with — one encoding, one parser.
    /// </summary>
    [Fact]
    public void StoredTail_ParsesBackToTheFilterItCameFrom()
    {
        var acct = Guid.NewGuid();
        var calId = "https://p42-caldav.icloud.com/1/calendars/home/";
        var tail = acct.ToString("D") + "|" + Uri.EscapeDataString(calId);

        Assert.Equal(new MainViewModel.CalendarFilter(acct, calId),
            MainViewModel.CalendarFilterFor(MainViewModel.CalendarSourcePrefix + tail));
    }

    // ── Editor target selection ──────────────────────────────────────────────────

    private static EventEditorViewModel MakeEditor(params CalendarSaveTarget[] accountTargets)
        => new(DateTime.Today.AddHours(9), accountTargets);

    [Fact]
    public void SelectTarget_ExactCalendar_IsSelected()
    {
        var apple = Guid.NewGuid();
        var editor = MakeEditor(
            new CalendarSaveTarget("Apple: Home", apple, "cal-home", "Home"),
            new CalendarSaveTarget("Apple: Family", apple, "cal-family", "Family"));

        Assert.True(editor.SelectTarget(apple, "cal-family"));
        Assert.Equal(apple, editor.SelectedTargetAccountId);
        Assert.Equal("cal-family", editor.SelectedTargetCalendarId);
        Assert.Equal("Family", editor.SelectedTargetCalendarName);
    }

    /// <summary>
    /// A Microsoft or Google account shows a node per synced calendar in the tree but offers ONE
    /// save target (its default calendar). Defaulting to one of those calendars must land on the
    /// account rather than falling back to Local, which is a different mailbox entirely.
    /// </summary>
    [Fact]
    public void SelectTarget_UnofferedCalendar_FallsBackToTheSameAccount()
    {
        var work = Guid.NewGuid();
        var editor = MakeEditor(new CalendarSaveTarget("Work", work));

        Assert.True(editor.SelectTarget(work, "cal-team"));
        Assert.Equal(work, editor.SelectedTargetAccountId);
        Assert.Null(editor.SelectedTargetCalendarId);
    }

    [Fact]
    public void SelectTarget_LocalCalendar_SelectsIndexZero()
    {
        var editor = MakeEditor(new CalendarSaveTarget("Work", Guid.NewGuid()));

        Assert.True(editor.SelectTarget(CalendarEvent.LocalAccountId, null));
        Assert.Equal(0, editor.SelectedTargetIndex);
        Assert.Equal(CalendarEvent.LocalAccountId, editor.SelectedTargetAccountId);
    }

    [Fact]
    public void SelectTarget_UnknownAccount_LeavesTheEditorOnLocal()
    {
        var editor = MakeEditor(new CalendarSaveTarget("Work", Guid.NewGuid()));

        Assert.False(editor.SelectTarget(Guid.NewGuid(), null));
        Assert.Equal(0, editor.SelectedTargetIndex);
        Assert.Equal(CalendarEvent.LocalAccountId, editor.SelectedTargetAccountId);
    }

    // ── NewEvent honors the default ──────────────────────────────────────────────

    private static (CalendarViewModel Vm, AccountModel Apple, string HomeId, string FamilyId) MakeICloudVm()
    {
        var apple = new AccountModel
        {
            Id = Guid.NewGuid(), ImapHost = "imap.mail.me.com", AccountName = "Apple", SyncCalendar = true,
        };
        const string home = "https://p42-caldav.icloud.com/1/calendars/home/";
        const string family = "https://p42-caldav.icloud.com/1/calendars/family/";
        var vm = new CalendarViewModel(new StubCalendarService(), onlineMode: false,
            showDeclinedEvents: false, showFieldLabels: false,
            graphSync: new StubGraphCalendarSyncService(),
            graphAccountsProvider: () => new[] { apple },
            calendarSourcesProvider: () => new List<(Guid, string, string)>
            {
                (apple.Id, home, "Home"),
                (apple.Id, family, "Family"),
            });
        return (vm, apple, home, family);
    }

    private static EventEditorViewModel OpenNewEventEditor(CalendarViewModel vm)
    {
        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.NewEventCommand.Execute(null);
        Assert.NotNull(editor);
        return editor!;
    }

    [Fact]
    public void NewEvent_WithNoDefault_OpensOnLocalCalendar()
    {
        var (vm, _, _, _) = MakeICloudVm();

        var editor = OpenNewEventEditor(vm);

        Assert.Equal(0, editor.SelectedTargetIndex);
        Assert.Equal(CalendarEvent.LocalAccountId, editor.SelectedTargetAccountId);
    }

    [Fact]
    public void NewEvent_WithDefaultCalendar_OpensOnThatCalendar()
    {
        var (vm, apple, _, family) = MakeICloudVm();
        vm.DefaultCalendar = new MainViewModel.CalendarFilter(apple.Id, family);

        var editor = OpenNewEventEditor(vm);
        editor.Title = "Reunion";

        Assert.Equal(apple.Id, editor.SelectedTargetAccountId);
        Assert.True(editor.TryBuildEvent(out var evt, out _));
        Assert.Equal(apple.Id, evt.AccountId);
        Assert.Equal(family, evt.CalendarId);
        Assert.Equal("Family", evt.CalendarName);
    }

    /// <summary>The default only preselects; the user can still pick something else before saving.</summary>
    [Fact]
    public void NewEvent_DefaultIsOnlyAPreselection()
    {
        var (vm, apple, _, family) = MakeICloudVm();
        vm.DefaultCalendar = new MainViewModel.CalendarFilter(apple.Id, family);

        var editor = OpenNewEventEditor(vm);
        Assert.True(editor.ShowSaveTarget);          // the picker is still offered
        editor.SelectedTargetIndex = 0;              // …and still works
        editor.Title = "Dentist";

        Assert.True(editor.TryBuildEvent(out var evt, out _));
        Assert.Equal(CalendarEvent.LocalAccountId, evt.AccountId);
    }

    /// <summary>
    /// A default naming an account that has since been removed must not strand the editor on a
    /// target that no longer exists — it falls back to Local, which always saves.
    /// </summary>
    [Fact]
    public void NewEvent_DefaultForMissingAccount_FallsBackToLocal()
    {
        var (vm, _, _, _) = MakeICloudVm();
        vm.DefaultCalendar = new MainViewModel.CalendarFilter(Guid.NewGuid(), "cal-gone");

        var editor = OpenNewEventEditor(vm);

        Assert.Equal(0, editor.SelectedTargetIndex);
        Assert.Equal(CalendarEvent.LocalAccountId, editor.SelectedTargetAccountId);
    }

    [Fact]
    public void NewEvent_DefaultOfLocal_OpensOnLocalCalendar()
    {
        var (vm, _, _, _) = MakeICloudVm();
        vm.DefaultCalendar = new MainViewModel.CalendarFilter(CalendarEvent.LocalAccountId, null);

        var editor = OpenNewEventEditor(vm);

        Assert.Equal(0, editor.SelectedTargetIndex);
    }

    // ── Tree node marker ─────────────────────────────────────────────────────────

    [Fact]
    public void DefaultCalendarNode_CarriesTheMarkerInItsAccessibleName()
    {
        var node = new FolderTreeNode
        {
            Folder = new MailFolderModel { FullName = MainViewModel.CalendarSourcePrefix + "local", DisplayName = "Local Calendar" },
            Label = "Local Calendar",
            IsCalendarNode = true,
        };

        Assert.Equal("Local Calendar", node.AutomationName);
        Assert.Equal(string.Empty, node.DefaultCalendarDisplay);

        node.IsDefaultCalendar = true;

        // The state has to be IN the name — an ItemStatus-only marker is not reliably spoken (#227).
        Assert.Equal("Local Calendar, default calendar", node.AutomationName);
        Assert.Equal("(default)", node.DefaultCalendarDisplay);
    }

    [Fact]
    public void DefaultCalendarNode_RaisesPropertyChangedForTheDerivedDisplays()
    {
        var node = new FolderTreeNode { Label = "Apple", IsCalendarNode = true };
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.IsDefaultCalendar = true;

        Assert.Contains(nameof(FolderTreeNode.AutomationName), changed);
        Assert.Contains(nameof(FolderTreeNode.DefaultCalendarDisplay), changed);
    }
}
