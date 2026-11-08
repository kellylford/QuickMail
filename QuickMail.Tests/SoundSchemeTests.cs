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
    public void WhereAGallerySchemeCameFromAndItsDescriptionRoundTrip()
    {
        var sha = new string('a', 64);
        var config = new ConfigModel();
        config.SoundSchemes["user-g"] = new SoundScheme("user-g", "Rain", isBuiltIn: false,
            events: new Dictionary<string, string> { ["NewMail"] = "none", ["FromNewerQuickMail"] = "x" },
            author: "Pat", license: "CC0-1.0", description: "Soft sounds.", galleryOrigin: new SoundGalleryOrigin("rain-at-night", sha));

        var back = RoundTrip(config).SoundSchemes["user-g"];

        Assert.Equal("Soft sounds.", back.Description);
        Assert.Equal(new SoundGalleryOrigin("rain-at-night", sha), back.GalleryOrigin);
        Assert.Equal("x", back.Events["FromNewerQuickMail"]);   // a key no event owns is kept, as before
        Assert.False(back.Events.ContainsKey("GalleryId"));
        Assert.False(back.Events.ContainsKey("Description"));
    }

    [Theory]
    [InlineData("Bad_Id", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("rain", "not a hash")]
    [InlineData("rain", null)]
    public void AGalleryOriginEditedByHandIntoNonsenseIsDropped(string id, string? sha)
    {
        var lines = new List<string> { "[sounds.user-g]", "Name = Rain", $"GalleryId = {id}" };
        if (sha is not null) lines.Add($"GallerySha256 = {sha}");
        var scheme = Parse([.. lines]).SoundSchemes["user-g"];
        Assert.Null(scheme.GalleryOrigin);
        Assert.False(scheme.Events.ContainsKey("GalleryId"));
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
        Assert.True(vm.CreateScheme("  Calm  "));

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
        vm.CreateScheme("Calm");
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
        vm.CreateScheme("Calm");
        var count = vm.SoundSchemes.Count;

        Assert.NotNull(vm.SchemeNameProblem(name));
        Assert.False(vm.CreateScheme(name));
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

        Assert.True(vm.CreateScheme("Wood mine", "wood"));

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
        vm.CreateScheme("Calm");
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
        vm.CreateScheme("Calm");
        Assert.False(vm.CanResetScheme);
    }

    [Fact]
    public void RenameKeepsTheSchemesSoundsAndRefusesATakenName()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.CreateScheme("Calm");
        vm.CreateScheme("Busy");
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Calm");
        Choose(vm, SoundEvent.NewMail, "Arrived (Wood)");

        Assert.False(vm.RenameScheme(vm.SelectedSoundScheme!.Id, "busy"));
        Assert.True(vm.RenameScheme(vm.SelectedSoundScheme!.Id, "CALM"));   // its own name, in other letters
        Assert.True(vm.RenameScheme(vm.SelectedSoundScheme!.Id, "Aaa quiet"));

        Assert.Equal("Aaa quiet", vm.SelectedSoundScheme!.Name);
        Assert.Equal("builtin:wood/arrived", Row(vm, SoundEvent.NewMail).Choice.Stored);
        Assert.Equal(["Glass", "Soft bell", "Wood", "Aaa quiet", "Busy"], vm.SoundSchemes.Select(s => s.Name));
    }

    [Fact]
    public void RenameActsOnTheSchemeItWasAskedForEvenIfAnotherIsShowingNow()
    {
        // The name dialog is modeless: the person can change Sound scheme before answering it.
        var vm = MakeSettings(new StubConfigService());
        vm.CreateScheme("Calm");
        var calm = vm.SelectedSoundScheme!.Id;
        Choose(vm, SoundEvent.NewMail, "Arrived (Wood)");
        vm.CreateScheme("Busy");
        Choose(vm, SoundEvent.MessageSent, "Sent (Glass)");   // unsaved edit to the scheme showing

        Assert.True(vm.RenameScheme(calm, "Quiet"));

        Assert.Equal("Quiet", vm.SelectedSoundScheme!.Name);
        Assert.Equal("builtin:wood/arrived", Row(vm, SoundEvent.NewMail).Choice.Stored);
        Assert.Contains(vm.SoundSchemes, s => s.Name == "Busy");
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Busy");
        Assert.Equal("builtin:glass/sent", Row(vm, SoundEvent.MessageSent).Choice.Stored);   // kept
    }

    [Fact]
    public void RenameOrCopyOfASchemeThatHasGoneFailsWithAReason()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.ConfirmRequested = (_, _) => true;
        vm.CreateScheme("Calm");
        var calm = vm.SelectedSoundScheme!.Id;
        vm.DeleteSelectedScheme();

        Assert.False(vm.RenameScheme(calm, "Quiet"));
        Assert.Equal("That sound scheme is no longer there.", vm.SchemeRenameProblem(calm, "Quiet"));
        Assert.False(vm.CreateScheme("Copy", calm));
        Assert.Null(vm.SchemeNameProblem("Copy"));   // so the View gives its own reason
        Assert.Equal(3, vm.SoundSchemes.Count);
    }

    [Fact]
    public void DeletingASchemeNotInUseLeavesTheOneInUse()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-a"] = new SoundScheme("user-a", "Calm", isBuiltIn: false);
        config.Load().SoundSchemes["user-b"] = new SoundScheme("user-b", "Busy", isBuiltIn: false,
            events: new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived" });
        config.Load().SoundSchemeId = "user-b";
        var vm = MakeSettings(config);
        vm.ConfirmRequested = (_, _) => true;
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Calm");
        vm.DeleteSelectedScheme();
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Busy");

        vm.SaveCommand.Execute(null);

        Assert.Equal("user-b", config.Load().SoundSchemeId);
        Assert.False(config.Load().SoundSchemes.ContainsKey("user-a"));
        Assert.Equal("builtin:wood/arrived", config.Load().SoundSchemes["user-b"].Events["NewMail"]);
    }

    [Fact]
    public void ASchemeRenamedButNeverEditedIsSavedWithItsNewNameAndSounds()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-a"] = new SoundScheme("user-a", "Calm", isBuiltIn: false,
            events: new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived", ["Unknown"] = "x" });
        var vm = MakeSettings(config);

        Assert.True(vm.RenameScheme("user-a", "Quiet"));
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "glass");
        vm.SaveCommand.Execute(null);

        var saved = config.Load().SoundSchemes["user-a"];
        Assert.Equal("Quiet", saved.Name);
        Assert.Equal("builtin:wood/arrived", saved.Events["NewMail"]);
        Assert.Equal("none", saved.Events["MessageSent"]);
        Assert.Equal("x", saved.Events["Unknown"]);
    }

    [Fact]
    public void ImportOrDeleteClearsAnOldSchemeMessage()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.SoundSchemeMessage = "Added the sound scheme Calm. Save to keep it.";
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Name == "Wood");
        Assert.False(vm.HasSoundSchemeMessage);
    }

    [Theory]
    [InlineData("Wood", "wood")]
    [InlineData("from-the-future", "glass")]
    public void AnOldListUnderAStyleWrittenOddlyOrUnknownStillPlays(string pack, string section)
    {
        var config = ConfigService.ParseFile(["[global]", $"SoundPack = {pack}", "[sounds]", "NewMail = none"]);
        Assert.Equal("none", config.SoundSchemes[section].Events["NewMail"]);
        Assert.Equal(SoundRef.None, SoundCatalog.Resolve(SoundEvent.NewMail, SoundCatalog.ActiveScheme(config)));
    }

    [Fact]
    public void BuiltInSchemesCannotBeRenamedOrDeleted()
    {
        var vm = MakeSettings(new StubConfigService());
        vm.ConfirmRequested = (_, _) => true;
        Assert.False(vm.IsUserSchemeSelected);
        Assert.False(vm.RenameScheme("glass", "Other"));
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

        Assert.Equal("Delete the sound scheme Calm and its sound files?", asked);
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
        vm.CreateScheme("Calm");
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
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "wood");
        Choose(vm, SoundEvent.MessageSent, "Ding.wav");

        // Only the scheme's own events can use a file in its folder (#948).
        Assert.Equal(2, vm.SoundFileUses("Ding.wav"));

        string? asked = null;
        vm.ConfirmRequested = (question, _) => { asked = question; return true; };
        Assert.True(vm.CanRemoveSoundFile);
        vm.RemoveSoundFileCommand.Execute(null);

        Assert.Equal("Remove the sound file Ding.wav from the sound scheme Wood? 2 events still use it, and will play a built-in sound instead. The file is deleted now, even if you then cancel Settings.", asked);
        Assert.Equal(["Ding.wav"], sounds.Removed);
        Assert.Equal(["wood"], sounds.RemovedFrom);
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
        Assert.Equal(1, vm.SoundFileUses("Spare.wav"));
        string? asked = null;
        vm.ConfirmRequested = (question, _) => { asked = question; return false; };
        vm.RemoveSoundFileCommand.Execute(null);
        Assert.Contains("from the sound scheme Glass? 1 event still uses it", asked, StringComparison.Ordinal);
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
    public void ResetAndRemoveSayTheyWorkedSoTheViewCanMoveFocus()
    {
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.Add("Ding.wav");
        var vm = MakeSettings(new StubConfigService(), sounds);
        vm.ConfirmRequested = (_, _) => true;
        Assert.False(vm.TryResetScheme());   // nothing to reset
        Choose(vm, SoundEvent.NewMail, "Ding.wav");
        Assert.True(vm.TryRemoveSoundFile());
        Assert.False(vm.TryRemoveSoundFile());   // gone now
        Assert.True(vm.TryResetScheme());
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

    /// <summary>The scheme these tests keep their sound files for; each scheme has a folder of its own (#948).</summary>
    private const string Scheme = "wood";

    /// <summary>A sounds folder of its own for one test, given as its <see cref="Scheme"/>'s folder, where that scheme's files go.</summary>
    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name, Scheme)).FullName;

    /// <summary>The sound service whose sounds folder holds <paramref name="folder"/> as <see cref="Scheme"/>'s folder.</summary>
    private SoundService Service(string folder) => new(_config, Path.GetDirectoryName(folder)!, _output, openBuiltIn: (pack, _) =>
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
    public void PreviewingADefaultRuleSoundPlaysTheSchemesRuleSound()
    {
        _config.Load().SoundSchemeId = "user-1";
        _config.Load().SoundSchemes["user-1"] = new SoundScheme("user-1", "Mine", false,
            events: new Dictionary<string, string> { ["RuleMatched"] = "builtin:softbell/attention" });
        using var service = Service(Folder("pr"));

        service.PreviewRule("builtin:matched");

        Assert.True(_output.Entered.Wait(TimeSpan.FromSeconds(5)));
        lock (_output.Played) Assert.Equal(["builtin:softbell/attention"], _output.Played);
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

        var outside = Path.Combine(folder, "..", "Outside.wav");
        File.WriteAllBytes(outside, [3]);

        Assert.True(service.TryRemoveCustomSound(Scheme, "Ding.wav", out _));
        Assert.True(service.TryRemoveCustomSound(Scheme, "Ding.wav", out _));   // already gone is fine
        Assert.True(service.TryRemoveCustomSound(Scheme, @"..\Outside.wav", out _));   // never outside the folder

        Assert.True(File.Exists(outside));
        Assert.Equal(["Keep.wav"], service.CustomSoundFiles(Scheme));
        Assert.Null(service.CustomSoundPath(Scheme, "Ding.wav"));
        Assert.NotNull(service.CustomSoundPath(Scheme, "Keep.wav"));
    }

    // ── Sharing a scheme ───────────────────────────────────────────────────────

    [Fact]
    public void AnExportedSchemeImportsWithItsSoundsElsewhere()
    {
        var here = Folder("here");
        var ding = TestWav.Tone();
        var otherDing = TestWav.Tone(salt: 9);
        File.WriteAllBytes(Path.Combine(here, "Ding.wav"), ding);
        var scheme = new SoundScheme("wood", "Wood", true, "wood", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav", ["MessageSent"] = "none" });
        var file = Path.Combine(_root, "Wood.qmsounds");
        using (var service = Service(here))
            Assert.Equal(0, SoundSchemeFile.Export(file, "Wood mine", SoundSchemeFile.Portable(scheme), service, Scheme));

        var there = Folder("there");
        File.WriteAllBytes(Path.Combine(there, "Ding.wav"), otherDing);   // a different sound with the same name
        using var other = Service(there);
        var imported = SoundSchemeFile.Import(file, other, Scheme);

        Assert.Equal("Wood mine", imported.Name);
        Assert.Equal("custom:Ding 2.wav", imported.Events["NewMail"]);
        Assert.Equal("none", imported.Events["MessageSent"]);
        Assert.Equal("builtin:wood/queued", imported.Events["MessageQueued"]);
        Assert.Equal(1, imported.SoundsAdded);
        Assert.Equal(0, imported.SoundsDropped);
        Assert.Equal(ding, File.ReadAllBytes(Path.Combine(there, "Ding 2.wav")));
        Assert.Equal(otherDing, File.ReadAllBytes(Path.Combine(there, "Ding.wav")));
    }

    [Fact]
    public void ASoundThatWillNotPlayIsDroppedOnImportAndCounted()
    {
        var here = Folder("here2");
        File.WriteAllBytes(Path.Combine(here, "Bad.wav"), TestWav.Tone());
        var file = Path.Combine(_root, "Bad.qmsounds");
        using (var service = Service(here))
        {
            // Gone.wav isn't there to export, so its event goes as none: one left out.
            Assert.Equal(1, SoundSchemeFile.Export(file, "Bad",
                new Dictionary<string, string> { ["NewMail"] = "custom:Bad.wav", ["SendFailed"] = "custom:Gone.wav" }, service, Scheme));
        }

        _output.Refuse = true;   // the sound won't decode here
        using var other = Service(Folder("there2"));
        var imported = SoundSchemeFile.Import(file, other, Scheme);

        Assert.Equal("none", imported.Events["NewMail"]);
        Assert.Equal("none", imported.Events["SendFailed"]);
        Assert.Equal(1, imported.SoundsDropped);
        Assert.Empty(other.CustomSoundFiles(Scheme));
    }

    [Fact]
    public void AnExportedFileSaysWhichAppItIsForAndKeepsItsDescription()
    {
        var file = Path.Combine(_root, "Described.qmsounds");
        using var service = Service(Folder("described"));
        SoundSchemeFile.Export(file, "Calm", new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived" }, service, Scheme,
            author: "Pat", license: "CC0-1.0", description: "Quiet sounds.\nFor late.");

        using (var zip = ZipFile.OpenRead(file))
        {
            Assert.Equal("scheme.json", zip.Entries[0].FullName);
            using var doc = System.Text.Json.JsonDocument.Parse(zip.Entries[0].Open());
            var root = doc.RootElement;
            Assert.Equal(["format", "app", "name", "author", "license", "description", "events"], root.EnumerateObject().Select(p => p.Name));
            Assert.Equal("quickmail", root.GetProperty("app").GetString());
            Assert.Equal("Quiet sounds.For late.", root.GetProperty("description").GetString());
            // Every event, in QuickMail's order.
            Assert.Equal(SoundCatalog.Events.Select(e => e.ConfigKey), root.GetProperty("events").EnumerateObject().Select(p => p.Name));
            Assert.Equal(new DateTime(2026, 1, 1), zip.Entries[0].LastWriteTime.DateTime);
        }

        var imported = SoundSchemeFile.Import(file, service, Scheme);
        Assert.Equal("Quiet sounds.For late.", imported.Description);
        Assert.Equal("Pat", imported.Author);
        // The same scheme gives the same bytes, which the gallery needs: it names a file by its hash.
        var again = Path.Combine(_root, "Described2.qmsounds");
        SoundSchemeFile.Export(again, "Calm", new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived" }, service, Scheme,
            author: "Pat", license: "CC0-1.0", description: "Quiet sounds.\nFor late.");
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(again));
    }

    [Fact]
    public void ASoundThatWontConvertStaysBehindWhenExportingAndIsCounted()
    {
        var here = Folder("mp3");
        File.WriteAllBytes(Path.Combine(here, "Song.mp3"), [0x49, 0x44, 0x33, 3, 0]);   // not really an MP3
        File.WriteAllBytes(Path.Combine(here, "Wide.wav"), TestWav.Tone(formatTag: 0xFFFE));   // a broken WAVE_FORMAT_EXTENSIBLE header
        var file = Path.Combine(_root, "Mp3.qmsounds");
        using var service = Service(here);

        var leftOut = SoundSchemeFile.Export(file, "Songs",
            new Dictionary<string, string> { ["NewMail"] = "custom:Song.mp3", ["SendFailed"] = "custom:Song.mp3", ["MessageSent"] = "custom:Wide.wav" }, service, Scheme);

        Assert.Equal(3, leftOut);
        using var zip = ZipFile.OpenRead(file);
        Assert.Equal(["scheme.json"], zip.Entries.Select(e => e.FullName));
    }

    /// <summary>The format tag, channels, rate and bit depth in a WAV's fmt chunk.</summary>
    private static (int Tag, int Channels, int Rate, int Bits) WavFormat(byte[] wav) =>
        (BitConverter.ToUInt16(wav, 20), BitConverter.ToUInt16(wav, 22), BitConverter.ToInt32(wav, 24), BitConverter.ToUInt16(wav, 34));

    /// <summary>Exports one event's sound and returns the scheme file read back, as every app reads it.</summary>
    private SoundSchemeFileContents ExportOne(string folder, string fileName, byte[] bytes, out int leftOut)
    {
        File.WriteAllBytes(Path.Combine(folder, fileName), bytes);
        var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".qmsounds");
        using var service = Service(folder);
        leftOut = SoundSchemeFile.Export(file, "Converted", new Dictionary<string, string> { ["NewMail"] = "custom:" + fileName }, service, Scheme);
        return SoundSchemeFile.Read(file);
    }

    [Theory]
    [InlineData(24, 1, 22050, 1, 22050)]   // 24-bit: same rate and channels, 16-bit
    [InlineData(16, 4, 22050, 2, 22050)]   // four channels: mixed down to stereo
    [InlineData(16, 1, 4000, 1, 44100)]    // a rate a scheme file can't hold: resampled to 44.1 kHz
    public void ANonPcmWavIsConvertedToSixteenBitPcmOnExport(int bits, int channels, int rate, int outChannels, int outRate)
    {
        var read = ExportOne(Folder("convert-" + bits + "-" + channels + "-" + rate), "Bell.wav",
            TestWav.Tone(bits, channels, rate, seconds: 0.2), out var leftOut);

        Assert.Equal(0, leftOut);
        Assert.Equal("custom:Bell.wav", read.Events["NewMail"]);
        var wav = read.Sounds["Bell.wav"];
        Assert.Null(SoundSchemeFile.SchemeWavProblem(wav));
        Assert.Equal((1, outChannels, outRate, 16), WavFormat(wav));
    }

    [Fact]
    public void AnMp3IsConvertedToAWavOnExportWithTheSameName()
    {
        var mp3 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAudio", "tone.mp3"));
        var probe = Path.Combine(_root, "probe.mp3");
        File.WriteAllBytes(probe, mp3);
        try
        {
            using var reader = new NAudio.Wave.MediaFoundationReader(probe);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or DllNotFoundException or TypeInitializationException)
        {
            Assert.Skip("This computer has no Media Foundation MP3 decoder, which QuickMail also needs to play MP3s: " + ex.Message);
        }

        var read = ExportOne(Folder("convert-mp3"), "Tone.mp3", mp3, out var leftOut);

        Assert.Equal(0, leftOut);
        Assert.Equal("custom:Tone.wav", read.Events["NewMail"]);
        var wav = Assert.Single(read.Sounds).Value;
        Assert.Null(SoundSchemeFile.SchemeWavProblem(wav));
        var (tag, outChannels, outRate, outBits) = WavFormat(wav);
        Assert.Equal((1, 1, 22050, 16), (tag, outChannels, outRate, outBits));
    }

    [Fact]
    public void AConvertedSoundIsNumberedWhenItsWavNameIsTaken()
    {
        var here = Folder("convert-names");
        File.WriteAllBytes(Path.Combine(here, "Bell.mp3"), [1]);
        File.WriteAllBytes(Path.Combine(here, "bell.wav"), TestWav.Tone(salt: 5));   // a different sound, already a WAV
        var converted = TestWav.Tone(salt: 9);
        var file = Path.Combine(_root, "names.qmsounds");
        using var service = Service(here);

        var leftOut = SoundSchemeFile.Export(file, "Bells",
            new Dictionary<string, string> { ["MessageSent"] = "custom:Bell.mp3", ["NewMail"] = "custom:Bell.mp3", ["SendFailed"] = "custom:bell.wav" },
            service, Scheme, toSchemeWav: _ => converted);

        Assert.Equal(0, leftOut);
        var read = SoundSchemeFile.Read(file);
        Assert.Equal("custom:Bell 2.wav", read.Events["MessageSent"]);
        Assert.Equal("custom:Bell 2.wav", read.Events["NewMail"]);
        Assert.Equal("custom:bell.wav", read.Events["SendFailed"]);
        Assert.Equal(converted, read.Sounds["Bell 2.wav"]);
        Assert.Equal(0, read.Dropped);
    }

    [Fact]
    public void AConversionOverASchemeFilesLimitsLeavesTheSoundOut()
    {
        var here = Folder("convert-long");
        File.WriteAllBytes(Path.Combine(here, "Long.wav"), TestWav.Tone(bits: 24, rate: 192000, channels: 2, seconds: 5));
        File.WriteAllBytes(Path.Combine(here, "Longer.wav"), TestWav.Tone(bits: 24, channels: 1, rate: 8000, seconds: 31));   // over 30 seconds
        var file = Path.Combine(_root, "long.qmsounds");
        using var service = Service(here);

        var leftOut = SoundSchemeFile.Export(file, "Long",
            new Dictionary<string, string> { ["NewMail"] = "custom:Long.wav", ["SendFailed"] = "custom:Longer.wav" }, service, Scheme);

        Assert.Equal(1, leftOut);   // 5 s of 192 kHz stereo is 3.8 MB as 16-bit, so it fits; 31 s doesn't
        var read = SoundSchemeFile.Read(file);
        Assert.Equal("custom:Long.wav", read.Events["NewMail"]);
        Assert.Equal("none", read.Events["SendFailed"]);
    }

    /// <summary>A .qmsounds file with this scheme.json and these entries, written as given.</summary>
    private string SchemeZip(string name, string json, params (string Name, byte[] Bytes)[] entries)
    {
        var file = Path.Combine(_root, name);
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("scheme.json").Open()))
            writer.Write(json);
        foreach (var (entryName, bytes) in entries)
        {
            using var es = zip.CreateEntry(entryName).Open();
            es.Write(bytes);
        }
        return file;
    }

    [Fact]
    public void AFileFromQuickMail0869KeepsItsMp3s()
    {
        // 0.8.69 wrote no "app", and could carry the MP3s QuickMail plays; those still come in.
        var file = SchemeZip("old.qmsounds", """{"format":1,"name":"Old","events":{"NewMail":"custom:Song.mp3"}}""",
            ("sounds/Song.mp3", [0x49, 0x44, 0x33, 3, 0]));
        using var service = Service(Folder("old"));

        var imported = SoundSchemeFile.Import(file, service, Scheme);

        Assert.Equal("custom:Song.mp3", imported.Events["NewMail"]);
        Assert.Equal(0, imported.SoundsDropped);
        Assert.Equal(["Song.mp3"], service.CustomSoundFiles(Scheme));
    }

    [Fact]
    public void ANewerFileOrAGalleryFileHoldsOnlyPlainWavs()
    {
        var events = """{"NewMail":"custom:Song.mp3","SendFailed":"custom:Wide.wav","MessageSent":"custom:Eight.wav","MessageQueued":"custom:Good.wav"}""";
        (string, byte[])[] sounds =
        [
            ("sounds/Song.mp3", [0x49, 0x44, 0x33, 3, 0]),
            ("sounds/Wide.wav", TestWav.Tone(formatTag: 0xFFFE)),
            ("sounds/Eight.wav", TestWav.Tone(bits: 8)),
            ("sounds/Good.wav", TestWav.Tone()),
        ];
        var newer = SchemeZip("new.qmsounds", $$"""{"format":1,"app":"quickmail","name":"New","events":{{events}}}""", sounds);
        var old = SchemeZip("old-gallery.qmsounds", $$"""{"format":1,"name":"Old","events":{{events}}}""", sounds);

        foreach (var read in new[] { SoundSchemeFile.Read(newer), SoundSchemeFile.Read(old, fromGallery: true) })
        {
            Assert.Equal(["Good.wav"], read.Sounds.Keys);
            Assert.Equal(3, read.Dropped);
            Assert.Equal("custom:Good.wav", read.Events["MessageQueued"]);
            Assert.Equal("none", read.Events["NewMail"]);
        }
    }

    [Theory]
    [InlineData("""{"format":1,"app":"thechatplace","name":"X","events":{}}""", "This is a sound scheme for The Chat Place, so QuickMail can't use it.")]
    [InlineData("""{"format":1,"app":"someday","name":"X","events":{}}""", "This is a sound scheme for someday, so QuickMail can't use it.")]
    [InlineData("""{"format":1,"app":5,"name":"X","events":{}}""", "This is a sound scheme for another app, so QuickMail can't use it.")]
    [InlineData("""{"format":1,"app":"\u0007","name":"X","events":{}}""", "This is a sound scheme for another app, so QuickMail can't use it.")]
    [InlineData("""{"format":1,"app":"quickmail","name":"X","app":"thechatplace","events":{}}""", "This is a sound scheme for The Chat Place, so QuickMail can't use it.")]
    [InlineData("""{"format":1.0,"name":"X","events":{}}""", "This file could not be read as a sound scheme for QuickMail.")]
    [InlineData("""{"format":1,"name":"X","events":[]}""", "This file could not be read as a sound scheme for QuickMail.")]
    [InlineData("""{"format":1,"name":"X","events":{},}""", "This file could not be read as a sound scheme for QuickMail.")]
    [InlineData("""{"format":99999999999,"name":"X","events":{}}""", "This file could not be read as a sound scheme for QuickMail.")]
    [InlineData("""{"format":2,"app":"thechatplace","name":"X","events":{}}""", "This sound scheme was made by a newer version of QuickMail. Update QuickMail and try again.")]
    public void AFileThatCantBeUsedSaysWhy(string json, string message)
    {
        var file = SchemeZip("refused.qmsounds", json);
        Assert.Equal(message, Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Read(file)).Message);
    }

    [Fact]
    public void JsonNestedEightDeepIsReadButNineIsNot()
    {
        // The outermost object is the first level, so seven arrays inside it make eight.
        Assert.Equal("X", SoundSchemeFile.Read(SchemeZip("eight.qmsounds", """{"format":1,"name":"X","events":{},"d":[[[[[[[1]]]]]]]}""")).Name);
        Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Read(SchemeZip("nine.qmsounds", """{"format":1,"name":"X","events":{},"d":[[[[[[[[1]]]]]]]]}""")));
    }

    [Fact]
    public void TheReadingRulesMatchTheSpec()
    {
        var wav = TestWav.Tone();
        // A repeated key: the last counts. A lone surrogate is removed. Case is folded for built-in sounds.
        // Knock.wav and knock.wav are one file on Windows: the first in QuickMail's event order counts.
        var json = """
            {"format":1,"app":"quickmail","name":"Before","name":"Rain \ud800","description":"  One line. ",
             "events":{"SendFailed":"custom:Knock.wav","MessageSent":"custom:knock.wav","NewMail":"BUILTIN:SoftBell/Arrived",
                       "MessageQueued":"None","WatchedMail":" none","newmail":"none","ActionFailed":"custom:missing.wav",
                       "ConnectionLost":"custom:big.wav","NewMail":"builtin:wood/arrived"}}
            """;
        var file = SchemeZip("rules.qmsounds", "﻿" + json,
            ("sounds/Knock.wav", wav), ("sounds/knock.wav", wav), ("sounds/knock.wav", TestWav.Tone(bits: 8)),
            ("sounds/big.wav", new byte[SoundCatalog.MaxCustomFileBytes + 1]), ("sounds/../evil.wav", wav), ("other.txt", [1]));

        var read = SoundSchemeFile.Read(file);

        Assert.Equal("Rain", read.Name);
        Assert.Equal("One line.", read.Description);
        Assert.Equal(new Dictionary<string, string>
        {
            ["MessageSent"] = "custom:knock.wav",   // MessageSent comes before SendFailed in QuickMail's order
            ["SendFailed"] = "none",
            ["NewMail"] = "builtin:wood/arrived",
            ["MessageQueued"] = "none",
            ["WatchedMail"] = "none",
            ["ActionFailed"] = "none",
            ["ConnectionLost"] = "none",
        }.OrderBy(e => e.Key, StringComparer.Ordinal), read.Events.OrderBy(e => e.Key, StringComparer.Ordinal));
        Assert.Equal(["knock.wav"], read.Sounds.Keys);
        Assert.Equal(wav, read.Sounds["knock.wav"]);   // the first sounds/knock.wav, not the 8-bit one after it
        Assert.Equal(3, read.Dropped);   // Knock.wav (case), missing.wav, big.wav
    }

    [Fact]
    public void ASoundAlreadyHereIsUsedWhenTheFileDoesNotCarryIt()
    {
        var file = SchemeZip("here.qmsounds", """{"format":1,"app":"quickmail","name":"X","events":{"NewMail":"custom:Mine.wav","SendFailed":"custom:Gone.wav"}}""");
        var read = SoundSchemeFile.Read(file, haveFile: name => name == "Mine.wav");
        Assert.Equal("custom:Mine.wav", read.Events["NewMail"]);
        Assert.Equal("none", read.Events["SendFailed"]);
        Assert.Equal(1, read.Dropped);
        Assert.Empty(read.Sounds);
    }

    [Fact]
    public void OnlyTheFirstHundredEventsAreRead()
    {
        var junk = string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"Junk{i}\":\"none\""));
        var file = SchemeZip("many.qmsounds", $$$"""{"format":1,"name":"X","events":{"MessageSent":"none",{{{junk}}},"NewMail":"none"}}""");
        var read = SoundSchemeFile.Read(file);
        Assert.Equal(["MessageSent"], read.Events.Keys);
    }

    [Fact]
    public void OnlyStoredAndDeflateEntriesAreRead()
    {
        // Stored is readable; a method other than stored or DEFLATE isn't, so its sound is dropped.
        var file = SchemeZip("methods.qmsounds", """{"format":1,"app":"quickmail","name":"X","events":{"NewMail":"custom:a.wav","SendFailed":"custom:b.wav"}}""");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
        {
            using (var es = zip.CreateEntry("sounds/a.wav", CompressionLevel.NoCompression).Open()) es.Write(TestWav.Tone());
            using (var es = zip.CreateEntry("sounds/b.wav", CompressionLevel.Optimal).Open()) es.Write(TestWav.Tone());
        }
        // Mark b.wav as Deflate64 (method 9) in its local and central headers: .NET could read it, other apps can't.
        var bytes = File.ReadAllBytes(file);
        var name = System.Text.Encoding.ASCII.GetBytes("sounds/b.wav");
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02
                && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
                BitConverter.GetBytes((ushort)9).CopyTo(bytes, i + 10);
        }
        File.WriteAllBytes(file, bytes);

        var read = SoundSchemeFile.Read(file);
        Assert.Equal("custom:a.wav", read.Events["NewMail"]);
        Assert.Equal("none", read.Events["SendFailed"]);
        Assert.Equal(1, read.Dropped);
    }

    [Theory]
    [InlineData(16, 1, 8000, 1, null)]
    [InlineData(16, 2, 192000, 1, null)]
    [InlineData(8, 1, 8000, 1, "16-bit")]
    [InlineData(24, 1, 8000, 1, "16-bit")]
    [InlineData(16, 3, 8000, 1, "16-bit")]
    [InlineData(16, 1, 7999, 1, "sample rate")]
    [InlineData(16, 1, 192001, 1, "sample rate")]
    [InlineData(16, 1, 8000, 0xFFFE, "PCM")]
    [InlineData(16, 1, 8000, 3, "PCM")]
    public void OnlyAPlainSixteenBitWavMayBeInASchemeFile(int bits, int channels, int rate, int tag, string? problem)
    {
        var result = SoundSchemeFile.SchemeWavProblem(TestWav.Tone(bits, channels, rate, seconds: 0.01, formatTag: (ushort)tag));
        if (problem is null) Assert.Null(result);
        else Assert.Contains(problem, result, StringComparison.Ordinal);
    }

    [Fact]
    public void AWavLongerThanThirtySecondsOrEmptyIsRefused()
    {
        Assert.Contains("30 seconds", SoundSchemeFile.SchemeWavProblem(TestWav.Tone(seconds: 0.01, declaredDataBytes: 8000 * 2 * 31)), StringComparison.Ordinal);
        Assert.Contains("no sound", SoundSchemeFile.SchemeWavProblem(TestWav.Tone(seconds: 0)), StringComparison.Ordinal);
        Assert.NotNull(SoundSchemeFile.SchemeWavProblem([1, 2, 3]));
        Assert.NotNull(SoundSchemeFile.SchemeWavProblem("RIFF\x0c\0\0\0WAVEdata\0\0\0\0"u8));   // data before fmt
    }

    [Fact]
    public void AWavIsReadOnlyAsFarAsItsRiffSizeSays()
    {
        var wav = TestWav.Tone();
        var zero = (byte[])wav.Clone();
        BitConverter.GetBytes(0u).CopyTo(zero, 4);
        Assert.NotNull(SoundSchemeFile.SchemeWavProblem(zero));
        var short_ = (byte[])wav.Clone();
        BitConverter.GetBytes(4u + 24).CopyTo(short_, 4);   // WAVE and the fmt chunk, but not data
        Assert.NotNull(SoundSchemeFile.SchemeWavProblem(short_));
        Assert.Null(SoundSchemeFile.SchemeWavProblem(wav));
    }

    [Fact]
    public void ASoundFileThatCantBeReadIsLeftOutOfAnExportRatherThanFailingIt()
    {
        var here = Folder("locked");
        File.WriteAllBytes(Path.Combine(here, "Locked.wav"), TestWav.Tone());
        File.WriteAllBytes(Path.Combine(here, "Fine.wav"), TestWav.Tone(salt: 3));
        var file = Path.Combine(_root, "locked.qmsounds");
        using var service = Service(here);
        using (new FileStream(Path.Combine(here, "Locked.wav"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, SoundSchemeFile.Export(file, "L",
                new Dictionary<string, string> { ["NewMail"] = "custom:Locked.wav", ["SendFailed"] = "custom:Fine.wav" }, service, Scheme));
        }
        Assert.Equal(["Fine.wav"], SoundSchemeFile.Read(file).Sounds.Keys);
    }

    [Theory]
    [InlineData("\u0007  Ho\u0001stile  ", 60, "Hostile")]
    [InlineData("abc  def", 4, "abc")]
    [InlineData("ab😀", 3, "ab")]
    [InlineData("\u0085\u009F", 60, "")]
    public void TextIsCleanedInTheSpecsOrder(string text, int max, string clean) =>
        Assert.Equal(clean, SoundSchemeFile.CleanText(text, max));

    [Fact]
    public void AnUnpairedSurrogateIsRemovedButAPairIsKept()
    {
        // Built here rather than in InlineData, which can't carry an unpaired surrogate intact.
        Assert.Equal("ab", SoundSchemeFile.CleanText("  a" + '\uD800' + "b ", 60));
        Assert.Equal("ab", SoundSchemeFile.CleanText("a" + '\uDC00' + "b", 60));
        Assert.Equal("a😀", SoundSchemeFile.CleanText("a😀", 60));
    }

    [Theory]
    [InlineData("CON.wav", false)]
    [InlineData("con.tar.wav", false)]
    [InlineData("COM¹.wav", false)]
    [InlineData("COM10.wav", true)]
    [InlineData("a‮wav.mp3", false)]
    [InlineData("a\u0085.wav", false)]
    [InlineData("knock.wav", true)]
    [InlineData("song.MP3", true)]
    [InlineData(" knock.wav", false)]
    [InlineData("knock.wav.", false)]
    [InlineData("\U0001F514 bell.wav", true)]   // an emoji is a surrogate pair, and fine
    [InlineData("a‍b.wav", false)]          // a zero-width joiner is a formatting character
    [InlineData("a\U000E0001.wav", false)]       // so is this one, outside the Basic Multilingual Plane
    public void ASafeFileNameIsTheSpecs(string name, bool safe) => Assert.Equal(safe, SoundRef.IsSafeCustomFileName(name));

    [Fact]
    public void ABuiltInSoundMatchesWhateverItsCase()
    {
        Assert.True(SoundRef.TryParse("BUILTIN:SoftBell/Arrived", out var sound));
        Assert.Equal("builtin:softbell/arrived", sound.ToStored());
        Assert.False(SoundRef.TryParse("builtin:chrome/sent", out _));
        Assert.False(SoundRef.TryParse("builtin:arrived/extra/x", out _));
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

        var imported = SoundSchemeFile.Import(file, service, Scheme);

        Assert.Equal("Oddname", imported.Name);
        // A value QuickMail can't use plays nothing; a key that isn't one of its events is skipped.
        Assert.Equal(new Dictionary<string, string> { ["NewMail"] = "none", ["SendFailed"] = "none", ["MessageSent"] = "builtin:softbell/sent" }
            .OrderBy(e => e.Key, StringComparer.Ordinal), imported.Events.OrderBy(e => e.Key, StringComparer.Ordinal));
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
        var ex = Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Import(file, service, Scheme));
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
        var ex = Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Import(file, service, Scheme));
        Assert.Contains("newer version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryLargerThanItsHeaderSaysNeverLandsAsASound()
    {
        var file = Path.Combine(_root, "liar.qmsounds");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("scheme.json").Open()))
                writer.Write("""{"format":1,"name":"Liar","events":{}}""");
            using var es = zip.CreateEntry("sounds/Big.wav", CompressionLevel.Optimal).Open();
            es.Write(new byte[SoundCatalog.MaxCustomFileBytes + 1]);
        }
        // Rewrite the central directory's uncompressed size for Big.wav to 1 byte: the header now lies.
        var bytes = File.ReadAllBytes(file);
        var name = System.Text.Encoding.ASCII.GetBytes("sounds/Big.wav");
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4b || bytes[i + 2] != 0x01 || bytes[i + 3] != 0x02) continue;
            if (!bytes.AsSpan(i + 46, name.Length).SequenceEqual(name)) continue;
            BitConverter.GetBytes(1u).CopyTo(bytes, i + 24);
        }
        File.WriteAllBytes(file, bytes);

        // Either the read stops at the cap and refuses the file, or the archive reader stops at the
        // length the header claims; what must never happen is the oversized sound arriving.
        using var service = Service(Folder("liar"));
        try { SoundSchemeFile.Import(file, service, Scheme); }
        catch (SoundSchemeFileException) { }
        Assert.Empty(service.CustomSoundFiles(Scheme));
    }

    [Fact]
    public void AnOversizedSoundIsDroppedAndCounted()
    {
        var file = SchemeZip("big.qmsounds", """{"format":1,"app":"quickmail","name":"Big","events":{"NewMail":"custom:Big.wav"}}""",
            ("sounds/Big.wav", new byte[SoundCatalog.MaxCustomFileBytes + 1]));
        using var service = Service(Folder("big"));
        var imported = SoundSchemeFile.Import(file, service, Scheme);
        Assert.Equal("none", imported.Events["NewMail"]);
        Assert.Equal(1, imported.SoundsDropped);
        Assert.Empty(service.CustomSoundFiles(Scheme));
    }

    [Fact]
    public void AFileLargerThanEightyMegabytesIsTooLarge()
    {
        var file = Path.Combine(_root, "huge.qmsounds");
        using (var stream = File.Create(file)) stream.SetLength(SoundSchemeFile.MaxFileBytes + 1);
        Assert.Equal("This sound scheme is too large to use.", Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Read(file)).Message);
    }

    [Fact]
    public void ASoundIsNeverWrittenOverAFolderOfTheSameName()
    {
        var file = SchemeZip("folder.qmsounds", """{"format":1,"app":"quickmail","name":"X","events":{"NewMail":"custom:Ding.wav"}}""",
            ("sounds/Ding.wav", TestWav.Tone()));
        var there = Folder("folder-clash");
        Directory.CreateDirectory(Path.Combine(there, "Ding.wav"));
        using var service = Service(there);
        var imported = SoundSchemeFile.Import(file, service, Scheme);
        Assert.Equal("custom:Ding 2.wav", imported.Events["NewMail"]);
        Assert.True(File.Exists(Path.Combine(there, "Ding 2.wav")));
    }

    [Fact]
    public void SettingsImportAddsTheSchemeWithAUniqueNameAndShowsIt()
    {
        var here = Folder("vm-here");
        File.WriteAllBytes(Path.Combine(here, "Ding.wav"), TestWav.Tone());
        var file = Path.Combine(_root, "Calm.qmsounds");
        using (var service = Service(here))
            SoundSchemeFile.Export(file, "Wood", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" }, service, Scheme);

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
        var imported = SoundSchemeFile.Import(file, service, Scheme);
        Assert.Equal("Glass", imported.Name);
        Assert.Equal("none", imported.Events["NewMail"]);
        Assert.Equal("builtin:glass/sent", imported.Events["MessageSent"]);
        Assert.StartsWith("Saved the sound scheme Glass", vm.SoundSchemeMessage, StringComparison.Ordinal);
    }
}
