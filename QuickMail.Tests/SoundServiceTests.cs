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
        public Exception? Failure { get; set; }

        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
        {
            Played.Enqueue((clip, volume));
            Entered.Set();
            Hold?.Wait(cancellationToken);
            if (Failure is not null) throw Failure;
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
        Thread.Sleep(50);
        Assert.Equal(keys, _output.Played.Select(p => p.Clip.Key));
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
        Settings().SoundEvents["MessageSent"] = "none";
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
    public void ACustomFilePlaysFromTheSoundsFolder()
    {
        var path = Path.Combine(_folder, "My Chime.wav");
        File.WriteAllBytes(path, [1, 2, 3]);
        Settings().PlayEventSounds = true;
        Settings().SoundEvents["NewMail"] = "custom:My Chime.wav";

        Service().Play(SoundEvent.NewMail);

        AssertPlayed("custom:My Chime.wav");
        Assert.Equal(path, _output.Played.Single().Clip.FilePath);
    }

    [Fact]
    public void AMissingCustomFileFallsBackToTheBuiltInSound()
    {
        Settings().PlayEventSounds = true;
        Settings().SoundEvents["NewMail"] = "custom:gone.wav";
        Service().Play(SoundEvent.NewMail);
        AssertPlayed("builtin:arrived");
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
    public void DisposeIsSafeTwiceAndEndsTheWorkerMidSound()
    {
        Settings().PlayEventSounds = true;
        _output.Hold = new ManualResetEventSlim();
        var service = Service();
        service.Play(SoundEvent.MessageSent);
        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));

        service.Dispose();
        service.Dispose();
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
        if (choice is not null) Settings().SoundEvents["NewMail"] = choice;

        Assert.Equal(expected, Service().IsAudible(SoundEvent.NewMail));
    }
}
