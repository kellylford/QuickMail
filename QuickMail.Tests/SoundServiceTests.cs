using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public sealed class SoundServiceTests : IDisposable
{
    /// <summary>Records what it is asked to play, and can be made to hold a sound open or to fail.</summary>
    private sealed class FakeOutput : ISoundOutput
    {
        public ConcurrentQueue<(SoundClip Clip, float Volume)> Played { get; } = new();
        public ManualResetEventSlim? Hold { get; set; }
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Cancelled { get; } = new();
        public Exception? Failure { get; set; }

        /// <summary>Fails this one sound only, so the sound played after it can be seen.</summary>
        public string? FailOnlyFor { get; set; }

        /// <summary>Behaves like a stuck audio driver: takes no notice of cancellation.</summary>
        public bool IgnoresCancellation { get; set; }

        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
        {
            Played.Enqueue((clip, volume));
            Entered.Set();
            try
            {
                if (IgnoresCancellation) Hold?.Wait(CancellationToken.None);
                else Hold?.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.Set();
                throw;
            }
            if (Failure is not null) throw Failure;
            if (clip.Key == FailOnlyFor) throw new FormatException("Not a WAVE file");
        }

        public Exception? CheckFailure { get; set; }

        public void Check(string filePath)
        {
            if (CheckFailure is not null) throw CheckFailure;
        }
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"QM-SNDS-{Guid.NewGuid():N}");
    private readonly StubConfigService _config = new();
    private readonly FakeOutput _output = new();
    private DateTime _now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private bool _quiet;
    private SoundService? _service;

    public SoundServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _output.Hold?.Set();
        _service?.Dispose();
        Directory.Delete(_folder, recursive: true);
        foreach (var folder in _sourceFolders) Directory.Delete(folder, recursive: true);
    }

    private SoundService Service() => _service ??= new SoundService(_config, _folder, _output,
        openBuiltIn: (_, _) => new MemoryStream(), isQuiet: () => _quiet, utcNow: () => _now);

    private ConfigModel Settings() => _config.Load();

    /// <summary>Waits for the worker to have played exactly this many, then checks no more arrive.</summary>
    private void AssertPlayed(params string[] keys)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_output.Played.Count < keys.Length && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        Thread.Sleep(250);   // Long enough for a sound wrongly queued to have reached the worker.
        Assert.Equal(keys, _output.Played.Select(p => p.Clip.Key));
    }

    [Fact]
    public void PreviewingAnEventPlaysItWithSoundsOffAndMuted()
    {
        Settings().PlayEventSounds = false;
        Settings().MuteSounds = true;
        Service().PreviewEvent(SoundEvent.NewMail);
        AssertPlayed("builtin:arrived");
    }

    [Fact]
    public void PreviewingAnEventUsesTheSavedVolume()
    {
        Settings().SoundVolume = 30;
        Service().PreviewEvent(SoundEvent.NewMail);
        AssertPlayed("builtin:arrived");
        Assert.Equal(0.3f, _output.Played.Single().Volume, 0.001f);
    }

    [Fact]
    public void PreviewingAnEventWithNoSoundStopsWhatIsPlaying()
    {
        Settings().ActiveSoundEvents()["MessageSent"] = "none";
        _output.Hold = new ManualResetEventSlim();
        Service().PreviewEvent(SoundEvent.NewMail);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Service().PreviewEvent(SoundEvent.MessageSent);

        Assert.True(_output.Cancelled.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        AssertPlayed("builtin:arrived");
    }

    [Fact]
    public void PreviewingAnEventWhoseFileIsGonePlaysThatEventsBuiltInSound()
    {
        // Not the rule sound Preview falls back to: this has to sound as the event really would.
        Settings().ActiveSoundEvents()["MessageSent"] = SoundRef.Custom("gone.wav").ToStored();
        Service().PreviewEvent(SoundEvent.MessageSent);
        AssertPlayed("builtin:sent");
    }

    [Fact]
    public void PreviewingAnEventWhoseFileWontPlayFallsBackToItsBuiltInSound()
    {
        var stored = SoundRef.Custom("bad.wav").ToStored();
        var folder = Path.Combine(_folder, SoundCatalog.DefaultPack);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "bad.wav"), "not a sound");
        Settings().ActiveSoundEvents()["MessageSent"] = stored;
        _output.FailOnlyFor = stored;

        Service().PreviewEvent(SoundEvent.MessageSent);

        AssertPlayed(stored, "builtin:sent");
    }

    [Fact]
    public void EventSoundsAreSilentUntilTurnedOn()
    {
        Service().Play(SoundEvent.MessageSent);
        AssertPlayed();
    }

    [Fact]
    public void ATurnedOnEventPlaysItsBuiltInSound()
    {
        Settings().PlayEventSounds = true;
        Service().Play(SoundEvent.MessageSent);
        AssertPlayed("builtin:sent");
    }

    [Fact]
    public void AChoiceOfNoSoundPlaysNothing()
    {
        Settings().PlayEventSounds = true;
        Settings().ActiveSoundEvents()["MessageSent"] = "none";
        Service().Play(SoundEvent.MessageSent);
        AssertPlayed();
    }

    [Fact]
    public void ARuleSoundPlaysWithEventSoundsOff()
    {
        Service().PlayRule("builtin:reminder");
        AssertPlayed("builtin:reminder");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"custom:..\..\evil.wav")]
    public void AnUnreadableRuleSoundPlaysTheDefault(string? stored)
    {
        Service().PlayRule(stored);
        AssertPlayed("builtin:" + SoundCatalog.DefaultRuleSound);
    }

    [Fact]
    public void ARuleToldToPlayNoSoundPlaysTheDefaultAsTheEditorHasNoNone()
    {
        Service().PlayRule("none");
        AssertPlayed("builtin:" + SoundCatalog.DefaultRuleSound);
    }

    [Fact]
    public void ARuleRecordsWhichMessagesItSoundedFor()
    {
        var key = SoundCatalog.MessageKey(Guid.NewGuid(), "7");
        Assert.False(Service().WasSoundedByRule(key));
        Service().PlayRule("builtin:matched", [key]);
        Assert.True(Service().WasSoundedByRule(key));
    }

    [Fact]
    public void MuteSilencesEventsAndRules()
    {
        Settings().PlayEventSounds = true;
        Settings().MuteSounds = true;
        Service().Play(SoundEvent.MessageSent);
        Service().PlayRule("builtin:matched");
        AssertPlayed();
    }

    [Fact]
    public void APreviewPlaysEvenWhenMuted()
    {
        Settings().MuteSounds = true;
        Service().Preview("builtin:arrived");
        AssertPlayed("builtin:arrived");
    }

    [Fact]
    public void APreviewOfNothingPlaysNothing()
    {
        Service().Preview("none");
        Service().Preview("not a sound");
        AssertPlayed();
    }

    [Fact]
    public void APreviewCutsOffWhatIsPlayingAndClearsWhatIsWaiting()
    {
        Settings().PlayEventSounds = true;
        _output.Hold = new ManualResetEventSlim();
        Service().Play(SoundEvent.MessageSent);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));
        Service().Play(SoundEvent.SendFailed);

        Service().Preview("builtin:arrived");

        AssertPlayed("builtin:sent", "builtin:arrived");
    }

    [Fact]
    public void QuietHoldsBackBackgroundSoundsOnly()
    {
        Settings().PlayEventSounds = true;
        _quiet = true;
        Service().Play(SoundEvent.ConnectionLost);
        Service().Play(SoundEvent.NewMail);
        Service().PlayRule("builtin:matched");
        Service().Play(SoundEvent.MessageSent);
        AssertPlayed("builtin:sent");
    }

    [Fact]
    public void ARuleSoundHoldsBackTheNewMailSoundBehindIt()
    {
        Settings().PlayEventSounds = true;
        Service().PlayRule("builtin:matched");
        Service().Play(SoundEvent.NewMail);
        AssertPlayed("builtin:matched");
    }

    [Fact]
    public void ABurstIsCappedWhileOneIsPlaying()
    {
        Settings().PlayEventSounds = true;
        _output.Hold = new ManualResetEventSlim();
        Service().Play(SoundEvent.MessageSent);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));

        foreach (var e in new[] { SoundEvent.SendFailed, SoundEvent.MessageQueued, SoundEvent.ActionFailed, SoundEvent.ConnectionLost, SoundEvent.ConnectionRestored })
            Service().Play(e);
        _output.Hold.Set();

        AssertPlayed("builtin:sent", "builtin:failed", "builtin:queued", "builtin:error");
    }

    [Theory]
    [InlineData(100, 1f)]
    [InlineData(40, 0.4f)]
    public void TheVolumeSettingReachesTheOutput(int setting, float expected)
    {
        Settings().PlayEventSounds = true;
        Settings().SoundVolume = setting;
        Service().Play(SoundEvent.MessageSent);
        AssertPlayed("builtin:sent");
        Assert.Equal(expected, _output.Played.Single().Volume, precision: 3);
    }

    [Fact]
    public void ZeroVolumeSkipsTheSoundAndLeavesTheToastItsOwn()
    {
        Settings().PlayEventSounds = true;
        Settings().SoundVolume = 0;
        Service().Play(SoundEvent.MessageSent);
        AssertPlayed();
        Assert.False(Service().IsAudible(SoundEvent.MessageSent));
    }

    [Fact]
    public void ACustomFilePlaysFromTheSchemesFolder()
    {
        // Glass is the scheme in use, so its own folder (#948).
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "glass")).FullName, "My Chime.wav");
        File.WriteAllBytes(path, [1, 2, 3]);
        Settings().PlayEventSounds = true;
        Settings().ActiveSoundEvents()["NewMail"] = "custom:My Chime.wav";

        Service().Play(SoundEvent.NewMail);

        AssertPlayed("custom:My Chime.wav");
        Assert.Equal(path, _output.Played.Single().Clip.FilePath);
    }

    [Fact]
    public void AMissingCustomFileFallsBackToTheBuiltInSound()
    {
        Settings().PlayEventSounds = true;
        Settings().ActiveSoundEvents()["NewMail"] = "custom:gone.wav";
        Service().Play(SoundEvent.NewMail);
        AssertPlayed("builtin:arrived");
    }

    // ── Adding a chosen file ───────────────────────────────────────────────────

    private string SourceFile(string name, byte[] bytes)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"QM-SRC-{Guid.NewGuid():N}")).FullName;
        _sourceFolders.Add(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private readonly List<string> _sourceFolders = [];

    [Fact]
    public void AChosenFileIsCopiedInAndTheOriginalLeftAlone()
    {
        var source = SourceFile("My Chime.wav", [1, 2, 3]);

        Assert.True(Service().TryAddCustomSound(null,source, out var name, out _));

        Assert.Equal("My Chime.wav", name);
        Assert.Equal([(byte)1, 2, 3], File.ReadAllBytes(Path.Combine(_folder, name)));
        Assert.True(File.Exists(source));
        Assert.Equal(["My Chime.wav"], Service().CustomSoundFiles(null));
    }

    [Fact]
    public void ChoosingTheSameFileAgainUsesTheCopyAlreadyThere()
    {
        Service().TryAddCustomSound(null,SourceFile("Chime.wav", [1]), out _, out _);
        Assert.True(Service().TryAddCustomSound(null,SourceFile("Chime.wav", [1]), out var name, out _));
        Assert.Equal("Chime.wav", name);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void ADifferentFileWithTheSameNameDoesNotReplaceTheOneInUse()
    {
        Service().TryAddCustomSound(null,SourceFile("Chime.wav", [1]), out _, out _);
        Assert.True(Service().TryAddCustomSound(null,SourceFile("Chime.wav", [2]), out var name, out _));
        Assert.Equal("Chime 2.wav", name);
        Assert.Equal([(byte)1], File.ReadAllBytes(Path.Combine(_folder, "Chime.wav")));
    }

    [Fact]
    public void AFileWhoseNameCannotBeKeptGetsAPlainName()
    {
        var source = SourceFile(new string('x', 110) + ".wav", [1]);
        Assert.True(Service().TryAddCustomSound(null,source, out var name, out _));
        Assert.Equal("Custom sound.wav", name);
    }

    [Fact]
    public void NumberingANameDoesNotPushItPastTheLimit()
    {
        var longName = new string('x', 96) + ".wav";   // 100 characters, the most allowed.
        Service().TryAddCustomSound(null,SourceFile(longName, [1]), out _, out _);

        Assert.True(Service().TryAddCustomSound(null,SourceFile(longName, [2]), out var name, out _));

        Assert.NotEqual(longName, name);
        Assert.True(SoundRef.IsSafeCustomFileName(name), $"{name} would be refused when next read.");
    }

    [Theory]
    [InlineData(0)]    // Empty.
    [InlineData(10)]   // Cut off inside the header.
    public void ADamagedFileIsCalledDamagedWithTheRealDecoder(int bytes)
    {
        using var service = new SoundService(_config, _folder, new NAudioSoundOutput());
        Assert.False(service.TryAddCustomSound(null,SourceFile("short.wav", new byte[bytes]), out _, out var refusal));
        Assert.Contains("could not be played", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealSoundIsAcceptedByTheRealDecoder()
    {
        using var stream = SoundService.OpenBuiltInResource(SoundCatalog.DefaultPack, "arrived");
        using var copy = new MemoryStream();
        stream!.CopyTo(copy);
        using var service = new SoundService(_config, _folder, new NAudioSoundOutput());

        Assert.True(service.TryAddCustomSound(null,SourceFile("Mine.wav", copy.ToArray()), out var name, out _));

        Assert.Equal("Mine.wav", name);
    }

    // ── Stopping, previewing, falling back ─────────────────────────────────────

    [Fact]
    public void StopEndsWhatIsPlayingAndWhatIsWaiting()
    {
        Settings().PlayEventSounds = true;
        _output.Hold = new ManualResetEventSlim();
        Service().Play(SoundEvent.MessageSent);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));
        Service().Play(SoundEvent.SendFailed);

        Service().StopAll();

        Assert.True(_output.Cancelled.Wait(TimeSpan.FromSeconds(5)));
        AssertPlayed("builtin:sent");
    }

    [Fact]
    public void PreviewingNoSoundStopsTheLastPreview()
    {
        _output.Hold = new ManualResetEventSlim();
        Service().Preview("builtin:arrived");
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));

        Service().Preview("none");

        Assert.True(_output.Cancelled.Wait(TimeSpan.FromSeconds(5)));
        AssertPlayed("builtin:arrived");
    }

    [Fact]
    public void APreviewPlaysAtTheVolumeAskedFor()
    {
        Settings().SoundVolume = 100;
        Service().Preview("builtin:arrived", 20);
        AssertPlayed("builtin:arrived");
        Assert.Equal(0.2f, _output.Played.Single().Volume, precision: 3);
    }

    [Fact]
    public void ACustomFileThatWillNotPlayFallsBackToTheBuiltInSound()
    {
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "glass")).FullName, "Bad.wav"), [1]);
        Settings().PlayEventSounds = true;
        Settings().ActiveSoundEvents()["NewMail"] = "custom:Bad.wav";
        _output.FailOnlyFor = "custom:Bad.wav";

        Service().Play(SoundEvent.NewMail);

        AssertPlayed("custom:Bad.wav", "builtin:arrived");
    }

    [Fact]
    public void ABuiltInSoundThatFailsHasNothingToFallBackTo()
    {
        Settings().PlayEventSounds = true;
        _output.Failure = new InvalidOperationException("no audio device");
        Service().Play(SoundEvent.NewMail);
        AssertPlayed("builtin:arrived");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ToastsAreSilentWhenSoundsAreOnOrMuted(bool on, bool muted, bool silent)
    {
        Settings().PlayEventSounds = on;
        Settings().MuteSounds = muted;
        Assert.Equal(silent, Service().SilencesToasts);
    }

    [Fact]
    public void AToastStaysSilentForAnEventSetToNone()
    {
        Settings().PlayEventSounds = true;
        Settings().ActiveSoundEvents()["NewMail"] = "none";
        Assert.True(Service().SilencesToasts);
        Assert.False(Service().IsAudible(SoundEvent.NewMail));
    }

    [Fact]
    public void AnEventThatHappenedOnItsOwnStaysQuietEvenIfItUsuallyAnswersThePerson()
    {
        Settings().PlayEventSounds = true;
        _quiet = true;
        Service().Play(SoundEvent.MessageSent, background: true);
        Service().Play(SoundEvent.SendFailed);
        AssertPlayed("builtin:failed");
    }

    [Fact]
    public void AnotherKindOfFileIsRefused()
    {
        var source = SourceFile("notes.txt", [1]);
        Assert.False(Service().TryAddCustomSound(null,source, out _, out var refusal));
        Assert.Contains("WAV and MP3", refusal, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void AFileOverTheSizeLimitIsRefusedWithoutDecodingIt()
    {
        _output.CheckFailure = new InvalidOperationException("should not have been decoded");
        var source = SourceFile("big.wav", new byte[SoundCatalog.MaxCustomFileBytes + 1]);
        Assert.False(Service().TryAddCustomSound(null,source, out _, out var refusal));
        Assert.Contains("larger than 5 MB", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatWillNotDecodeIsRefused()
    {
        _output.CheckFailure = new FormatException("Not a WAVE file");
        Assert.False(Service().TryAddCustomSound(null,SourceFile("junk.wav", [1]), out var name, out var refusal));
        Assert.Empty(name);
        Assert.Contains("could not be played", refusal, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void ASoundThatRunsTooLongIsRefusedSayingSo()
    {
        _output.CheckFailure = new SoundTooLongException("too long");
        Assert.False(Service().TryAddCustomSound(null,SourceFile("song.wav", [1]), out _, out var refusal));
        Assert.Contains("longer than 30 seconds", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatCannotBeReadIsRefusedRatherThanThrown()
    {
        Assert.False(Service().TryAddCustomSound(null,Path.Combine(_folder, "not there.wav"), out _, out var refusal));
        Assert.False(string.IsNullOrEmpty(refusal));
    }

    [Fact]
    public void AnOutputFailureNeverReachesTheCallerAndLaterSoundsStillPlay()
    {
        Settings().PlayEventSounds = true;
        _output.Failure = new InvalidOperationException("no audio device");
        Service().Play(SoundEvent.MessageSent);
        Service().Play(SoundEvent.SendFailed);
        AssertPlayed("builtin:sent", "builtin:failed");
    }

    [Fact]
    public void SoundsAfterDisposeAreIgnored()
    {
        Settings().PlayEventSounds = true;
        var service = Service();
        service.Dispose();
        service.Play(SoundEvent.MessageSent);
        service.PlayRule("builtin:matched");
        service.Preview("builtin:arrived");
        AssertPlayed();
    }

    [Fact]
    public void DisposeIsSafeTwiceAndCutsOffTheSoundPlaying()
    {
        Settings().PlayEventSounds = true;
        _output.Hold = new ManualResetEventSlim();
        var service = Service();
        service.Play(SoundEvent.MessageSent);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));

        service.Dispose();
        service.Dispose();

        Assert.True(_output.Cancelled.IsSet);
    }

    [Fact]
    public void AStuckAudioDriverCannotHoldUpExit()
    {
        Settings().PlayEventSounds = true;
        _output.Hold = new ManualResetEventSlim();
        _output.IgnoresCancellation = true;
        var service = Service();
        service.Play(SoundEvent.MessageSent);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        service.Dispose();

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void MutingCutsOffTheSoundPlaying()
    {
        Settings().PlayEventSounds = true;
        _output.Hold = new ManualResetEventSlim();
        Service().Play(SoundEvent.MessageSent);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));

        Settings().MuteSounds = true;

        Assert.True(_output.Cancelled.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void MuteDoesNotCutOffAPreview()
    {
        Settings().MuteSounds = true;
        _output.Hold = new ManualResetEventSlim();
        Service().Preview("builtin:arrived");
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(_output.Cancelled.Wait(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void EveryBuiltInSoundOpensThroughTheServicesOwnLookup()
    {
        foreach (var name in SoundCatalog.BuiltInSounds.Distinct())
        {
            using var stream = SoundService.OpenBuiltInResource(SoundCatalog.DefaultPack, name);
            Assert.NotNull(stream);
            Assert.True(stream.Length > 0);
        }
    }

    [Fact]
    public void EveryStyleHasEverySoundAndTheyDiffer()
    {
        foreach (var name in SoundCatalog.BuiltInSounds.Distinct())
        {
            var contents = new HashSet<string>();
            foreach (var pack in SoundCatalog.Packs)
            {
                using var stream = SoundService.OpenBuiltInResource(pack.Id, name);
                Assert.True(stream is not null, $"{pack.Id} has no sound '{name}'.");
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                contents.Add(Convert.ToHexString(copy.ToArray()));
            }
            Assert.Equal(SoundCatalog.Packs.Count, contents.Count);
        }
    }

    [Fact]
    public void TheChosenStyleIsWhatPlays()
    {
        var asked = new List<string>();
        Settings().PlayEventSounds = true;
        Settings().SoundSchemeId = "wood";
        using var service = new SoundService(_config, _folder, new OpeningOutput(_output), openBuiltIn: (pack, _) =>
        {
            lock (asked) asked.Add(pack);
            return new MemoryStream();
        });

        service.Play(SoundEvent.MessageSent);

        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));
        lock (asked) Assert.Equal(["wood"], asked);
    }

    [Fact]
    public void APreviewPlaysInTheStyleAskedFor()
    {
        var asked = new List<string>();
        Settings().SoundSchemeId = "wood";
        using var service = new SoundService(_config, _folder, new OpeningOutput(_output), openBuiltIn: (pack, _) =>
        {
            lock (asked) asked.Add(pack);
            return new MemoryStream();
        });

        service.Preview("builtin:arrived", null, "softbell");

        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));
        lock (asked) Assert.Equal(["softbell"], asked);
    }

    /// <summary>Opens each clip, as the real output does, then hands over to the fake.</summary>
    private sealed class OpeningOutput(FakeOutput inner) : ISoundOutput
    {
        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
        {
            using var stream = clip.OpenStream?.Invoke();
            inner.Play(clip, volume, cancellationToken);
        }

        public void Check(string filePath) => inner.Check(filePath);
    }

    [Fact]
    public void AnUnknownPackFallsBackToTheDefaultPack()
    {
        Settings().PlayEventSounds = true;
        Settings().SoundSchemeId = "no such pack";
        using var service = new SoundService(_config, _folder, _output);
        service.Play(SoundEvent.MessageSent);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));

        _output.Played.TryPeek(out var played);
        using var stream = played.Clip.OpenStream!();
        Assert.True(stream.Length > 0);
    }

    [Theory]
    [InlineData(false, false, 100, null, false)]   // Event sounds off.
    [InlineData(true, true, 100, null, false)]     // Muted.
    [InlineData(true, false, 0, null, false)]      // No volume.
    [InlineData(true, false, 100, "none", false)]  // This event turned off.
    [InlineData(true, false, 100, null, true)]
    public void IsAudibleSaysWhetherTheToastShouldBeSilent(bool on, bool muted, int volume, string? choice, bool expected)
    {
        Settings().PlayEventSounds = on;
        Settings().MuteSounds = muted;
        Settings().SoundVolume = volume;
        if (choice is not null) Settings().ActiveSoundEvents()["NewMail"] = choice;

        Assert.Equal(expected, Service().IsAudible(SoundEvent.NewMail));
    }
}
