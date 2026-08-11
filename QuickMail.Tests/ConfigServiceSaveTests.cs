using System;
using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class ConfigServiceSaveTests
{
    private static ProfileContext MakeTempProfile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QM-CFG-{Guid.NewGuid():N}");
        return new ProfileContext(tempDir);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSettings()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.AnnounceHints = !config.AnnounceHints;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal(config.AnnounceHints, reloaded.AnnounceHints);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSort()
    {
        // Sort had exactly the failure this class exists to catch: ConfigModel.Sort existed and
        // MainViewModel wrote it on every sort change, but ConfigService had neither a parse case
        // nor a writer block — so the chosen sort survived the process (cached ConfigModel) and
        // silently reset to Newest First on every launch. Found while fixing #520.
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.Sort = "dateAsc";
        service.Save(config);

        Assert.Equal("dateAsc", new ConfigService(profile).Load().Sort);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFlaggedFirstSort()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.Sort = "flaggedFirst";
        service.Save(config);

        Assert.Equal("flaggedFirst", new ConfigService(profile).Load().Sort);
    }

    [Fact]
    public void LoadingAnUnrecognisedSort_FallsBackToNewestFirst()
    {
        var profile = MakeTempProfile();
        File.WriteAllText(Path.Combine(profile.ProfileDir, "config.ini"),
                          "[global]\nSort = nonsense\n");

        Assert.Equal("dateDesc", new ConfigService(profile).Load().Sort);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRememberViewPerFolder()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        Assert.True(service.Load().RememberViewPerFolder);   // on by default (#520)

        var config = service.Load();
        config.RememberViewPerFolder = false;
        service.Save(config);

        Assert.False(new ConfigService(profile).Load().RememberViewPerFolder);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFieldLabelSettings()
    {
        // Same failure shape as the calendar regression below, and RuleListShowFieldLabels really
        // had it: the property and the Settings writer existed, but ConfigService had neither a
        // parse case nor a writer block, so it silently reset on every launch. A setting is not
        // wired up until it survives this round trip.
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.MessageListShowFieldLabels = true;
        config.RuleListShowFieldLabels    = true;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.True(reloaded.MessageListShowFieldLabels);
        Assert.True(reloaded.RuleListShowFieldLabels);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsStartupSettings()
    {
        // The startup folder is the whole point of #516; if it does not survive a save/load it is
        // not a setting, it is a session preference. Also guards the [windowing] ordering trap
        // documented in SaveThenLoad_RoundTripsCalendarSettings below — these keys are parsed
        // under [global] and would be silently dropped if written after that header.
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.StartupFolder        = "INBOX/Work";
        config.StartupFolderAccount = "8f14e45f-ceea-467a-9575-1b1c1b1c1b1c";
        config.StartupFolderLabel   = "Work";
        config.StartupSyncScope     = ConfigModel.StartupSyncScopeInboxes;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal("INBOX/Work", reloaded.StartupFolder);
        Assert.Equal("8f14e45f-ceea-467a-9575-1b1c1b1c1b1c", reloaded.StartupFolderAccount);
        Assert.Equal("Work", reloaded.StartupFolderLabel);
        Assert.Equal(ConfigModel.StartupSyncScopeInboxes, reloaded.StartupSyncScope);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAVirtualStartupFolder()
    {
        // The common case: "open me in All Inboxes". Stored without the NUL sentinel prefix
        // because an INI file cannot carry one.
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.StartupFolder      = "AllInboxes";
        config.StartupFolderLabel = "All Inboxes";
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal("AllInboxes", reloaded.StartupFolder);
        Assert.Equal(string.Empty, reloaded.StartupFolderAccount);
    }

    [Fact]
    public void FreshConfig_DefaultsToAllMailAndStartupFolderScope()
    {
        var profile = MakeTempProfile();

        var config = new ConfigService(profile).Load();

        Assert.Equal(string.Empty, config.StartupFolder);          // empty == All Mail
        Assert.Equal(string.Empty, config.StartupFolderAccount);
        Assert.Equal(ConfigModel.StartupSyncScopeStartupFolder, config.StartupSyncScope);

        var ini = File.ReadAllText(Path.Combine(profile.ProfileDir, "config.ini"));
        Assert.Contains("StartupSyncScope = startupFolder", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnknownStartupSyncScope_NormalizesToTheDefault()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);
        var config  = service.Load();
        config.StartupSyncScope = "everything-please";
        service.Save(config);

        Assert.Equal(ConfigModel.StartupSyncScopeStartupFolder,
                     new ConfigService(profile).Load().StartupSyncScope);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsCalendarSettings()
    {
        // Regression test: ShowDeclinedEvents and CalendarPaneOpen were being written by
        // Save() after the "[windowing]" section header, so ParseFile silently dropped them
        // on the next Load() (they're only recognized under "[global]"). Both settings looked
        // saved (right there in the file) but were reset to their default (off) on every restart.
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.ShowDeclinedEvents = !config.ShowDeclinedEvents;
        config.CalendarPaneOpen = !config.CalendarPaneOpen;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal(config.ShowDeclinedEvents, reloaded.ShowDeclinedEvents);
        Assert.Equal(config.CalendarPaneOpen, reloaded.CalendarPaneOpen);
    }

    [Fact]
    public void AnnounceMessageActions_DefaultsOn_AndRoundTrips()
    {
        // Issue #317: delete/archive announcements get their own toggle, on by default, and must
        // survive a real INI write→read (it's written under [global], like the other announce keys).
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        Assert.True(service.Load().AnnounceMessageActions); // on by default

        var config = service.Load();
        config.AnnounceMessageActions = false;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.False(reloaded.AnnounceMessageActions);
    }

    [Fact]
    public void NotifyOnNewMail_DefaultsOff_AndRoundTrips()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        // Default is off (opt-in) when the key is absent from a fresh config.
        Assert.False(service.Load().NotifyOnNewMail);

        var config = service.Load();
        config.NotifyOnNewMail = true;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.True(reloaded.NotifyOnNewMail);
    }

    [Fact]
    public void CloseToTray_DefaultsOff_AndRoundTrips()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        Assert.False(service.Load().CloseToTray);       // default off
        Assert.False(service.Load().TrayHintShown);     // default off

        var config = service.Load();
        config.CloseToTray = true;
        config.TrayHintShown = true;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.True(reloaded.CloseToTray);
        Assert.True(reloaded.TrayHintShown);
    }

    [Fact]
    public void MailSyncPollMinutes_DefaultsTo5_AndRoundTrips()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        // Default is 5 minutes (fallback poll on) for a fresh config.
        Assert.Equal(5, service.Load().MailSyncPollMinutes);

        var config = service.Load();
        config.MailSyncPollMinutes = 15;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal(15, reloaded.MailSyncPollMinutes);
    }

    [Theory]
    [InlineData(0, 0)]     // 0 disables the fallback poll and is preserved
    [InlineData(-3, 0)]    // any non-positive value normalizes to 0 (disabled)
    [InlineData(1, 1)]     // lower bound
    [InlineData(200, 120)] // clamped to the 120-minute ceiling
    public void MailSyncPollMinutes_IsClampedOnLoad(int written, int expected)
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.MailSyncPollMinutes = written;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal(expected, reloaded.MailSyncPollMinutes);
    }


    [Fact]
    public void SaveThenLoad_RoundTripsReadAsPlainText()
    {
        // Issue #34: the sticky plain-text preference must survive a real INI write→read
        // (StubConfigService can't catch a mis-sectioned key like the calendar-settings bug above).
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.ReadAsPlainText = !config.ReadAsPlainText;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal(config.ReadAsPlainText, reloaded.ReadAsPlainText);
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        service.Save(service.Load());
        service.Save(service.Load()); // second save exercises the overwrite path

        var leftovers = Directory.GetFiles(profile.ProfileDir, "*.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Save_OverwritesExistingConfigCompletely()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        service.Save(config);
        service.Save(config);

        // The file must be valid, parseable config after repeated in-place saves.
        var reloaded = new ConfigService(profile).Load();
        Assert.NotNull(reloaded);
    }
}
