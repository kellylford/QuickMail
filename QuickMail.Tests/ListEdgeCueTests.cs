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
/// <see cref="MainViewModel.SignalListEdge"/>: the "Reached the end of a list" sound when it would be
/// heard, else a "Top of list" / "Bottom of list" hint.
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

    // ── Sound or hint ──────────────────────────────────────────────────────────

    private static (MainViewModel Vm, List<(string Text, AnnouncementCategory Category)> Announced) MakeVm(ISoundService? sounds)
    {
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
            new StubOAuthService(), new StubSyncService(), new StubConfigService(), new StubCommandRegistry(),
            new StubViewService(), new StubRuleService(), new StubSmtpService(), soundService: sounds);
        var announced = new List<(string, AnnouncementCategory)>();
        vm.AnnouncementRequested += (_, a) => announced.Add(a);
        return (vm, announced);
    }

    [Fact]
    public void WithTheSoundAudibleTheSoundPlaysAndNothingIsSaid()
    {
        var sounds = new RecordingSoundService();
        sounds.Audible.Add(SoundEvent.ListEnd);
        var (vm, announced) = MakeVm(sounds);

        vm.SignalListEdge(ListEdge.Top);
        vm.SignalListEdge(ListEdge.Bottom);

        Assert.Equal([SoundEvent.ListEnd, SoundEvent.ListEnd], sounds.Played);
        Assert.Empty(announced);
    }

    [Fact]
    public void WithoutTheSoundTheEdgeIsSaidAsAHint()
    {
        var sounds = new RecordingSoundService();   // nothing audible
        var (vm, announced) = MakeVm(sounds);

        vm.SignalListEdge(ListEdge.Top);
        vm.SignalListEdge(ListEdge.Bottom);

        Assert.Empty(sounds.Played);
        Assert.Equal([("Top of list", AnnouncementCategory.Hint), ("Bottom of list", AnnouncementCategory.Hint)], announced);
    }

    [Fact]
    public void WithNoSoundServiceTheEdgeIsStillSaid()
    {
        var (vm, announced) = MakeVm(null);
        vm.SignalListEdge(ListEdge.Top);
        Assert.Equal([("Top of list", AnnouncementCategory.Hint)], announced);
    }

    [Fact]
    public void NoEdgeIsNeitherHeardNorSaid()
    {
        var sounds = new RecordingSoundService();
        sounds.Audible.Add(SoundEvent.ListEnd);
        var (vm, announced) = MakeVm(sounds);

        vm.SignalListEdge(ListEdge.None);

        Assert.Empty(sounds.Played);
        Assert.Empty(announced);
    }

    // ── The real sound settings decide which ───────────────────────────────────

    private sealed class SilentOutput : ISoundOutput
    {
        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken) { }
        public void Check(string filePath) { }
    }

    /// <summary>What the cue is with these settings, through the real <see cref="SoundService"/>.</summary>
    private static (bool Played, List<(string Text, AnnouncementCategory Category)> Announced) CueWith(Action<ConfigModel> settings)
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.PlayEventSounds = true;
        settings(cfg);
        var folder = Path.Combine(Path.GetTempPath(), $"QM-EDGE-{Guid.NewGuid():N}");
        using var service = new SoundService(config, folder, new SilentOutput(), openBuiltIn: (_, _) => new MemoryStream());
        var audible = service.IsAudible(SoundEvent.ListEnd);
        var (vm, announced) = MakeVm(service);
        vm.SignalListEdge(ListEdge.Top);
        return (audible, announced);
    }

    [Theory]
    [InlineData("glass")]
    [InlineData("softbell")]
    [InlineData("wood")]
    public void EveryBuiltInSchemeHasTheSound(string scheme)
    {
        var (played, announced) = CueWith(c => c.SoundSchemeId = scheme);
        Assert.True(played);
        Assert.Empty(announced);
    }

    [Fact]
    public void ASchemeOfYourOwnMadeBeforeTheEventExistedHasNoSoundSoTheEdgeIsSaid()
    {
        // A scheme made, imported or added from the gallery before #923 has no ListEnd entry at all.
        var (played, announced) = CueWith(c =>
        {
            c.SoundSchemes["user-old"] = new SoundScheme("user-old", "Rain", isBuiltIn: false,
                events: new Dictionary<string, string> { ["NewMail"] = "builtin:arrived", ["MessageSent"] = "builtin:sent" });
            c.SoundSchemeId = "user-old";
        });
        Assert.False(played);
        Assert.Equal([("Top of list", AnnouncementCategory.Hint)], announced);
    }

    [Fact]
    public void ASchemeOfYourOwnWithASoundChosenForItPlaysIt()
    {
        var (played, announced) = CueWith(c =>
        {
            c.SoundSchemes["user-new"] = new SoundScheme("user-new", "Rain", isBuiltIn: false,
                events: new Dictionary<string, string> { ["ListEnd"] = "builtin:glass/edge" });
            c.SoundSchemeId = "user-new";
        });
        Assert.True(played);
        Assert.Empty(announced);
    }

    [Fact]
    public void TheSoundTurnedOffInTheSchemeMeansTheEdgeIsSaid()
    {
        var (played, announced) = CueWith(c => c.ActiveSoundEvents()["ListEnd"] = "none");
        Assert.False(played);
        Assert.Equal([("Top of list", AnnouncementCategory.Hint)], announced);
    }

    [Fact]
    public void SoundsOffMeansTheEdgeIsSaid()
    {
        var (played, announced) = CueWith(c => c.PlayEventSounds = false);
        Assert.False(played);
        Assert.Equal([("Top of list", AnnouncementCategory.Hint)], announced);
    }

    [Fact]
    public void MutedMeansTheEdgeIsSaid()
    {
        var (played, announced) = CueWith(c => c.MuteSounds = true);
        Assert.False(played);
        Assert.Equal([("Top of list", AnnouncementCategory.Hint)], announced);
    }

    [Fact]
    public void NoVolumeMeansTheEdgeIsSaid()
    {
        var (played, announced) = CueWith(c => c.SoundVolume = 0);
        Assert.False(played);
        Assert.Equal([("Top of list", AnnouncementCategory.Hint)], announced);
    }
}
