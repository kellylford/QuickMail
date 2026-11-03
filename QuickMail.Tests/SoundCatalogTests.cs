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
    [InlineData("custom:NUL.wav")]
    [InlineData("custom:con.mp3")]
    [InlineData("custom:AUX.wav")]
    [InlineData("custom:COM1.wav")]
    [InlineData("custom:lpt9.mp3")]
    [InlineData("custom:COM¹.wav")]
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

    [Fact]
    public void EveryEventHasItsOwnSoundByDefault()
    {
        var none = new Dictionary<string, string>();
        var sounds = Enum.GetValues<SoundEvent>().Select(e => SoundCatalog.Resolve(e, none)).ToList();

        Assert.All(sounds, s => Assert.Equal(SoundRefKind.BuiltIn, s.Kind));
        Assert.Equal(sounds.Count, sounds.Distinct().Count());
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

    // ── Built-in sounds ────────────────────────────────────────────────────────

    public static TheoryData<string> BuiltInSounds => [.. SoundCatalog.BuiltInSounds.Distinct()];

    public static TheoryData<string> Packs => [.. SoundCatalog.Packs.Select(p => p.Id)];

    private static Stream OpenBuiltIn(string pack, string name) =>
        typeof(SoundService).Assembly.GetManifestResourceStream($"QuickMail.Sounds.BuiltIn.{pack}.{name}.wav")
        ?? throw new FileNotFoundException($"No embedded sound '{name}' in '{pack}'.");

    [Theory]
    [MemberData(nameof(BuiltInSounds))]
    public void EveryBuiltInSoundShipsAndDecodesInEveryStyle(string name)
    {
        foreach (var pack in SoundCatalog.Packs)
        {
            using var reader = NAudioSoundOutput.Open(new SoundClip(name, null, () => OpenBuiltIn(pack.Id, name)));
            Assert.InRange(reader.TotalTime, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            Assert.Equal(44100, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
        }
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void NoEmbeddedSoundIsLeftOverFromAnOldName(string pack)
    {
        var prefix = $"QuickMail.Sounds.BuiltIn.{pack}.";
        var embedded = typeof(SoundService).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..^".wav".Length]);
        Assert.Equal(SoundCatalog.BuiltInSounds.Distinct().Order(StringComparer.Ordinal), embedded.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BuiltInSounds))]
    public void ASoundRunsTheSameLengthInEveryStyle(string name)
    {
        var lengths = SoundCatalog.Packs.Select(pack =>
        {
            using var reader = NAudioSoundOutput.Open(new SoundClip(name, null, () => OpenBuiltIn(pack.Id, name)));
            return reader.Length;
        });
        Assert.Single(lengths.Distinct());
    }

    [Fact]
    public void EveryEmbeddedSoundBelongsToAStyle()
    {
        const string prefix = "QuickMail.Sounds.BuiltIn.";
        var packs = typeof(SoundService).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..].Split('.')[0]).Distinct();
        Assert.Equal(SoundCatalog.Packs.Select(p => p.Id).Order(StringComparer.Ordinal), packs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(".wav", typeof(FormatException))]
    [InlineData(".mp3", typeof(NAudio.MediaFoundation.MediaFoundationException))]
    public void AFileThatIsNotAudioIsRefusedWhenOpened(string extension, Type expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"QM-SND-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 37))]);
        try
        {
            var ex = Record.Exception(() => NAudioSoundOutput.Open(new SoundClip("junk", path, null)).Dispose());
            Assert.IsType(expected, ex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(31, true)]
    public void ASoundLongerThanTheLimitIsRefusedHoweverSmallTheFile(int seconds, bool refused)
    {
        var path = Path.Combine(Path.GetTempPath(), $"QM-SND-{Guid.NewGuid():N}.wav");
        var format = new NAudio.Wave.WaveFormat(8000, 8, 1);
        using (var writer = new NAudio.Wave.WaveFileWriter(path, format))
            writer.Write(new byte[8000 * seconds], 0, 8000 * seconds);
        try
        {
            var ex = Record.Exception(() => NAudioSoundOutput.Open(new SoundClip("long", path, null)).Dispose());
            if (refused) Assert.IsType<SoundTooLongException>(ex);
            else Assert.Null(ex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1, false)]   // Not present: locked or asleep. Nothing is being interrupted.
    [InlineData(2, true)]    // A full-screen app.
    [InlineData(3, true)]    // A full-screen game.
    [InlineData(4, true)]    // Presentation mode.
    [InlineData(5, false)]   // Accepts notifications.
    [InlineData(6, true)]    // Quiet time.
    [InlineData(7, true)]    // A full-screen Store app.
    [InlineData(0, false)]   // Not a value Windows defines.
    public void WindowsStatesThatHoldBackBackgroundSounds(int state, bool quiet) =>
        Assert.Equal(quiet, WindowsQuietState.IsQuietState(state));

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
        config.SoundPack = "wood";
        config.SoundEvents["NewMail"] = "custom:My Chime.wav";
        config.SoundEvents["MessageSent"] = "none";
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.True(reloaded.PlayEventSounds);
        Assert.True(reloaded.MuteSounds);
        Assert.Equal(40, reloaded.SoundVolume);
        Assert.Equal("wood", reloaded.SoundPack);
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
