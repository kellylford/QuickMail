using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Scheme folders no scheme owns any more (#955), against real folders: what is removed, and every
/// way a folder is kept.
/// </summary>
public sealed class SoundSchemeFolderCleanupTests : IDisposable
{
    private const string Calm = "user-0123456789abcdef0123456789abcdef";
    private const string Gone = "user-fedcba9876543210fedcba9876543210";
    private const string Other = "user-00000000000000000000000000000001";

    private readonly string _profile = Path.Combine(Path.GetTempPath(), $"QM-SFC-{Guid.NewGuid():N}");
    private string Sounds => Path.Combine(_profile, SoundCatalog.CustomFolderName);

    public SoundSchemeFolderCleanupTests() => Directory.CreateDirectory(Sounds);

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); } catch (IOException) { }
    }

    private void Config(params SoundScheme[] schemes) => ConfigIn(_profile, schemes);

    private static void ConfigIn(string dir, params SoundScheme[] schemes)
    {
        var config = new ConfigModel();
        foreach (var scheme in schemes) config.SoundSchemes[scheme.Id] = scheme;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.ini"), ConfigService.BuildFileText(config));
    }

    private static SoundScheme Own(string id) =>
        new(id, "Mine", isBuiltIn: false, events: new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" });

    private string Folder(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Sounds, name)).FullName;
        File.WriteAllBytes(Path.Combine(dir, "Ding.wav"), [1, 2, 3]);
        return dir;
    }

    /// <summary>A scheme folder made for a scheme not saved yet, written down as Settings would.</summary>
    private string Unsaved(string id)
    {
        Assert.True(SoundSchemeFolderCleanup.NoteUnsaved(Sounds, [id]));
        return Folder(id);
    }

    private HashSet<string> Journal => SoundSchemeFolderCleanup.ReadJournal(Sounds);

    [Fact]
    public void AnUnsavedFolderNoSchemeOwnsIsRemovedAndEverythingElseStays()
    {
        Config(Own(Calm));
        var calm = Folder(Calm);
        var gone = Unsaved(Gone);
        var glass = Folder("glass");                  // a built-in scheme's, never touched
        var notAScheme = Folder("notes");             // not a scheme's folder name at all
        var almost = Folder("user-calm_x");           // a character a scheme id can't have
        var rules = Path.Combine(Sounds, "Pat.wav");  // a rule's sound, in the sounds folder itself
        File.WriteAllBytes(rules, [4]);

        Assert.Equal(1, SoundSchemeFolderCleanup.Run(_profile, []));

        Assert.False(Directory.Exists(gone));
        Assert.True(File.Exists(Path.Combine(calm, "Ding.wav")));
        Assert.True(Directory.Exists(glass));
        Assert.True(Directory.Exists(notAScheme));
        Assert.True(Directory.Exists(almost));
        Assert.True(File.Exists(rules));
        Assert.Empty(Journal);
        Assert.False(File.Exists(Path.Combine(Sounds, SoundSchemeFolderCleanup.JournalFileName)));
    }

    [Fact]
    public void AFolderNeverWrittenDownIsNeverRemovedEvenIfNothingOwnsIt()
    {
        // A config.ini deleted or reset to the defaults must not make every scheme of your own look
        // like it belongs to nothing (#955 review).
        Config();
        var mine = Folder(Gone);
        Unsaved(Other);

        Assert.Equal(1, SoundSchemeFolderCleanup.Run(_profile, []));

        Assert.True(File.Exists(Path.Combine(mine, "Ding.wav")));
    }

    [Fact]
    public void AtStartupAfterConfigWasDeletedAndRecreatedYourSchemesStay()
    {
        // The real order at startup: ConfigService.Load writes a default config.ini when there is
        // none, and the tidy-up runs after it.
        var mine = Folder(Calm);
        new ConfigService(new ProfileContext(_profile)).Load();
        Assert.True(File.Exists(Path.Combine(_profile, "config.ini")));

        SoundSchemeFolderCleanup.RunAtStartup(_profile);

        Assert.True(File.Exists(Path.Combine(mine, "Ding.wav")));
    }

    [Fact]
    public void AnUnsavedFolderThatIsNowOwnedIsKeptAndCrossedOff()
    {
        // Crossed off, so a config.ini lost later can't make it look unsaved again.
        Config(Own(Calm));
        var calm = Unsaved(Calm);

        Assert.Equal(0, SoundSchemeFolderCleanup.Run(_profile, []));

        Assert.True(Directory.Exists(calm));
        Assert.Empty(Journal);
    }

    [Fact]
    public void ForgettingASavedSchemeCrossesItOffAndKeepsTheRest()
    {
        Unsaved(Calm);
        Unsaved(Other);

        Assert.True(SoundSchemeFolderCleanup.ForgetUnsaved(Sounds, [Calm.ToUpperInvariant(), "glass"]));

        Assert.Equal([Other], Journal);
    }

    [Fact]
    public void TheListIgnoresAnythingThatIsntASchemeOfYourOwn()
    {
        File.WriteAllLines(Path.Combine(Sounds, SoundSchemeFolderCleanup.JournalFileName), ["glass", "..", @"user-..\x", "", "notes", Calm]);
        Config();
        Folder("glass");
        Folder("notes");

        SoundSchemeFolderCleanup.Run(_profile, []);

        Assert.True(Directory.Exists(Path.Combine(Sounds, "glass")));
        Assert.True(Directory.Exists(Path.Combine(Sounds, "notes")));
        Assert.True(Directory.Exists(Sounds));
    }

    [Fact]
    public void ASchemeOpenInSettingsKeepsItsFolderThoughConfigDoesNotHaveIt()
    {
        Config(Own(Calm));
        var unsaved = Unsaved(Other);

        Assert.Equal(0, SoundSchemeFolderCleanup.Run(_profile, [Other]));

        Assert.True(Directory.Exists(unsaved));
        Assert.Equal([Other], Journal);   // still unsaved: if Settings never saves it, it goes later
    }

    [Fact]
    public void ASchemeConfigMentionsOnlyAsTheOneInUseKeepsItsFolder()
    {
        // Named anywhere in config.ini is enough, such as by a line a newer QuickMail wrote.
        Config(Own(Calm));
        File.AppendAllText(Path.Combine(_profile, "config.ini"), $"{Environment.NewLine}[later]{Environment.NewLine}Scheme = {Other.ToUpperInvariant()}{Environment.NewLine}");
        var mentioned = Unsaved(Other);

        SoundSchemeFolderCleanup.Run(_profile, []);

        Assert.True(Directory.Exists(mentioned));
    }

    [Fact]
    public void AFolderAnImportBackupStillNamesIsKept()
    {
        // An import replaced this computer's schemes; undoing it by hand from the backup must still
        // find the replaced scheme's sounds.
        Config(Own(Calm));
        ConfigIn(Path.Combine(_profile, "backup-20261108-101500"), Own(Gone));
        var replaced = Unsaved(Gone);

        Assert.Equal(0, SoundSchemeFolderCleanup.Run(_profile, []));

        Assert.True(Directory.Exists(replaced));
    }

    [Fact]
    public void WithoutAConfigNothingIsRemoved()
    {
        var folder = Unsaved(Gone);

        Assert.Null(SoundSchemeFolderCleanup.Run(_profile, []));

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void AConfigFromBeforeSoundsOrNotWholeRemovesNothing()
    {
        // No PlayEventSounds: its having no schemes proves nothing.
        File.WriteAllText(Path.Combine(_profile, "config.ini"), "[general]" + Environment.NewLine + "PreviewLines = 2" + Environment.NewLine);
        var folder = Unsaved(Gone);

        Assert.Null(SoundSchemeFolderCleanup.Run(_profile, []));

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void AnEmptyConfigRemovesNothing()
    {
        File.WriteAllText(Path.Combine(_profile, "config.ini"), string.Empty);
        var folder = Unsaved(Gone);

        Assert.Null(SoundSchemeFolderCleanup.Run(_profile, []));

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void AConfigThatCannotBeReadRemovesNothing()
    {
        Config(Own(Calm));
        var folder = Unsaved(Gone);
        using var locked = new FileStream(Path.Combine(_profile, "config.ini"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Null(SoundSchemeFolderCleanup.Run(_profile, []));

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void NoSoundsFolderIsNothingToDo()
    {
        Directory.Delete(Sounds);
        Config(Own(Calm));

        Assert.Equal(0, SoundSchemeFolderCleanup.Run(_profile, []));
    }

    [Theory]
    [InlineData(Calm, true)]
    [InlineData("USER-0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("glass", false)]
    [InlineData("user-", false)]
    [InlineData("user-a_b", false)]
    [InlineData("user-a.b", false)]
    [InlineData("notes", false)]
    public void OnlyAFolderNamedLikeASchemeOfYourOwnIsEverLookedAt(string name, bool looked)
    {
        Assert.Equal(looked, SoundSchemeFolderCleanup.IsUserSchemeFolderName(name));
    }

    [Fact]
    public void TheSoundServiceSweepsOnlyAProfilesSoundsFolder()
    {
        Config(Own(Calm));
        var gone = Folder(Gone);
        using (var noting = new SoundService(new StubConfigService(), Sounds, new NoOutput()))
            noting.NoteUnsavedSchemeFolders([Gone]);
        Assert.Equal([Gone], Journal);
        using (var elsewhere = new SoundService(new StubConfigService(), Path.Combine(_profile, "other"), new NoOutput()))
            Assert.Null(elsewhere.RemoveUnusedSchemeFolders([]));   // not a "sounds" folder: nothing to check it against
        Assert.True(Directory.Exists(gone));

        using var service = new SoundService(new StubConfigService(), Sounds, new NoOutput());
        Assert.Equal(1, service.RemoveUnusedSchemeFolders([Calm]));
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public void AtStartupItKeepsWhatConfigHasAndRemovesWhatWasLeftUnsaved()
    {
        Config(Own(Calm));
        var calm = Folder(Calm);
        var gone = Unsaved(Gone);

        SoundSchemeFolderCleanup.RunAtStartup(_profile);

        Assert.True(Directory.Exists(calm));
        Assert.False(Directory.Exists(gone));
    }

    internal sealed class NoOutput : ISoundOutput
    {
        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken) { }
        public void Check(string filePath) { }
    }
}

/// <summary>Settings and the follow-ups to each scheme's folder (#955).</summary>
public sealed class SoundSchemeFolderTidySettingsTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else File.Delete(path);
            }
            catch (IOException) { }
        }
    }

    private static SettingsViewModel MakeSettings(StubConfigService config, ISoundService sounds) =>
        new(config, new StubCommandRegistry(), sounds: sounds);

    private static StubConfigService WithGlass(params (string Key, string Value)[] events)
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["glass"] = new SoundScheme("glass", "Glass", isBuiltIn: true, pack: "glass",
            events: events.Select(e => KeyValuePair.Create(e.Key, e.Value)));
        return config;
    }

    // ── Folders no scheme owns ─────────────────────────────────────────────────

    [Fact]
    public void OpeningSettingsRemovesFoldersNoSavedSchemeOwns()
    {
        var config = WithGlass();
        config.Load().SoundSchemes["user-calm"] = new SoundScheme("user-calm", "Calm", false);
        var sounds = new RecordingSoundService();

        MakeSettings(config, sounds);

        var sweep = Assert.Single(sounds.UnusedFolderSweeps);
        Assert.Contains("user-calm", sweep);
    }

    [Fact]
    public void SavingKeepsTheFoldersOfEverySchemeMadeHere()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithGlass(), sounds);
        Assert.True(vm.CreateScheme("Brand new"));
        var made = vm.SelectedSoundScheme!.Id;

        vm.SaveCommand.Execute(null);

        var sweep = sounds.UnusedFolderSweeps.Last();
        Assert.Contains(made, sweep);
        Assert.Contains("glass", sweep);
    }

    [Fact]
    public void SchemesMadeInSettingsAreWrittenDownUntilSavedOrCancelled()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithGlass(), sounds);
        vm.CreateScheme("New one");
        var made = vm.SelectedSoundScheme!.Id;
        vm.CreateScheme("Copied", "glass");
        var copied = vm.SelectedSoundScheme!.Id;
        Assert.Equal(2, sounds.UnsavedFolders.Count);
        Assert.Contains(made, sounds.UnsavedFolders);
        Assert.Contains(copied, sounds.UnsavedFolders);

        vm.SaveCommand.Execute(null);

        Assert.Empty(sounds.UnsavedFolders);
    }

    [Fact]
    public void CancelCrossesOffTheSchemesWhoseFoldersItDeleted()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithGlass(), sounds);
        vm.CreateScheme("New one");

        vm.DiscardUnsavedSchemeSounds();

        Assert.Empty(sounds.UnsavedFolders);
    }

    [Fact]
    public void AnImportedSchemeIsWrittenDownBeforeItsSoundsLand()
    {
        var file = Path.Combine(TempDir(), "rain.qmsounds");
        SoundSchemeFile.Export(file, "Rain", new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived" }, new RecordingSoundService(), "wood");
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(new StubConfigService(), sounds);

        Assert.True(vm.ImportSchemeFrom(file));

        Assert.Equal([vm.SelectedSoundScheme!.Id], sounds.UnsavedFolders);
    }

    [Fact]
    public void CancelNeverSweeps()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithGlass(), sounds);
        vm.CreateScheme("Brand new");

        vm.DiscardUnsavedSchemeSounds();

        Assert.Single(sounds.UnusedFolderSweeps);   // the one when Settings opened
    }

    [Fact]
    public void ASchemeMadeInSettingsKeepsItsRealFolderUntilSaveOrCancel()
    {
        // Against real folders: a scheme made here isn't in config.ini yet, and opening the Sounds
        // tab, or anything else, must not lose its files before Save.
        var profile = TempDir();
        var config = WithGlass();
        File.WriteAllText(Path.Combine(profile, "config.ini"), ConfigService.BuildFileText(config.Load()));
        using var sounds = new SoundService(config, Path.Combine(profile, SoundCatalog.CustomFolderName), new SoundSchemeFolderCleanupTests.NoOutput());
        var vm = MakeSettings(config, sounds);
        vm.PickSoundFileRequested = () => Wav("Mine.wav", 1);

        Assert.True(vm.CreateScheme("Mine"));
        var id = vm.SelectedSoundScheme!.Id;
        Assert.True(vm.TryChooseSoundFile());
        var file = sounds.CustomSoundPath(id, "Mine.wav");
        Assert.NotNull(file);

        Assert.Equal(0, sounds.RemoveUnusedSchemeFolders([id]));   // as Settings would name it
        vm.SaveCommand.Execute(null);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void ASchemeLeftUnsavedByAStopIsRemovedAtTheNextStartAndSavedOnesStay()
    {
        // QuickMail stops while Settings has a new scheme with a file in it: neither Save nor Cancel.
        var profile = TempDir();
        var config = WithGlass();
        config.Load().SoundSchemes["user-calm"] = new SoundScheme("user-calm", "Calm", false);
        File.WriteAllText(Path.Combine(profile, "config.ini"), ConfigService.BuildFileText(config.Load()));
        var calm = Directory.CreateDirectory(Path.Combine(profile, "sounds", "user-calm")).FullName;
        File.WriteAllBytes(Path.Combine(calm, "Mine.wav"), TestWav.Tone(salt: 3));
        string left;
        using (var sounds = new SoundService(config, Path.Combine(profile, SoundCatalog.CustomFolderName), new SoundSchemeFolderCleanupTests.NoOutput()))
        {
            var vm = MakeSettings(config, sounds);
            vm.PickSoundFileRequested = () => Wav("New.wav", 4);
            Assert.True(vm.CreateScheme("Unsaved"));
            Assert.True(vm.TryChooseSoundFile());
            left = Path.Combine(profile, "sounds", SoundCatalog.SchemeFolderName(vm.SelectedSoundScheme!.Id)!);
            Assert.True(Directory.Exists(left));
        }

        SoundSchemeFolderCleanup.RunAtStartup(profile);

        Assert.False(Directory.Exists(left));
        Assert.True(File.Exists(Path.Combine(calm, "Mine.wav")));
    }

    // ── Reset ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ResetThenSaveDeletesTheFilesTheSchemeNoLongerUses()
    {
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.AddRange(["Ding.wav", "Unused.wav"]);
        var vm = MakeSettings(WithGlass(("NewMail", "custom:Ding.wav")), sounds);
        vm.ConfirmRequested = (_, _) => true;

        Assert.True(vm.TryResetScheme());
        Assert.Empty(sounds.Removed);   // not yet: Cancel would keep them
        vm.SaveCommand.Execute(null);

        Assert.Equal(["Ding.wav", "Unused.wav"], sounds.Removed.Order(StringComparer.Ordinal));
        Assert.All(sounds.RemovedFrom, f => Assert.Equal("glass", f));
    }

    [Fact]
    public void ResetThenCancelDeletesNothing()
    {
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.Add("Ding.wav");
        var vm = MakeSettings(WithGlass(("NewMail", "custom:Ding.wav")), sounds);
        vm.ConfirmRequested = (_, _) => true;
        vm.TryResetScheme();

        vm.DiscardUnsavedSchemeSounds();

        Assert.Empty(sounds.Removed);
    }

    [Fact]
    public void AFileChosenAfterResetIsKept()
    {
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.AddRange(["Ding.wav", "Keep.wav"]);
        var vm = MakeSettings(WithGlass(("NewMail", "custom:Ding.wav")), sounds);
        vm.ConfirmRequested = (_, _) => true;
        vm.TryResetScheme();
        vm.SelectedSoundEvent = vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.MessageSent);
        vm.PickSoundFileRequested = () => @"C:\Music\Keep.wav";
        Assert.True(vm.TryChooseSoundFile());

        vm.SaveCommand.Execute(null);

        Assert.Equal(["Ding.wav"], sounds.Removed);
    }

    [Fact]
    public void ResetOfAnotherSchemeLeavesTheSchemeShowingAlone()
    {
        // Only the scheme that was reset loses files, even if Save happens on another.
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.Add("Ding.wav");
        var config = WithGlass(("NewMail", "custom:Ding.wav"));
        config.Load().SoundSchemes["wood"] = new SoundScheme("wood", "Wood", isBuiltIn: true, pack: "wood",
            events: new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" });
        var vm = MakeSettings(config, sounds);
        vm.ConfirmRequested = (_, _) => true;
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "wood");
        vm.TryResetScheme();
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "glass");

        vm.SaveCommand.Execute(null);

        Assert.Equal(["wood"], sounds.RemovedFrom.Distinct());
    }

    [Fact]
    public void ResetSaysTheFilesGoOnlyWhenThereAreAny()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithGlass(("NewMail", "none")), sounds);
        string? asked = null;
        vm.ConfirmRequested = (q, _) => { asked = q; return false; };

        vm.TryResetScheme();
        Assert.DoesNotContain("sound files", asked, StringComparison.Ordinal);

        sounds.CustomFiles.Add("Ding.wav");
        vm.TryResetScheme();
        Assert.Contains("The sound files you added to it are deleted when you save", asked, StringComparison.Ordinal);
    }

    // ── A gallery update, then Cancel ───────────────────────────────────────────

    [Fact]
    public void CancelAfterAGalleryUpdateTakesOutOnlyTheFilesTheUpdateBrought()
    {
        var profile = TempDir();
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-rain"] = new SoundScheme("user-rain", "My rain", false,
            events: new Dictionary<string, string> { ["NewMail"] = "custom:Old.wav" },
            galleryOrigin: new SoundGalleryOrigin("rain", new string('1', 64)));
        using var sounds = new SoundService(config, Path.Combine(profile, SoundCatalog.CustomFolderName), new SoundSchemeFolderCleanupTests.NoOutput());
        var rain = Directory.CreateDirectory(Path.Combine(profile, "sounds", "user-rain")).FullName;
        File.WriteAllBytes(Path.Combine(rain, "Old.wav"), TestWav.Tone(salt: 1));
        var update = GalleryFile(sounds, profile);

        var vm = MakeSettings(config, sounds);
        vm.UpdateChoiceRequested = _ => SoundSchemeUpdateChoice.ReplaceEverything;
        Assert.True(vm.ImportSchemeFrom(update, new SoundGalleryEntry("rain", "Rain", "Pat", "", "CC0-1.0", "rain.qmsounds", 1, new string('2', 64), null)));
        Assert.True(File.Exists(Path.Combine(rain, "New.wav")));

        vm.DiscardUnsavedSchemeSounds();

        Assert.False(File.Exists(Path.Combine(rain, "New.wav")));
        Assert.True(File.Exists(Path.Combine(rain, "Old.wav")));   // what the saved scheme still uses
    }

    [Fact]
    public void SaveAfterAGalleryUpdateDeletesTheOldVersionsFilesItNoLongerUses()
    {
        var profile = TempDir();
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-rain"] = new SoundScheme("user-rain", "My rain", false,
            events: new Dictionary<string, string> { ["NewMail"] = "custom:Old.wav", ["MessageSent"] = "custom:Shared.wav" },
            galleryOrigin: new SoundGalleryOrigin("rain", new string('1', 64)));
        using var sounds = new SoundService(config, Path.Combine(profile, SoundCatalog.CustomFolderName), new SoundSchemeFolderCleanupTests.NoOutput());
        var rain = Directory.CreateDirectory(Path.Combine(profile, "sounds", "user-rain")).FullName;
        File.WriteAllBytes(Path.Combine(rain, "Old.wav"), TestWav.Tone(salt: 1));
        File.WriteAllBytes(Path.Combine(rain, "Unused.wav"), TestWav.Tone(salt: 5));
        var update = GalleryFile(sounds, profile);   // NewMail plays its own New.wav

        var vm = MakeSettings(config, sounds);
        vm.UpdateChoiceRequested = _ => SoundSchemeUpdateChoice.ReplaceEverything;
        Assert.True(vm.ImportSchemeFrom(update, new SoundGalleryEntry("rain", "Rain", "Pat", "", "CC0-1.0", "rain.qmsounds", 1, new string('2', 64), null)));
        // After the update, a file of the person's own for another event: it is used, so it stays.
        vm.SelectedSoundEvent = vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.TrashEmptied);
        vm.PickSoundFileRequested = () => Wav("Mine.wav", 6);
        Assert.True(vm.TryChooseSoundFile());

        vm.SaveCommand.Execute(null);

        Assert.Equal(["Mine.wav", "New.wav"], Directory.GetFiles(rain).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AGalleryUpdateDeletesNothingOnCancelButWhatItBrought()
    {
        var profile = TempDir();
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-rain"] = new SoundScheme("user-rain", "My rain", false,
            events: new Dictionary<string, string> { ["NewMail"] = "custom:Old.wav" },
            galleryOrigin: new SoundGalleryOrigin("rain", new string('1', 64)));
        using var sounds = new SoundService(config, Path.Combine(profile, SoundCatalog.CustomFolderName), new SoundSchemeFolderCleanupTests.NoOutput());
        var rain = Directory.CreateDirectory(Path.Combine(profile, "sounds", "user-rain")).FullName;
        File.WriteAllBytes(Path.Combine(rain, "Old.wav"), TestWav.Tone(salt: 1));
        File.WriteAllBytes(Path.Combine(rain, "Unused.wav"), TestWav.Tone(salt: 5));
        var update = GalleryFile(sounds, profile);

        var vm = MakeSettings(config, sounds);
        vm.UpdateChoiceRequested = _ => SoundSchemeUpdateChoice.ReplaceEverything;
        vm.ImportSchemeFrom(update, new SoundGalleryEntry("rain", "Rain", "Pat", "", "CC0-1.0", "rain.qmsounds", 1, new string('2', 64), null));
        vm.DiscardUnsavedSchemeSounds();

        Assert.Equal(["Old.wav", "Unused.wav"], Directory.GetFiles(rain).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SaveAfterAGalleryUpdateKeepsWhatItBrought()
    {
        var profile = TempDir();
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-rain"] = new SoundScheme("user-rain", "My rain", false,
            galleryOrigin: new SoundGalleryOrigin("rain", new string('1', 64)));
        using var sounds = new SoundService(config, Path.Combine(profile, SoundCatalog.CustomFolderName), new SoundSchemeFolderCleanupTests.NoOutput());
        var update = GalleryFile(sounds, profile);

        var vm = MakeSettings(config, sounds);
        vm.UpdateChoiceRequested = _ => SoundSchemeUpdateChoice.ReplaceEverything;
        vm.ImportSchemeFrom(update, new SoundGalleryEntry("rain", "Rain", "Pat", "", "CC0-1.0", "rain.qmsounds", 1, new string('2', 64), null));
        vm.SaveCommand.Execute(null);
        vm.DiscardUnsavedSchemeSounds();

        Assert.True(File.Exists(Path.Combine(profile, "sounds", "user-rain", "New.wav")));
    }

    // ── Copy scheme's message ────────────────────────────────────────────────────

    [Fact]
    public void ACopyThatCouldNotBringEveryFileSaysSoInTheStatusTextAsWell()
    {
        var sounds = new RecordingSoundService { CopySchemeSoundsFails = true };
        var vm = MakeSettings(WithGlass(), sounds);
        var announced = new List<string>();
        vm.SoundResultAnnounced += announced.Add;

        Assert.True(vm.CreateScheme("Copy", "glass"));

        Assert.StartsWith("Not all of the sound files of Glass could be copied", vm.SoundSchemeMessage, StringComparison.Ordinal);
        Assert.Equal([vm.SoundSchemeMessage], announced);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QM-SFT-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return dir;
    }

    private string Wav(string name, byte salt)
    {
        var dir = TempDir();
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, TestWav.Tone(salt: salt));
        return path;
    }

    /// <summary>A .qmsounds file whose NewMail plays its own New.wav, made from a scratch scheme folder that is then removed.</summary>
    private string GalleryFile(SoundService sounds, string profile)
    {
        var scratch = Directory.CreateDirectory(Path.Combine(profile, "sounds", "wood")).FullName;
        File.WriteAllBytes(Path.Combine(scratch, "New.wav"), TestWav.Tone(salt: 2));
        var file = Path.Combine(TempDir(), "rain.qmsounds");
        SoundSchemeFile.Export(file, "Rain", new Dictionary<string, string> { ["NewMail"] = "custom:New.wav" }, sounds, "wood");
        Directory.Delete(scratch, recursive: true);
        return file;
    }
}

/// <summary>Holding a result back until a dialog has closed (#955).</summary>
public class HeldAnnouncementsTests
{
    [Fact]
    public void NothingIsHeldUnlessHolding()
    {
        var held = new HeldAnnouncements();

        Assert.False(held.TryHold("Saved."));
        Assert.Null(held.TakeAll());
    }

    [Fact]
    public void WhatIsOfferedWhileHoldingIsHeldThenTakenOnce()
    {
        var held = new HeldAnnouncements();
        held.Hold();
        Assert.True(held.TryHold("Not all of the sound files could be copied."));
        Assert.True(held.TryHold("Second."));
        held.Stop();

        Assert.False(held.TryHold("After."));   // said at once
        Assert.Equal("Not all of the sound files could be copied. Second.", held.TakeAll());
        Assert.Null(held.TakeAll());
    }

    [Fact]
    public void NestedHoldsLastUntilTheOutermostStops()
    {
        var held = new HeldAnnouncements();
        held.Hold();
        held.Hold();
        held.Stop();
        Assert.True(held.IsHolding);
        held.Stop();
        held.Stop();   // one too many does no harm
        Assert.False(held.IsHolding);
    }
}
