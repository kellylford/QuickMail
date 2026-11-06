using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>Sound schemes: making your own, resetting a built-in one, and sharing (#932).</summary>
public class SoundSchemeTests
{
    private static SoundScheme Glass(Dictionary<string, string>? stored = null) =>
        new("glass", "Glass", isBuiltIn: true, pack: "glass", events: stored);

    private static SoundScheme Own(Dictionary<string, string>? stored = null) =>
        new("user-1", "Mine", isBuiltIn: false, events: stored);

    // ── SoundRef with a pack ────────────────────────────────────────────────────

    [Theory]
    [InlineData("builtin:wood/arrived")]
    [InlineData("builtin:softbell/matched")]
    [InlineData("builtin:arrived")]
    public void ABuiltInSoundWithOrWithoutAPackRoundTrips(string text)
    {
        Assert.True(SoundRef.TryParse(text, out var sound));
        Assert.Equal(text, sound.ToStored());
    }

    [Theory]
    [InlineData("builtin:chimes/arrived")]
    [InlineData("builtin:wood/nothing")]
    [InlineData("builtin:/arrived")]
    [InlineData("builtin:wood/")]
    [InlineData("builtin:../arrived")]
    public void AnUnknownPackOrNameIsRefused(string text) => Assert.False(SoundRef.TryParse(text, out _));

    [Fact]
    public void ABuiltInSoundWithAPackSaysWhichPack()
    {
        Assert.True(SoundRef.TryParse("builtin:softbell/arrived", out var sound));
        Assert.Equal("softbell", sound.Pack);
        Assert.Equal("Arrived (Soft bell)", sound.DisplayName);
    }

    // ── Resolving ──────────────────────────────────────────────────────────────

    [Fact]
    public void ABuiltInSchemePlaysItsShippedSoundsUntilChanged()
    {
        Assert.Equal(SoundRef.BuiltIn("arrived"), SoundCatalog.Resolve(SoundEvent.NewMail, Glass()));
        Assert.Equal(SoundRef.None, SoundCatalog.Resolve(SoundEvent.NewMail, Glass(new() { ["NewMail"] = "none" })));
        Assert.Equal(SoundRef.BuiltIn("sent"), SoundCatalog.Resolve(SoundEvent.MessageSent, Glass(new() { ["NewMail"] = "none" })));
    }

    [Fact]
    public void ASchemeOfYourOwnIsSilentForAnyEventItDoesNotList()
    {
        var scheme = Own(new() { ["NewMail"] = "builtin:wood/arrived" });
        Assert.Equal(SoundRef.BuiltIn("arrived", "wood"), SoundCatalog.Resolve(SoundEvent.NewMail, scheme));
        Assert.Equal(SoundRef.None, SoundCatalog.Resolve(SoundEvent.MessageSent, scheme));
        Assert.Equal(SoundRef.None, SoundCatalog.Resolve(SoundEvent.RuleMatched, scheme));
    }

    [Theory]
    [InlineData("glass", "glass")]
    [InlineData("wood", "wood")]
    [InlineData("from-the-future", "glass")]
    [InlineData("user-gone", "glass")]
    public void TheSchemeInUseIsTheOneNamedOrGlass(string id, string expected)
    {
        var config = new ConfigModel { SoundSchemeId = id };
        Assert.Equal(expected, SoundCatalog.ActiveScheme(config).Id);
    }

    [Fact]
    public void ARuleWithTheDefaultSoundPlaysTheSchemesRuleSound()
    {
        var scheme = Glass(new() { ["RuleMatched"] = "builtin:attention" });
        Assert.Equal(SoundRef.BuiltIn("attention"), SoundService.RuleSound("builtin:matched", scheme));
        Assert.Equal(SoundRef.BuiltIn("attention"), SoundService.RuleSound(null, scheme));
        Assert.Equal(SoundRef.BuiltIn("attention"), SoundService.RuleSound("rubbish", scheme));
        // A rule with a sound of its own plays it in every scheme.
        Assert.Equal(SoundRef.BuiltIn("reminder"), SoundService.RuleSound("builtin:reminder", scheme));
        Assert.Equal(SoundRef.BuiltIn("matched", "wood"), SoundService.RuleSound("builtin:wood/matched", scheme));
    }

    // ── config.ini ─────────────────────────────────────────────────────────────

    private static ConfigModel Parse(params string[] lines) => ConfigService.ParseFile(lines);

    private static ConfigModel RoundTrip(ConfigModel config) =>
        ConfigService.ParseFile(ConfigService.BuildFileText(config).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

    [Fact]
    public void A0868ConfigKeepsItsStyleAndChoicesAsThatScheme()
    {
        var config = Parse("[global]", "SoundPack = wood", "PlayEventSounds = on", "[sounds]", "NewMail = custom:Ding.mp3", "MessageMoved = none");

        Assert.Equal("wood", config.SoundSchemeId);
        var wood = config.SoundSchemes["wood"];
        Assert.True(wood.IsBuiltIn);
        Assert.Equal("custom:Ding.mp3", wood.Events["NewMail"]);
        Assert.Equal("none", wood.Events["MessageMoved"]);
        Assert.False(config.SoundSchemes.ContainsKey("glass"));

        var written = ConfigService.BuildFileText(config);
        Assert.DoesNotContain("SoundPack", written, StringComparison.Ordinal);
        Assert.DoesNotContain("[sounds]", written, StringComparison.Ordinal);
        Assert.Contains("SoundScheme = wood", written, StringComparison.Ordinal);
        Assert.Contains("[sounds.wood]", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A0868ConfigWithNoStyleMovesItsChoicesIntoGlass()
    {
        var config = Parse("[sounds]", "NewMail = none");
        Assert.Equal("glass", config.SoundSchemeId);
        Assert.Equal("none", config.SoundSchemes["glass"].Events["NewMail"]);
    }

    [Fact]
    public void SoundSchemeWinsOverAnOldSoundPack()
    {
        var config = Parse("[global]", "SoundPack = wood", "SoundScheme = softbell");
        Assert.Equal("softbell", config.SoundSchemeId);
    }

    [Fact]
    public void ASchemeOfYourOwnRoundTripsWithEveryEventAndAnAwkwardName()
    {
        var config = new ConfigModel { SoundSchemeId = "user-abc" };
        config.SoundSchemes["user-abc"] = new SoundScheme("user-abc", "Calm = quiet [evening] # mine", isBuiltIn: false,
            events: SoundCatalog.Events.ToDictionary(i => i.ConfigKey, _ => "none"));
        config.SoundSchemes["user-abc"].Events["NewMail"] = "builtin:softbell/arrived";

        var reloaded = RoundTrip(config);

        var scheme = reloaded.SoundSchemes["user-abc"];
        Assert.False(scheme.IsBuiltIn);
        Assert.Equal("Calm = quiet [evening] # mine", scheme.Name);
        Assert.Equal(SoundCatalog.Events.Count, scheme.Events.Count);
        Assert.Equal("builtin:softbell/arrived", scheme.Events["NewMail"]);
        Assert.False(scheme.Events.ContainsKey("Name"));
        Assert.Equal("user-abc", reloaded.SoundSchemeId);
    }

    [Fact]
    public void AnUnknownSectionAndAnUnknownSchemeSurviveASave()
    {
        var profile = new ProfileContext(Path.Combine(Path.GetTempPath(), $"QM-SCH-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(profile.ProfileDir);
        File.WriteAllLines(Path.Combine(profile.ProfileDir, "config.ini"),
            ["[global]", "SoundScheme = chimes", "[sounds.chimes]", "Name = From the future", "NewMail = builtin:chimes/arrived", "[sounds.glass]", "Brand new event = builtin:future"]);

        var service = new ConfigService(profile);
        var vm = new SettingsViewModel(service, new StubCommandRegistry(), sounds: new RecordingSoundService());
        Assert.Equal("glass", vm.SelectedSoundScheme!.Id);   // shown as Glass...
        vm.SaveCommand.Execute(null);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal("chimes", reloaded.SoundSchemeId);       // ...but kept as written
        Assert.Equal("builtin:chimes/arrived", reloaded.SoundSchemes["chimes"].Events["NewMail"]);
        Assert.Equal("From the future", reloaded.SoundSchemes["chimes"].Events["Name"]);
        Assert.Equal("builtin:future", reloaded.SoundSchemes["glass"].Events["Brand new event"]);
        Assert.DoesNotContain(vm.SoundSchemes, s => s.Id == "chimes");
    }

    // ── Settings ───────────────────────────────────────────────────────────────

    private static SettingsViewModel MakeSettings(StubConfigService config, RecordingSoundService? sounds = null) =>
        new(config, new StubCommandRegistry(), sounds: sounds ?? new RecordingSoundService());

    private static SoundEventRow Row(SettingsViewModel vm, SoundEvent e) => vm.SoundEventRows.Single(r => r.Info.Event == e);

    private static void Choose(SettingsViewModel vm, SoundEvent e, string label)
    {
        vm.SelectedSoundEvent = Row(vm, e);
        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Label == label);
    }

    [Fact]
    public void TheSchemesAreTheBuiltInOnesThenYourOwnByName()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-b"] = new SoundScheme("user-b", "Zebra", isBuiltIn: false);
        config.Load().SoundSchemes["user-a"] = new SoundScheme("user-a", "apple", isBuiltIn: false);
        var vm = MakeSettings(config);
        Assert.Equal(["Glass", "Soft bell", "Wood", "apple", "Zebra"], vm.SoundSchemes.Select(s => s.Name));
    }

    [Fact]
    public void ANewSchemeStartsWithEveryEventSilentAndIsShown()
    {
        var vm = MakeSettings(new StubConfigService());
        Assert.True(vm.CreateScheme("  Calm  ", copyShown: false));

        Assert.Equal("Calm", vm.SelectedSoundScheme!.Name);
        Assert.False(vm.SelectedSoundScheme.IsBuiltIn);
        Assert.All(vm.SoundEventRows, r => Assert.Equal("none", r.Choice.Stored));
        Assert.Equal($"0 of {SoundCatalog.Events.Count} events have a sound", vm.SchemeSoundCount);
        Assert.True(vm.IsUserSchemeSelected);
        Assert.False(vm.CanResetScheme);
        Assert.Equal("New mail: None", Row(vm, SoundEvent.NewMail).AccessibleName);
    }

    [Fact]
    public void ASchemeOfYourOwnOffersEveryBuiltInSoundInEveryPack()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.CreateScheme("Calm", copyShown: false);
        Assert.Contains(vm.SoundChoices, c => c.Label == "Arrived (Soft bell)" && c.Stored == "builtin:softbell/arrived");
        Assert.Contains(vm.SoundChoices, c => c.Label == "Arrived (Glass)");
        Assert.DoesNotContain(vm.SoundChoices, c => c.Label == "Arrived");

        Choose(vm, SoundEvent.NewMail, "Arrived (Soft bell)");
        Assert.Equal("New mail: Arrived (Soft bell)", Row(vm, SoundEvent.NewMail).AccessibleName);
        Assert.Equal($"1 of {SoundCatalog.Events.Count} events have a sound", vm.SchemeSoundCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("glass")]
    [InlineData("SOFT BELL")]
    [InlineData("calm")]
    public void ANameThatIsEmptyOrTakenIsRefused(string name)
    {
        var vm = MakeSettings(new StubConfigService());
        vm.CreateScheme("Calm", copyShown: false);
        var count = vm.SoundSchemes.Count;

        Assert.NotNull(vm.SchemeNameProblem(name));
        Assert.False(vm.CreateScheme(name, copyShown: false));
        Assert.Equal(count, vm.SoundSchemes.Count);
    }

    [Fact]
    public void ATooLongNameIsRefused()
    {
        var vm = MakeSettings(new StubConfigService());
        Assert.Contains("too long", vm.SchemeNameProblem(new string('a', SoundCatalog.MaxSchemeNameLength + 1)), StringComparison.Ordinal);
        Assert.Null(vm.SchemeNameProblem(new string('a', SoundCatalog.MaxSchemeNameLength)));
    }

    [Fact]
    public void ACopyStartsFromTheSchemeShownWithItsTonePinned()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["wood"] = new SoundScheme("wood", "Wood", true, "wood", new Dictionary<string, string> { ["MessageSent"] = "none" });
        config.Load().SoundSchemeId = "wood";
        var vm = MakeSettings(config);
        Choose(vm, SoundEvent.TrashEmptied, "Reminder");   // unsaved, still copied

        Assert.True(vm.CreateScheme("Wood mine", copyShown: true));

        Assert.Equal("builtin:wood/arrived", Row(vm, SoundEvent.NewMail).Choice.Stored);
        Assert.Equal("none", Row(vm, SoundEvent.MessageSent).Choice.Stored);
        Assert.Equal("builtin:wood/reminder", Row(vm, SoundEvent.TrashEmptied).Choice.Stored);
        Assert.Equal("New mail: Arrived (Wood)", Row(vm, SoundEvent.NewMail).AccessibleName);
    }

    [Fact]
    public void SwitchingSchemesKeepsTheUnsavedChangesOfEach()
    {
        var vm = MakeSettings(new StubConfigService());
        Choose(vm, SoundEvent.NewMail, "None");                       // Glass
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Wood");
        Assert.Equal("builtin:arrived", Row(vm, SoundEvent.NewMail).Choice.Stored);   // Wood's own
        Choose(vm, SoundEvent.MessageSent, "Reminder");               // Wood

        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Glass");
        Assert.Equal("none", Row(vm, SoundEvent.NewMail).Choice.Stored);
        Assert.Equal("builtin:sent", Row(vm, SoundEvent.MessageSent).Choice.Stored);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Wood");
        Assert.Equal("builtin:reminder", Row(vm, SoundEvent.MessageSent).Choice.Stored);
    }

    [Fact]
    public void SwitchingSchemesKeepsTheEventSelected()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.SelectedSoundEvent = Row(vm, SoundEvent.TrashEmptied);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Wood");
        Assert.Equal(SoundEvent.TrashEmptied, vm.SelectedSoundEvent!.Info.Event);
        Assert.Equal(vm.SelectedSoundEvent.Choice, vm.SelectedSoundChoice);
    }

    [Fact]
    public void SavingWritesOnlyChangesForBuiltInSchemesAndEverythingForYourOwn()
    {
        var config = new StubConfigService();
        var vm = MakeSettings(config);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Wood");
        Choose(vm, SoundEvent.TrashEmptied, "None");
        vm.CreateScheme("Calm", copyShown: false);
        Choose(vm, SoundEvent.NewMail, "Arrived (Soft bell)");

        vm.SaveCommand.Execute(null);

        var saved = config.Load();
        var calm = saved.SoundSchemes.Values.Single(s => s.Name == "Calm");
        Assert.Equal(calm.Id, saved.SoundSchemeId);
        Assert.Equal(SoundCatalog.Events.Count, calm.Events.Count);
        Assert.Equal("builtin:softbell/arrived", calm.Events["NewMail"]);
        Assert.Equal("none", calm.Events["MessageSent"]);
        Assert.Equal(new Dictionary<string, string> { ["TrashEmptied"] = "none" }, saved.SoundSchemes["wood"].Events);
        Assert.False(saved.SoundSchemes.ContainsKey("glass"));
    }

    [Fact]
    public void ResetPutsABuiltInSchemeBackAndThenIsDisabled()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["wood"] = new SoundScheme("wood", "Wood", true, "wood", new Dictionary<string, string> { ["TrashEmptied"] = "none" });
        config.Load().SoundSchemeId = "wood";
        var vm = MakeSettings(config);
        string? asked = null;
        vm.ConfirmRequested = (question, _) => { asked = question; return true; };

        Assert.True(vm.CanResetScheme);
        vm.ResetSchemeCommand.Execute(null);

        Assert.StartsWith("Reset Wood to how it ships?", asked, StringComparison.Ordinal);
        Assert.Equal("Trash emptied: Emptied", Row(vm, SoundEvent.TrashEmptied).AccessibleName);
        Assert.False(vm.CanResetScheme);
        vm.SaveCommand.Execute(null);
        Assert.False(config.Load().SoundSchemes.ContainsKey("wood"));
    }

    [Fact]
    public void ResetDoesNothingIfTheAnswerIsNo()
    {
        var vm = MakeSettings(new StubConfigService());
        Choose(vm, SoundEvent.NewMail, "None");
        vm.ConfirmRequested = (_, _) => false;
        vm.ResetSchemeCommand.Execute(null);
        Assert.Equal("none", Row(vm, SoundEvent.NewMail).Choice.Stored);
        Assert.True(vm.CanResetScheme);
    }

    [Fact]
    public void ResetIsNotForYourOwnSchemes()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.CreateScheme("Calm", copyShown: false);
        Assert.False(vm.CanResetScheme);
    }

    [Fact]
    public void RenameKeepsTheSchemesSoundsAndRefusesATakenName()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.CreateScheme("Calm", copyShown: false);
        vm.CreateScheme("Busy", copyShown: false);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Calm");
        Choose(vm, SoundEvent.NewMail, "Arrived (Wood)");

        Assert.False(vm.RenameSelectedScheme("busy"));
        Assert.True(vm.RenameSelectedScheme("CALM"));   // its own name, in other letters
        Assert.True(vm.RenameSelectedScheme("Aaa quiet"));

        Assert.Equal("Aaa quiet", vm.SelectedSoundScheme!.Name);
        Assert.Equal("builtin:wood/arrived", Row(vm, SoundEvent.NewMail).Choice.Stored);
        Assert.Equal(["Glass", "Soft bell", "Wood", "Aaa quiet", "Busy"], vm.SoundSchemes.Select(s => s.Name));
    }

    [Fact]
    public void BuiltInSchemesCannotBeRenamedOrDeleted()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.ConfirmRequested = (_, _) => true;
        Assert.False(vm.IsUserSchemeSelected);
        Assert.False(vm.RenameSelectedScheme("Other"));
        Assert.False(vm.DeleteSelectedScheme());
        Assert.Equal(3, vm.SoundSchemes.Count);
    }

    [Fact]
    public void DeletingTheSchemeInUseAsksByNameAndSelectsGlass()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-x"] = new SoundScheme("user-x", "Calm", isBuiltIn: false);
        config.Load().SoundSchemeId = "user-x";
        var vm = MakeSettings(config);
        string? asked = null;
        vm.ConfirmRequested = (question, _) => { asked = question; return true; };

        Assert.True(vm.DeleteSelectedScheme());

        Assert.Equal("Delete the sound scheme Calm?", asked);
        Assert.Equal("glass", vm.SelectedSoundScheme!.Id);
        Assert.Equal("Message sent: Sent", Row(vm, SoundEvent.MessageSent).AccessibleName);
        vm.SaveCommand.Execute(null);
        Assert.Equal("glass", config.Load().SoundSchemeId);
        Assert.False(config.Load().SoundSchemes.ContainsKey("user-x"));
    }

    [Fact]
    public void AnUpgradeShowsTheOldStyleAndChoicesWithResetAvailable()
    {
        var config = new StubConfigService();
        config.Save(ConfigService.ParseFile(["[global]", "SoundPack = wood", "[sounds]", "NewMail = custom:Ding.mp3"]));
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.Add("Ding.mp3");
        var vm = MakeSettings(config, sounds);

        Assert.Equal("Wood", vm.SelectedSoundScheme!.Name);
        Assert.Equal("New mail: Ding.mp3", Row(vm, SoundEvent.NewMail).AccessibleName);
        Assert.True(vm.CanResetScheme);
    }

    [Fact]
    public void ARuleMatchedIsAnEventInTheList()
    {
        var vm = MakeSettings(new StubConfigService());
        Assert.Equal("A rule matched: Matched", Row(vm, SoundEvent.RuleMatched).AccessibleName);
    }

    [Fact]
    public void PreviewInASchemeOfYourOwnUsesGlassForAnUnpinnedSound()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(new StubConfigService(), sounds);
        vm.CreateScheme("Calm", copyShown: false);
        Choose(vm, SoundEvent.NewMail, "Arrived (Wood)");
        vm.PreviewSoundCommand.Execute(null);
        Assert.Equal("builtin:wood/arrived", sounds.Previewed.Last());
        Assert.Equal("glass", sounds.PreviewPack);
    }

    // ── Remove file ────────────────────────────────────────────────────────────

    [Fact]
    public void RemoveFileSaysWhatStillUsesItThenListsItAsMissing()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["wood"] = new SoundScheme("wood", "Wood", true, "wood", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" });
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.Add("Ding.wav");
        var vm = MakeSettings(config, sounds);
        vm.RuleSoundsProvider = () => ["custom:Ding.wav", "builtin:reminder", null, "custom:DING.wav"];
        vm.CreateScheme("Calm", copyShown: false);
        Choose(vm, SoundEvent.MessageSent, "Ding.wav");

        var (schemes, rules) = vm.SoundFileUses("Ding.wav");
        Assert.Equal(["Wood", "Calm"], schemes);
        Assert.Equal(2, rules);

        string? asked = null;
        vm.ConfirmRequested = (question, _) => { asked = question; return true; };
        Assert.True(vm.CanRemoveSoundFile);
        vm.RemoveSoundFileCommand.Execute(null);

        Assert.Equal("Remove the sound file Ding.wav from QuickMail? It is still used by the sound schemes Wood and Calm, and by 2 rules, which will play a built-in sound instead.", asked);
        Assert.Equal(["Ding.wav"], sounds.Removed);
        Assert.Equal("Message sent: Ding.wav (missing)", Row(vm, SoundEvent.MessageSent).AccessibleName);
        Assert.False(vm.CanRemoveSoundFile);
        vm.SaveCommand.Execute(null);
        // The uses stay, and play their fallback as for any missing file.
        Assert.Equal("custom:Ding.wav", config.Load().SoundSchemes["wood"].Events["NewMail"]);
    }

    [Fact]
    public void RemoveFileCountsAnUnsavedChoiceAsAUse()
    {
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.Add("Spare.wav");
        var vm = MakeSettings(new StubConfigService(), sounds);
        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Label == "Spare.wav");
        var (schemes, _) = vm.SoundFileUses("Spare.wav");
        Assert.Equal(["Glass"], schemes);
        string? asked = null;
        vm.ConfirmRequested = (question, _) => { asked = question; return false; };
        vm.RemoveSoundFileCommand.Execute(null);
        Assert.Contains("still used by the sound scheme Glass", asked, StringComparison.Ordinal);
        Assert.Empty(sounds.Removed);   // the answer was No
    }

    [Fact]
    public void RemoveFileIsOnlyForACustomFileThatIsThere()
    {
        var config = new StubConfigService();
        config.Load().ActiveSoundEvents()["NewMail"] = "custom:Gone.wav";
        var vm = MakeSettings(config);
        Assert.False(vm.CanRemoveSoundFile);   // Message sent: a built-in sound
        vm.SelectedSoundEvent = Row(vm, SoundEvent.NewMail);
        Assert.False(vm.CanRemoveSoundFile);   // missing
    }

    [Fact]
    public void AFileThatCannotBeRemovedSaysWhy()
    {
        var sounds = new RecordingSoundService { RemoveRefusal = "Ding.wav could not be removed." };
        sounds.CustomFiles.Add("Ding.wav");
        var vm = MakeSettings(new StubConfigService(), sounds);
        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Label == "Ding.wav");
        vm.ConfirmRequested = (_, _) => true;
        string? announced = null;
        vm.SoundResultAnnounced += text => announced = text;

        vm.RemoveSoundFileCommand.Execute(null);

        Assert.Equal("Ding.wav could not be removed.", vm.SoundFileMessage);
        Assert.Equal(vm.SoundFileMessage, announced);
        Assert.Equal("Message sent: Ding.wav", vm.SoundEventRows[0].AccessibleName);   // not marked missing
    }

    [Fact]
    public void ChoosingAFileSaysItWorkedSoTheViewCanMoveFocus()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.PickSoundFileRequested = () => @"C:\Music\Ding.wav";
        Assert.True(vm.TryChooseSoundFile());
        vm.PickSoundFileRequested = () => null;
        Assert.False(vm.TryChooseSoundFile());
    }
}

/// <summary>The sound service with schemes, against the real files in a temp folder (#932).</summary>
public sealed class SoundSchemeServiceTests : IDisposable
{
    private sealed class Output : ISoundOutput
    {
        public List<string> Played { get; } = [];
        public ManualResetEventSlim Entered { get; } = new();
        public bool Refuse { get; set; }

        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
        {
            using var stream = clip.OpenStream?.Invoke();
            lock (Played) Played.Add(clip.Key);
            Entered.Set();
        }

        public void Check(string filePath)
        {
            if (Refuse) throw new FormatException("Not a WAVE file");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"QM-SCHS-{Guid.NewGuid():N}");
    private readonly StubConfigService _config = new();
    private readonly Output _output = new();
    private readonly List<string> _packsAsked = [];

    public SoundSchemeServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private SoundService Service(string folder) => new(_config, folder, _output, openBuiltIn: (pack, _) =>
    {
        lock (_packsAsked) _packsAsked.Add(pack);
        return new MemoryStream();
    });

    [Fact]
    public void ASchemeOfYourOwnPlaysAPinnedSoundInItsPackAndOthersNotAtAll()
    {
        _config.Load().PlayEventSounds = true;
        _config.Load().SoundSchemeId = "user-1";
        _config.Load().SoundSchemes["user-1"] = new SoundScheme("user-1", "Mine", false, events: new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived" });
        using var service = Service(Folder("a"));

        Assert.False(service.IsAudible(SoundEvent.MessageSent));
        Assert.True(service.IsAudible(SoundEvent.NewMail));
        service.Play(SoundEvent.MessageSent);
        service.Play(SoundEvent.NewMail);

        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(250);
        lock (_output.Played) Assert.Equal(["builtin:wood/arrived"], _output.Played);
        lock (_packsAsked) Assert.Equal(["wood"], _packsAsked);
    }

    [Fact]
    public void ARuleWithTheDefaultSoundInASilentSchemePlaysNothingAndHoldsNothingBack()
    {
        _config.Load().SoundSchemeId = "user-1";
        _config.Load().SoundSchemes["user-1"] = new SoundScheme("user-1", "Mine", false);
        using var service = Service(Folder("b"));

        service.PlayRule("builtin:matched", ["key"]);

        Thread.Sleep(250);
        lock (_output.Played) Assert.Empty(_output.Played);
        Assert.False(service.WasSoundedByRule("key"));
    }

    [Fact]
    public void RemovingACustomSoundDeletesOnlyThatFile()
    {
        var folder = Folder("c");
        File.WriteAllBytes(Path.Combine(folder, "Ding.wav"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "Keep.wav"), [2]);
        using var service = Service(folder);

        Assert.True(service.TryRemoveCustomSound("Ding.wav", out _));
        Assert.True(service.TryRemoveCustomSound("Ding.wav", out _));   // already gone is fine
        Assert.True(service.TryRemoveCustomSound(@"..\Keep.wav", out _));   // never outside the folder

        Assert.Equal(["Keep.wav"], service.CustomSoundFiles());
        Assert.Null(service.CustomSoundPath("Ding.wav"));
        Assert.NotNull(service.CustomSoundPath("Keep.wav"));
    }

    // ── Sharing a scheme ───────────────────────────────────────────────────────

    [Fact]
    public void AnExportedSchemeImportsWithItsSoundsElsewhere()
    {
        var here = Folder("here");
        File.WriteAllBytes(Path.Combine(here, "Ding.wav"), [1, 2, 3]);
        var scheme = new SoundScheme("wood", "Wood", true, "wood", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav", ["MessageSent"] = "none" });
        var file = Path.Combine(_root, "Wood.qmsounds");
        using (var service = Service(here))
            SoundSchemeFile.Export(file, "Wood mine", SoundSchemeFile.Portable(scheme), service);

        var there = Folder("there");
        File.WriteAllBytes(Path.Combine(there, "Ding.wav"), [9, 9]);   // a different sound with the same name
        using var other = Service(there);
        var imported = SoundSchemeFile.Import(file, other);

        Assert.Equal("Wood mine", imported.Name);
        Assert.Equal("custom:Ding 2.wav", imported.Events["NewMail"]);
        Assert.Equal("none", imported.Events["MessageSent"]);
        Assert.Equal("builtin:wood/queued", imported.Events["MessageQueued"]);
        Assert.Equal(1, imported.SoundsAdded);
        Assert.Equal(0, imported.SoundsDropped);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(there, "Ding 2.wav")));
        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(there, "Ding.wav")));
    }

    [Fact]
    public void ASoundThatWillNotPlayIsDroppedOnImportAndCounted()
    {
        var here = Folder("here2");
        File.WriteAllBytes(Path.Combine(here, "Bad.wav"), [1]);
        var file = Path.Combine(_root, "Bad.qmsounds");
        using (var service = Service(here))
            SoundSchemeFile.Export(file, "Bad", new Dictionary<string, string> { ["NewMail"] = "custom:Bad.wav", ["SendFailed"] = "custom:Gone.wav" }, service);

        _output.Refuse = true;
        using var other = Service(Folder("there2"));
        var imported = SoundSchemeFile.Import(file, other);

        Assert.Equal("none", imported.Events["NewMail"]);
        Assert.Equal("none", imported.Events["SendFailed"]);
        Assert.Equal(2, imported.SoundsDropped);
        Assert.Empty(other.CustomSoundFiles());
    }

    [Fact]
    public void ImportIgnoresUnsafeEntriesAndUnreadableValues()
    {
        var file = Path.Combine(_root, "Odd.qmsounds");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("scheme.json").Open()))
                writer.Write("""{"format":1,"name":"Odd\nname","events":{"NewMail":"custom:..\\..\\evil.wav","SendFailed":"builtin:nope","Bad key!":"none","MessageSent":"builtin:softbell/sent"}}""");
            zip.CreateEntry("../evil.wav");
            zip.CreateEntry("sounds/../../evil.wav");
        }
        var there = Folder("there3");
        using var service = Service(there);

        var imported = SoundSchemeFile.Import(file, service);

        Assert.Equal("Oddname", imported.Name);
        Assert.Equal(new Dictionary<string, string> { ["MessageSent"] = "builtin:softbell/sent" }, imported.Events);
        Assert.Empty(Directory.GetFiles(there));
        Assert.False(File.Exists(Path.Combine(_root, "evil.wav")));
    }

    [Theory]
    [InlineData("not a zip")]
    [InlineData("")]
    public void AFileThatIsNotASchemeIsRefusedWithAReason(string content)
    {
        var file = Path.Combine(_root, "junk.qmsounds");
        File.WriteAllText(file, content);
        using var service = Service(Folder("j"));
        var ex = Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Import(file, service));
        Assert.Contains("sound scheme", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASchemeFromANewerQuickMailSaysToUpdate()
    {
        var file = Path.Combine(_root, "new.qmsounds");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("scheme.json").Open()))
            writer.Write("""{"format":99,"name":"Future","events":{}}""");
        using var service = Service(Folder("n"));
        var ex = Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Import(file, service));
        Assert.Contains("newer version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOversizedEntryIsRefused()
    {
        var file = Path.Combine(_root, "big.qmsounds");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("scheme.json").Open()))
                writer.Write("""{"format":1,"name":"Big","events":{}}""");
            using var es = zip.CreateEntry("sounds/Big.wav").Open();
            es.Write(new byte[SoundCatalog.MaxCustomFileBytes + 1]);
        }
        using var service = Service(Folder("big"));
        Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Import(file, service));
    }

    [Fact]
    public void SettingsImportAddsTheSchemeWithAUniqueNameAndShowsIt()
    {
        var here = Folder("vm-here");
        File.WriteAllBytes(Path.Combine(here, "Ding.wav"), [1, 2, 3]);
        var file = Path.Combine(_root, "Calm.qmsounds");
        using (var service = Service(here))
            SoundSchemeFile.Export(file, "Wood", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" }, service);

        using var target = Service(Folder("vm-there"));
        var vm = new SettingsViewModel(_config, new StubCommandRegistry(), sounds: target);
        string? announced = null;
        vm.SoundResultAnnounced += text => announced = text;
        vm.PickSchemeImportPathRequested = () => file;

        vm.ImportSchemeCommand.Execute(null);

        Assert.Equal("Wood 2", vm.SelectedSoundScheme!.Name);   // "Wood" is the built-in one
        Assert.False(vm.SelectedSoundScheme.IsBuiltIn);
        Assert.Equal("New mail: Ding.wav", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail).AccessibleName);
        Assert.Equal("Message sent: None", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.MessageSent).AccessibleName);
        Assert.Equal("Added the sound scheme Wood 2, with 1 sound file. Save to keep it.", announced);
        Assert.Equal(announced, vm.SoundSchemeMessage);
    }

    [Fact]
    public void SettingsExportWritesTheSchemeShownWithItsUnsavedChanges()
    {
        using var service = Service(Folder("vm-export"));
        var vm = new SettingsViewModel(_config, new StubCommandRegistry(), sounds: service);
        vm.SelectedSoundEvent = vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail);
        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Label == "None");
        var file = Path.Combine(_root, "out.qmsounds");
        string? suggested = null;
        vm.PickSchemeExportPathRequested = name => { suggested = name; return file; };

        vm.ExportSchemeCommand.Execute(null);

        Assert.Equal("Glass.qmsounds", suggested);
        var imported = SoundSchemeFile.Import(file, service);
        Assert.Equal("Glass", imported.Name);
        Assert.Equal("none", imported.Events["NewMail"]);
        Assert.Equal("builtin:glass/sent", imported.Events["MessageSent"]);
        Assert.StartsWith("Saved the sound scheme Glass", vm.SoundSchemeMessage, StringComparison.Ordinal);
    }
}
