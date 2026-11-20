using System;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Where the Calendar node sits among the folder tree's roots. It has always been first, which is
/// in the way for someone who arrows down from the top of the tree to reach mail; this makes the
/// position a choice, taken from the Calendar node's context menu.
///
/// Two failure modes are worth guarding. A setting with no parse case or no writer block resets on
/// the next launch and looks wired up right until then (the reasoning WindowingPreferencesTests and
/// DefaultCalendarTests are both built on). And the second move is the one that breaks: the first
/// build assigns FolderTree outright, every later one goes through MergeFolderNodes, so a change
/// that only works on a fresh tree passes a single-toggle test and does nothing in the app.
/// </summary>
public class CalendarFolderPositionTests
{
    private static ProfileContext MakeTempProfile()
        => new(Path.Combine(Path.GetTempPath(), $"QM-CALPOS-{Guid.NewGuid():N}"));

    private static MainViewModel MakeVm(IConfigService? config = null) =>
        new(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            config ?? new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), calendarService: new StubCalendarService());

    private static string RootLabels(MainViewModel vm) =>
        string.Join(",", vm.FolderTree!.Select(n => n.Label));

    // ── Config round trip ────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_To_CalendarFirst()
        => Assert.False(new ConfigModel().CalendarAtEndOfFolderList);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Setting_RoundTripsThroughConfigIni(bool atEnd)
    {
        var profile = MakeTempProfile();
        var config = new ConfigService(profile).Load();
        config.CalendarAtEndOfFolderList = atEnd;
        new ConfigService(profile).Save(config);

        Assert.Equal(atEnd, new ConfigService(profile).Load().CalendarAtEndOfFolderList);
    }

    [Fact]
    public void ChoiceIsReadBackAtStartup()
    {
        var config = new StubConfigService();
        var stored = config.Load();
        stored.CalendarAtEndOfFolderList = true;
        config.Save(stored);

        Assert.True(MakeVm(config).CalendarAtEndOfFolderList);
    }

    [Fact]
    public void ChoosingAPositionPersistsIt()
    {
        var config = new StubConfigService();
        var vm = MakeVm(config);

        vm.SetCalendarAtEndOfFolderList(true);

        Assert.True(config.Load().CalendarAtEndOfFolderList);
        Assert.True(vm.CalendarAtEndOfFolderList);
    }

    // ── Tree placement ───────────────────────────────────────────────────────────

    [Fact]
    public void AtEnd_PutsCalendarAfterEveryOtherRoot()
    {
        var vm = MakeVm();

        vm.SetCalendarAtEndOfFolderList(true);

        Assert.NotNull(vm.FolderTree);
        Assert.Equal("Calendar", vm.FolderTree![^1].Label);
        Assert.NotEqual("Calendar", vm.FolderTree[0].Label);
    }

    /// <summary>
    /// The move back, which goes through MergeFolderNodes rather than a fresh assignment — the path
    /// every change after the first one takes.
    /// </summary>
    [Fact]
    public void MovingBackToTheTop_ReordersTheLiveTree()
    {
        var vm = MakeVm();

        vm.SetCalendarAtEndOfFolderList(true);
        var atEnd = RootLabels(vm);
        vm.SetCalendarAtEndOfFolderList(false);

        Assert.Equal("Calendar", vm.FolderTree![0].Label);
        Assert.NotEqual(atEnd, RootLabels(vm));
        // Nothing gained or lost on the way — only the calendar's place changed.
        Assert.Equal(atEnd.Split(',').OrderBy(s => s), RootLabels(vm).Split(',').OrderBy(s => s));
    }

    [Fact]
    public void ChoosingThePositionItIsAlreadyIn_SaysSo()
    {
        var vm = MakeVm();

        Assert.Equal("Calendar is already at the start of the folder list.",
                     vm.SetCalendarAtEndOfFolderList(false));
        Assert.Equal("Calendar moved to the end of the folder list.",
                     vm.SetCalendarAtEndOfFolderList(true));
        Assert.Equal("Calendar is already at the end of the folder list.",
                     vm.SetCalendarAtEndOfFolderList(true));
    }

    /// <summary>
    /// Online-only builds and the test harness wire no calendar service, so there is no Calendar
    /// node to place. The command is still registered and reachable from the palette, so it must
    /// say that rather than silently writing a setting nothing honors.
    /// </summary>
    [Fact]
    public void WithNoCalendar_TheChoiceIsRefused()
    {
        var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), new StubLocalStoreService(), new StubOAuthService(),
            new StubSyncService(), new StubConfigService(), new StubCommandRegistry(),
            new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.Equal("The calendar is not available, so it has no place in the folder list.",
                     vm.SetCalendarAtEndOfFolderList(true));
    }
}
