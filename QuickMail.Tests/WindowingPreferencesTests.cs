using System;
using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Windowing preference persistence, deferred from PR #38 (issue #40).
///
/// These are round-trip tests on purpose. The failure this guards against is the one described in
/// ConfigServiceSaveTests: a property and a Settings writer exist, but ConfigService has no parse
/// case or no writer block, so the setting silently resets on every launch. A setting is not wired
/// up until it survives Save → Load in a fresh service.
/// </summary>
public class WindowingPreferencesTests
{
    private static ProfileContext MakeTempProfile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QM-WIN-{Guid.NewGuid():N}");
        return new ProfileContext(tempDir);
    }

    private static ConfigModel SaveThenReload(Action<ConfigModel> mutate)
    {
        var profile = MakeTempProfile();
        var config = new ConfigService(profile).Load();
        mutate(config);
        new ConfigService(profile).Save(config);
        return new ConfigService(profile).Load();
    }

    /// <summary>Writes a raw config.ini so the parse side can be tested independently of the writer.</summary>
    private static ConfigModel LoadFromIni(string body)
    {
        var profile = MakeTempProfile();
        File.WriteAllText(Path.Combine(profile.ProfileDir, "config.ini"), body);
        return new ConfigService(profile).Load();
    }

    // ── Defaults ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_AreReadingPaneAndConfirmOn()
    {
        var config = new ConfigModel();

        Assert.Equal(MessageOpenMode.ReadingPane, config.Windowing.MessageOpenMode);
        Assert.True(config.Windowing.ConfirmCloseTabWithUnsaved);
    }

    // ── MessageOpenMode round trip ───────────────────────────────────────────────

    [Theory]
    [InlineData(MessageOpenMode.ReadingPane)]
    [InlineData(MessageOpenMode.Tab)]
    [InlineData(MessageOpenMode.Window)]
    public void MessageOpenMode_RoundTrips(MessageOpenMode mode)
    {
        var reloaded = SaveThenReload(c => c.Windowing.MessageOpenMode = mode);

        Assert.Equal(mode, reloaded.Windowing.MessageOpenMode);
    }

    [Theory]
    [InlineData("tab",         MessageOpenMode.Tab)]
    [InlineData("TAB",         MessageOpenMode.Tab)]
    [InlineData("Tab",         MessageOpenMode.Tab)]
    [InlineData("window",      MessageOpenMode.Window)]
    [InlineData("WINDOW",      MessageOpenMode.Window)]
    [InlineData("readingpane", MessageOpenMode.ReadingPane)]
    public void MessageOpenMode_ParsesCaseInsensitively(string raw, MessageOpenMode expected)
    {
        var config = LoadFromIni($"[windowing]\nMessageOpenMode = {raw}\n");

        Assert.Equal(expected, config.Windowing.MessageOpenMode);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("reading pane")] // note the space: not the accepted spelling
    public void MessageOpenMode_UnknownValue_FallsBackToReadingPane(string raw)
    {
        // Falling back rather than throwing keeps a hand-edited config.ini from blocking startup.
        var config = LoadFromIni($"[windowing]\nMessageOpenMode = {raw}\n");

        Assert.Equal(MessageOpenMode.ReadingPane, config.Windowing.MessageOpenMode);
    }

    [Fact]
    public void MessageOpenMode_AbsentFromFile_IsReadingPane()
    {
        var config = LoadFromIni("[windowing]\n");

        Assert.Equal(MessageOpenMode.ReadingPane, config.Windowing.MessageOpenMode);
    }

    // ── ConfirmCloseTabWithUnsaved round trip ────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmCloseTabWithUnsaved_RoundTrips(bool value)
    {
        var reloaded = SaveThenReload(c => c.Windowing.ConfirmCloseTabWithUnsaved = value);

        Assert.Equal(value, reloaded.Windowing.ConfirmCloseTabWithUnsaved);
    }

    [Fact]
    public void ConfirmCloseTabWithUnsaved_OffSurvivesReload()
    {
        // Turning a confirmation OFF is the direction that matters: if it silently resets to on,
        // the user is re-prompted forever and reasonably concludes the setting does nothing.
        var reloaded = SaveThenReload(c => c.Windowing.ConfirmCloseTabWithUnsaved = false);

        Assert.False(reloaded.Windowing.ConfirmCloseTabWithUnsaved);
    }

    // ── Whole-section integrity ──────────────────────────────────────────────────

    [Fact]
    public void WindowingSettings_RoundTripTogether()
    {
        // Guards a writer that emits one key and drops its neighbour.
        var reloaded = SaveThenReload(c =>
        {
            c.Windowing.MessageOpenMode             = MessageOpenMode.Window;
            c.Windowing.ConfirmCloseTabWithUnsaved  = false;
        });

        Assert.Equal(MessageOpenMode.Window, reloaded.Windowing.MessageOpenMode);
        Assert.False(reloaded.Windowing.ConfirmCloseTabWithUnsaved);
    }

    [Fact]
    public void WindowingSettings_SurviveASecondSaveLoadCycle()
    {
        // A value that round-trips once but not twice means the writer emits a form its own parser
        // does not accept.
        var profile = MakeTempProfile();
        var first = new ConfigService(profile).Load();
        first.Windowing.MessageOpenMode            = MessageOpenMode.Tab;
        first.Windowing.ConfirmCloseTabWithUnsaved = false;
        new ConfigService(profile).Save(first);

        var second = new ConfigService(profile).Load();
        new ConfigService(profile).Save(second);
        var third = new ConfigService(profile).Load();

        Assert.Equal(MessageOpenMode.Tab, third.Windowing.MessageOpenMode);
        Assert.False(third.Windowing.ConfirmCloseTabWithUnsaved);
    }

    [Fact]
    public void SavingWindowingDoesNotDisturbOtherSettings()
    {
        var profile = MakeTempProfile();
        var config = new ConfigService(profile).Load();
        config.AnnounceHints                      = false;
        config.Windowing.MessageOpenMode          = MessageOpenMode.Tab;
        new ConfigService(profile).Save(config);

        var reloaded = new ConfigService(profile).Load();

        Assert.False(reloaded.AnnounceHints);
        Assert.Equal(MessageOpenMode.Tab, reloaded.Windowing.MessageOpenMode);
    }
}
