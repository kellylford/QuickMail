using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #923: Up or Home at the top of the message list, or Down or End at the bottom, moves
/// nothing, so nothing was said. The decision is <see cref="ListEdgeCue"/>; the cue is
/// <see cref="MainViewModel.SignalListEdge"/>: the "Reached the end of a list" sound when it can start
/// at once, else "Top of list" / "Bottom of list", said as a result without interrupting.
/// <see cref="ListEdgePositionTests"/> covers where the focused row is.
/// </summary>
public sealed class ListEdgeCueTests
{
    // ── Is this key at the edge? ───────────────────────────────────────────────

    [Theory]
    [InlineData(Key.Up, 0, 5, ListEdge.Top)]
    [InlineData(Key.Home, 0, 5, ListEdge.Top)]
    [InlineData(Key.Down, 4, 5, ListEdge.Bottom)]
    [InlineData(Key.End, 4, 5, ListEdge.Bottom)]
    [InlineData(Key.Up, 1, 5, ListEdge.None)]      // moves up
    [InlineData(Key.Home, 3, 5, ListEdge.None)]    // moves to the top
    [InlineData(Key.Down, 0, 5, ListEdge.None)]    // moves down
    [InlineData(Key.End, 3, 5, ListEdge.None)]     // moves to the bottom
    [InlineData(Key.Down, 0, 1, ListEdge.Bottom)]  // one row: it is both ends
    [InlineData(Key.Up, 0, 1, ListEdge.Top)]
    [InlineData(Key.PageUp, 0, 5, ListEdge.None)]  // Page keys are left alone
    [InlineData(Key.PageDown, 4, 5, ListEdge.None)]
    [InlineData(Key.Left, 0, 5, ListEdge.None)]
    [InlineData(Key.Right, 4, 5, ListEdge.None)]
    [InlineData(Key.Enter, 0, 5, ListEdge.None)]
    public void APlainKeyMeetsAnEdgeOnlyWhenItWouldNotMove(Key key, int index, int count, ListEdge expected) =>
        Assert.Equal(expected, ListEdgeCue.At(key, ModifierKeys.None, index, count));

    [Theory]
    [InlineData(ModifierKeys.Shift)]     // extends the selection
    [InlineData(ModifierKeys.Control)]   // moves focus without selecting
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void AKeyWithAModifierIsNeverAnEdge(ModifierKeys modifiers)
    {
        Assert.Equal(ListEdge.None, ListEdgeCue.At(Key.Up, modifiers, 0, 5));
        Assert.Equal(ListEdge.None, ListEdgeCue.At(Key.End, modifiers, 4, 5));
    }

    [Theory]
    [InlineData(-1, 5)]   // no focused row
    [InlineData(0, 0)]    // empty list
    [InlineData(5, 5)]    // a stale index past the end
    public void NoRowOrNoListIsNoEdge(int index, int count)
    {
        Assert.Equal(ListEdge.None, ListEdgeCue.At(Key.Up, ModifierKeys.None, index, count));
        Assert.Equal(ListEdge.None, ListEdgeCue.At(Key.Down, ModifierKeys.None, index, count));
    }

    // ── Repeats ────────────────────────────────────────────────────────────────

    [Fact]
    public void EverySeparatePressAtTheEdgeCuesAgain()
    {
        var cue = new ListEdgeCue();
        Assert.Equal(ListEdge.Top, cue.Next(Key.Up, ModifierKeys.None, 0, 5, isRepeat: false));
        Assert.Equal(ListEdge.Top, cue.Next(Key.Up, ModifierKeys.None, 0, 5, isRepeat: false));
        Assert.Equal(ListEdge.Top, cue.Next(Key.Home, ModifierKeys.None, 0, 5, isRepeat: false));
    }

    [Fact]
    public void AKeyHeldAtTheEdgeCuesOnceNotOnEveryRepeat()
    {
        var cue = new ListEdgeCue();
        Assert.Equal(ListEdge.Top, cue.Next(Key.Up, ModifierKeys.None, 0, 5, isRepeat: false));
        Assert.Equal(ListEdge.None, cue.Next(Key.Up, ModifierKeys.None, 0, 5, isRepeat: true));
        Assert.Equal(ListEdge.None, cue.Next(Key.Up, ModifierKeys.None, 0, 5, isRepeat: true));
    }

    [Fact]
    public void AKeyHeldUntilItReachesTheEdgeCuesWhenItGetsThere()
    {
        var cue = new ListEdgeCue();
        Assert.Equal(ListEdge.None, cue.Next(Key.Down, ModifierKeys.None, 2, 5, isRepeat: false));
        Assert.Equal(ListEdge.None, cue.Next(Key.Down, ModifierKeys.None, 3, 5, isRepeat: true));
        Assert.Equal(ListEdge.Bottom, cue.Next(Key.Down, ModifierKeys.None, 4, 5, isRepeat: true));
        Assert.Equal(ListEdge.None, cue.Next(Key.Down, ModifierKeys.None, 4, 5, isRepeat: true));
    }

    [Fact]
    public void AMoveEndsAHoldSoTheNextEdgeCues()
    {
        var cue = new ListEdgeCue();
        Assert.Equal(ListEdge.Bottom, cue.Next(Key.Down, ModifierKeys.None, 4, 5, isRepeat: false));
        Assert.Equal(ListEdge.None, cue.Next(Key.Up, ModifierKeys.None, 4, 5, isRepeat: true));   // moved up
        Assert.Equal(ListEdge.Bottom, cue.Next(Key.Down, ModifierKeys.None, 4, 5, isRepeat: true));
    }

    // ── Sound or speech ────────────────────────────────────────────────────────

    /// <summary>What the VM asked to be said: the interrupting announcements, and the ones that wait for speech.</summary>
    private sealed record Heard(
        List<(string Text, AnnouncementCategory Category)> Interrupting,
        List<(string Text, AnnouncementCategory Category)> Queued);

    private static (MainViewModel Vm, Heard Heard) MakeVm(ISoundService? sounds)
    {
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
            new StubOAuthService(), new StubSyncService(), new StubConfigService(), new StubCommandRegistry(),
            new StubViewService(), new StubRuleService(), new StubSmtpService(), soundService: sounds);
        var heard = new Heard([], []);
        vm.AnnouncementRequested += (_, a) => heard.Interrupting.Add(a);
        vm.QueuedAnnouncementRequested += (_, a) => heard.Queued.Add(a);
        return (vm, heard);
    }

    [Fact]
    public void WhenTheSoundCanPlayTheSoundPlaysAndNothingIsSaid()
    {
        var sounds = new RecordingSoundService();
        sounds.Audible.Add(SoundEvent.ListEnd);
        var (vm, heard) = MakeVm(sounds);

        vm.SignalListEdge(ListEdge.Top);
        vm.SignalListEdge(ListEdge.Bottom);

        Assert.Equal([SoundEvent.ListEnd, SoundEvent.ListEnd], sounds.Played);
        Assert.Empty(heard.Interrupting);
        Assert.Empty(heard.Queued);
    }

    [Fact]
    public void WithoutTheSoundTheEdgeIsSaidAsAResultWithoutInterrupting()
    {
        var sounds = new RecordingSoundService();   // nothing audible
        var (vm, heard) = MakeVm(sounds);

        vm.SignalListEdge(ListEdge.Top);
        vm.SignalListEdge(ListEdge.Bottom);

        Assert.Empty(sounds.Played);
        // Queued, not interrupting: it must not cut off the screen reader reading the row just reached.
        Assert.Empty(heard.Interrupting);
        Assert.Equal([("Top of list", AnnouncementCategory.Result), ("Bottom of list", AnnouncementCategory.Result)], heard.Queued);
    }

    [Fact]
    public void WhileAnotherSoundIsPlayingTheEdgeIsSaidInstead()
    {
        var sounds = new RecordingSoundService { Busy = true };
        sounds.Audible.Add(SoundEvent.ListEnd);
        var (vm, heard) = MakeVm(sounds);

        vm.SignalListEdge(ListEdge.Bottom);

        Assert.Empty(sounds.Played);
        Assert.Equal([("Bottom of list", AnnouncementCategory.Result)], heard.Queued);
    }

    [Fact]
    public void WithNoSoundServiceTheEdgeIsStillSaid()
    {
        var (vm, heard) = MakeVm(null);
        vm.SignalListEdge(ListEdge.Top);
        Assert.Equal([("Top of list", AnnouncementCategory.Result)], heard.Queued);
    }

    [Fact]
    public void NoEdgeIsNeitherHeardNorSaid()
    {
        var sounds = new RecordingSoundService();
        sounds.Audible.Add(SoundEvent.ListEnd);
        var (vm, heard) = MakeVm(sounds);

        vm.SignalListEdge(ListEdge.None);

        Assert.Empty(sounds.Played);
        Assert.Empty(heard.Interrupting);
        Assert.Empty(heard.Queued);
    }

    // ── Through the real SoundService ──────────────────────────────────────────

    /// <summary>Records what reaches the audio engine; with <see cref="Hold"/> set, a sound "plays" until it is released.</summary>
    private sealed class RecordingOutput : ISoundOutput
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Played { get; } = new();
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim? Hold { get; set; }

        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
        {
            Played.Enqueue(clip.Key);
            Entered.Set();
            Hold?.Wait(cancellationToken);
        }

        public void Check(string filePath) { }
    }

    private sealed class RealSounds : IDisposable
    {
        public StubConfigService Config { get; } = new();
        public RecordingOutput Output { get; } = new();
        public SoundService Service { get; }

        public RealSounds(Action<ConfigModel> settings)
        {
            var cfg = Config.Load();
            cfg.PlayEventSounds = true;
            settings(cfg);
            Service = new SoundService(Config, Path.Combine(Path.GetTempPath(), $"QM-EDGE-{Guid.NewGuid():N}"), Output,
                openBuiltIn: (_, _) => new MemoryStream());
        }

        /// <summary>Waits for this many sounds to reach the engine, then a moment more for any wrongly queued.</summary>
        public List<string> PlayedAfterSettling(int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Output.Played.Count < expected && DateTime.UtcNow < deadline) Thread.Sleep(5);
            Thread.Sleep(200);
            return [.. Output.Played];
        }

        public void Dispose()
        {
            Output.Hold?.Set();
            Service.Dispose();
        }
    }

    /// <summary>The cue with these settings: what reached the audio engine, and what was said.</summary>
    private static (List<string> Played, Heard Heard) CueWith(Action<ConfigModel> settings)
    {
        using var sounds = new RealSounds(settings);
        var (vm, heard) = MakeVm(sounds.Service);
        vm.SignalListEdge(ListEdge.Top);
        return (sounds.PlayedAfterSettling(heard.Queued.Count == 0 ? 1 : 0), heard);
    }

    [Theory]
    [InlineData("glass")]
    [InlineData("softbell")]
    [InlineData("wood")]
    public void EveryBuiltInSchemePlaysTheEdgeSound(string scheme)
    {
        var (played, heard) = CueWith(c => c.SoundSchemeId = scheme);
        Assert.Equal(["builtin:edge"], played);
        Assert.Empty(heard.Queued);
    }

    [Fact]
    public void ASchemeOfYourOwnMadeBeforeTheEventExistedHasNoSoundSoTheEdgeIsSaid()
    {
        // A scheme made, imported or added from the gallery before #923 has no ListEnd entry at all.
        var (played, heard) = CueWith(c =>
        {
            c.SoundSchemes["user-old"] = new SoundScheme("user-old", "Rain", isBuiltIn: false,
                events: new Dictionary<string, string> { ["NewMail"] = "builtin:arrived", ["MessageSent"] = "builtin:sent" });
            c.SoundSchemeId = "user-old";
        });
        Assert.Empty(played);
        Assert.Equal([("Top of list", AnnouncementCategory.Result)], heard.Queued);
    }

    [Fact]
    public void ASchemeOfYourOwnWithASoundChosenForItPlaysIt()
    {
        var (played, heard) = CueWith(c =>
        {
            c.SoundSchemes["user-new"] = new SoundScheme("user-new", "Rain", isBuiltIn: false,
                events: new Dictionary<string, string> { ["ListEnd"] = "builtin:glass/edge" });
            c.SoundSchemeId = "user-new";
        });
        Assert.Equal(["builtin:glass/edge"], played);
        Assert.Empty(heard.Queued);
    }

    public static TheoryData<string> SilencingSettings => ["event none", "sounds off", "muted", "no volume"];

    [Theory]
    [MemberData(nameof(SilencingSettings))]
    public void WhenTheSoundWouldNotBeHeardTheEdgeIsSaid(string setting)
    {
        var (played, heard) = CueWith(c =>
        {
            switch (setting)
            {
                case "event none": c.ActiveSoundEvents()["ListEnd"] = "none"; break;
                case "sounds off": c.PlayEventSounds = false; break;
                case "muted": c.MuteSounds = true; break;
                case "no volume": c.SoundVolume = 0; break;
            }
        });
        Assert.Empty(played);
        Assert.Equal([("Top of list", AnnouncementCategory.Result)], heard.Queued);
    }

    [Fact]
    public void WhileASoundIsPlayingTheEdgeSoundIsNotQueuedAndTheEdgeIsSaid()
    {
        using var sounds = new RealSounds(c => c.SoundSchemeId = "glass");
        sounds.Output.Hold = new ManualResetEventSlim();
        var (vm, heard) = MakeVm(sounds.Service);

        sounds.Service.Play(SoundEvent.NewMail);
        Assert.True(sounds.Output.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the first sound never started");
        Assert.True(sounds.Service.IsBusy);

        vm.SignalListEdge(ListEdge.Bottom);

        Assert.Equal([("Bottom of list", AnnouncementCategory.Result)], heard.Queued);
        sounds.Output.Hold.Set();   // the first sound ends
        Assert.Equal(["builtin:arrived"], sounds.PlayedAfterSettling(1));   // and nothing was waiting behind it
    }

    [Fact]
    public void OnceTheOtherSoundHasEndedTheEdgeSoundPlaysAgain()
    {
        using var sounds = new RealSounds(c => c.SoundSchemeId = "glass");
        var (vm, heard) = MakeVm(sounds.Service);

        sounds.Service.Play(SoundEvent.NewMail);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((sounds.Output.Played.IsEmpty || sounds.Service.IsBusy) && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.False(sounds.Service.IsBusy);

        vm.SignalListEdge(ListEdge.Top);

        Assert.Equal(["builtin:arrived", "builtin:edge"], sounds.PlayedAfterSettling(2));
        Assert.Empty(heard.Queued);
    }

    [Fact]
    public void StoppingEverythingLeavesNothingCountedAsPlaying()
    {
        using var sounds = new RealSounds(c => c.SoundSchemeId = "glass");
        sounds.Output.Hold = new ManualResetEventSlim();
        sounds.Service.Play(SoundEvent.NewMail);
        Assert.True(sounds.Output.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        sounds.Service.Play(SoundEvent.MessageSent);   // waits behind it

        sounds.Service.StopAll();   // cancels the one playing and drops the one waiting

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sounds.Service.IsBusy && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.False(sounds.Service.IsBusy);
        Assert.True(sounds.Service.TryPlayNow(SoundEvent.ListEnd));
    }
}
