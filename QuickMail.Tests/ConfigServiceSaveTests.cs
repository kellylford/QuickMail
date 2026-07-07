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
    public void SaveThenLoad_RoundTripsUILanguageAndCalendarSettings()
    {
        // Regression test: UILanguage, ShowDeclinedEvents, and CalendarPaneOpen were being
        // written by Save() after the "[windowing]" section header, so ParseFile silently
        // dropped them on the next Load() (they're only recognized under "[global]"). This
        // meant changing the UI language in Settings appeared to do nothing after a restart.
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.UILanguage = "de";
        config.ShowDeclinedEvents = !config.ShowDeclinedEvents;
        config.CalendarPaneOpen = !config.CalendarPaneOpen;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal("de", reloaded.UILanguage);
        Assert.Equal(config.ShowDeclinedEvents, reloaded.ShowDeclinedEvents);
        Assert.Equal(config.CalendarPaneOpen, reloaded.CalendarPaneOpen);
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
