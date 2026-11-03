using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class SoundCatalogTests
{
    // ── SoundRef ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("none", SoundRefKind.None, "")]
    [InlineData(" NONE ", SoundRefKind.None, "")]
    [InlineData("builtin:sent", SoundRefKind.BuiltIn, "sent")]
    [InlineData("custom:My Chime.wav", SoundRefKind.Custom, "My Chime.wav")]
    [InlineData("custom:song.MP3", SoundRefKind.Custom, "song.MP3")]
    public void ValidValuesParse(string text, SoundRefKind kind, string name)
    {
        Assert.True(SoundRef.TryParse(text, out var sound));
        Assert.Equal(kind, sound.Kind);
        Assert.Equal(name, sound.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sent")]
    [InlineData("builtin:")]
    [InlineData("builtin:no-such-sound")]
    [InlineData("builtin:../sent")]
    [InlineData("custom:")]
    [InlineData("custom:..")]
    [InlineData("custom:noextension")]
    [InlineData("custom:virus.exe")]
    [InlineData("custom:.wav.exe")]
    [InlineData(@"custom:..\..\secret.wav")]
    [InlineData("custom:../../secret.wav")]
    [InlineData(@"custom:C:\Windows\Media\tada.wav")]
    [InlineData(@"custom:\\server\share\x.wav")]
    [InlineData("custom:C:x.wav")]
    [InlineData("custom:file.wav:stream.wav")]
    [InlineData("custom:ends-with-dot.wav.")]
    public void UnsafeOrUnknownValuesAreRefused(string? text)
    {
        Assert.False(SoundRef.TryParse(text, out var sound));
        Assert.Equal(SoundRef.None, sound);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("builtin:arrived")]
    [InlineData("custom:My Chime.wav")]
    public void StoredFormRoundTrips(string text)
    {
        Assert.True(SoundRef.TryParse(text, out var sound));
        Assert.Equal(text, sound.ToStored());
    }

    // ── Catalog ────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryEventIsInTheCatalogOnce()
    {
        var listed = SoundCatalog.Events.Select(e => e.Event).ToList();
        Assert.Equal(Enum.GetValues<SoundEvent>().OrderBy(e => e), listed.OrderBy(e => e));
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Equal(listed.Count, SoundCatalog.Events.Select(e => e.ConfigKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(SoundEvent.MessageDeleted)]
    [InlineData(SoundEvent.MessageArchived)]
    [InlineData(SoundEvent.MessageMoved)]
    public void ActionsThatRepeatQuicklyAreOffUntilChosen(SoundEvent e)
    {
        Assert.Equal(SoundRef.None, SoundCatalog.Resolve(e, new Dictionary<string, string>()));

        var chosen = new Dictionary<string, string> { [SoundCatalog.Info(e).ConfigKey] = "builtin:removed" };
        Assert.Equal(SoundRef.BuiltIn("removed"), SoundCatalog.Resolve(e, chosen));
    }

    [Fact]
    public void AnEventWithNoStoredChoiceUsesItsBuiltInSound() =>
        Assert.Equal(SoundRef.BuiltIn("arrived"), SoundCatalog.Resolve(SoundEvent.NewMail, new Dictionary<string, string>()));

    [Fact]
    public void AnEventCanBeTurnedOffOnItsOwn()
    {
        var stored = new Dictionary<string, string> { ["NewMail"] = "none" };
        Assert.Equal(SoundRef.None, SoundCatalog.Resolve(SoundEvent.NewMail, stored));
    }

    [Fact]
    public void AnUnreadableStoredChoiceFallsBackRatherThanSilencingTheEvent()
    {
        var stored = new Dictionary<string, string> { ["NewMail"] = @"custom:..\..\x.wav" };
        Assert.Equal(SoundRef.BuiltIn("arrived"), SoundCatalog.Resolve(SoundEvent.NewMail, stored));
    }

    // ── Config ─────────────────────────────────────────────────────────────────

    private static ProfileContext MakeTempProfile() =>
        new(Path.Combine(Path.GetTempPath(), $"QM-SND-{Guid.NewGuid():N}"));

    [Fact]
    public void EventSoundsAreOffAndNothingIsMutedByDefault()
    {
        var config = new ConfigService(MakeTempProfile()).Load();
        Assert.False(config.PlayEventSounds);
        Assert.False(config.MuteSounds);
        Assert.Equal(100, config.SoundVolume);
        Assert.Equal(SoundCatalog.DefaultPack, config.SoundPack);
        Assert.Empty(config.SoundEvents);
    }

    [Fact]
    public void SoundSettingsSurviveARestart()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);
        var config = service.Load();
        config.PlayEventSounds = true;
        config.MuteSounds = true;
        config.SoundVolume = 40;
        config.SoundEvents["NewMail"] = "custom:My Chime.wav";
        config.SoundEvents["MessageSent"] = "none";
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.True(reloaded.PlayEventSounds);
        Assert.True(reloaded.MuteSounds);
        Assert.Equal(40, reloaded.SoundVolume);
        Assert.Equal("custom:My Chime.wav", reloaded.SoundEvents["NewMail"]);
        Assert.Equal("none", reloaded.SoundEvents["MessageSent"]);
    }

    [Fact]
    public void GlobalSettingsAfterTheSoundsSectionStillLoad()
    {
        // The scalar settings are [global]: written under [windowing] they would silently reset on
        // the next load, which has happened before in this file.
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);
        var config = service.Load();
        config.SoundEvents["NewMail"] = "none";
        config.Windowing.ConfirmCloseTabWithUnsaved = !config.Windowing.ConfirmCloseTabWithUnsaved;
        config.ShowDeclinedEvents = true;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal(config.Windowing.ConfirmCloseTabWithUnsaved, reloaded.Windowing.ConfirmCloseTabWithUnsaved);
        Assert.True(reloaded.ShowDeclinedEvents);
        Assert.Equal("none", reloaded.SoundEvents["NewMail"]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("loud")]
    public void AnOutOfRangeVolumeKeepsTheDefault(string value)
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);
        service.Save(service.Load());
        var path = Path.Combine(profile.ProfileDir, "config.ini");
        File.WriteAllText(path, File.ReadAllText(path).Replace("SoundVolume = 100", $"SoundVolume = {value}", StringComparison.Ordinal));

        Assert.Equal(100, new ConfigService(profile).Load().SoundVolume);
    }

    [Fact]
    public void ZeroVolumeIsAllowed()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);
        var config = service.Load();
        config.SoundVolume = 0;
        service.Save(config);
        Assert.Equal(0, new ConfigService(profile).Load().SoundVolume);
    }
}
