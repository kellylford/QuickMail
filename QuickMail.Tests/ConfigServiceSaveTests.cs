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
