using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Each sound scheme's files in a folder of its own (#948): the one-time move of 0.8.69's shared
/// folder, against real files in a temp folder.
/// </summary>
public sealed class SoundFolderMigrationTests : IDisposable
{
    private readonly string _sounds = Path.Combine(Path.GetTempPath(), $"QM-MIG-{Guid.NewGuid():N}");

    public SoundFolderMigrationTests() => Directory.CreateDirectory(_sounds);

    public void Dispose()
    {
        try { Directory.Delete(_sounds, recursive: true); } catch (IOException) { }
    }

    private const string Calm = "user-0123456789abcdef0123456789abcdef";

    private static SoundScheme Own(string id, params (string Key, string Value)[] events) =>
        new(id, id, isBuiltIn: false, events: events.Select(e => KeyValuePair.Create(e.Key, e.Value)));

    private static SoundScheme BuiltIn(string id, params (string Key, string Value)[] events) =>
        new(id, SoundCatalog.PackLabel(id), isBuiltIn: true, pack: id, events: events.Select(e => KeyValuePair.Create(e.Key, e.Value)));

    private string Shared(string name, params byte[] bytes)
    {
        var path = Path.Combine(_sounds, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string InScheme(string folder, string name) => Path.Combine(_sounds, folder, name);

    private bool Done => File.Exists(Path.Combine(_sounds, SoundFolderMigration.MarkerFileName));

    [Fact]
    public void AFileOneSchemeUsesMovesIntoItsFolder()
    {
        var shared = Shared("Ding.wav", 1, 2, 3);

        SoundFolderMigration.Run(_sounds, [BuiltIn("glass", ("NewMail", "custom:Ding.wav"))], []);

        Assert.Equal([(byte)1, 2, 3], File.ReadAllBytes(InScheme("glass", "Ding.wav")));
        Assert.False(File.Exists(shared));
        Assert.True(Done);
    }

    [Fact]
    public void AFileTwoSchemesUseIsCopiedIntoEachThenRemoved()
    {
        var shared = Shared("Ding.wav", 7);

        SoundFolderMigration.Run(_sounds,
            [BuiltIn("wood", ("NewMail", "custom:Ding.wav")), Own(Calm, ("MessageSent", "custom:Ding.wav"), ("NewMail", "custom:Ding.wav"))], []);

        Assert.Equal([(byte)7], File.ReadAllBytes(InScheme("wood", "Ding.wav")));
        Assert.Equal([(byte)7], File.ReadAllBytes(InScheme(Calm, "Ding.wav")));
        Assert.False(File.Exists(shared));
    }

    [Fact]
    public void AFileNothingUsesStaysWhereItIs()
    {
        var unused = Shared("Unused.wav", 4);
        Shared("Ding.wav", 5);

        SoundFolderMigration.Run(_sounds, [BuiltIn("glass", ("NewMail", "custom:Ding.wav"))], []);

        Assert.True(File.Exists(unused));
        Assert.False(Directory.Exists(Path.Combine(_sounds, "wood")));
        Assert.Equal(["Ding.wav"], Directory.GetFiles(Path.Combine(_sounds, "glass")).Select(Path.GetFileName));
    }

    [Fact]
    public void AFileARuleUsesStaysForTheRuleAndIsCopiedForTheScheme()
    {
        var shared = Shared("Pat.wav", 6);

        SoundFolderMigration.Run(_sounds, [Own(Calm, ("NewMail", "custom:Pat.wav"))], ["custom:Pat.wav", "builtin:matched", null]);

        Assert.True(File.Exists(shared));   // where rules play from
        Assert.Equal([(byte)6], File.ReadAllBytes(InScheme(Calm, "Pat.wav")));
    }

    [Fact]
    public void NamesDifferingOnlyInCaseAreTheSameFile()
    {
        var shared = Shared("Ding.wav", 8);

        SoundFolderMigration.Run(_sounds, [BuiltIn("glass", ("NewMail", "custom:DING.wav"))], ["custom:ding.WAV"]);

        Assert.True(File.Exists(InScheme("glass", "DING.wav")));
        Assert.True(File.Exists(shared));   // a rule uses it, whatever the case
    }

    [Fact]
    public void ItHappensOnceAndLaterStartsLeaveEverythingAlone()
    {
        Shared("Pat.wav", 6);
        var schemes = new[] { Own(Calm, ("NewMail", "custom:Pat.wav")) };
        SoundFolderMigration.Run(_sounds, schemes, ["custom:Pat.wav"]);
        Assert.True(Done);

        // Someone removes the scheme's copy in Settings. The rule's copy must not bring it back.
        File.Delete(InScheme(Calm, "Pat.wav"));
        SoundFolderMigration.Run(_sounds, schemes, ["custom:Pat.wav"]);

        Assert.False(File.Exists(InScheme(Calm, "Pat.wav")));
    }

    [Fact]
    public void PlacingAgainChangesNothing()
    {
        Shared("Ding.wav", 1);
        Shared("Pat.wav", 2);
        var schemes = new[] { BuiltIn("glass", ("NewMail", "custom:Ding.wav"), ("SendFailed", "custom:Pat.wav")) };

        Assert.True(SoundFolderMigration.Place(_sounds, schemes, ["custom:Pat.wav"], removeFromShared: true));
        var after = Snapshot();
        Assert.True(SoundFolderMigration.Place(_sounds, schemes, ["custom:Pat.wav"], removeFromShared: true));

        Assert.Equal(after, Snapshot());
    }

    [Fact]
    public void ARunThatStoppedMidwayIsFinishedNextTime()
    {
        // As a run stopped by a crash leaves it: wood's copy in place, a half-written copy for the
        // scheme of the person's own, the shared file still there, and no marker.
        var shared = Shared("Ding.wav", 1, 2, 3, 4);
        Directory.CreateDirectory(Path.Combine(_sounds, "wood"));
        File.WriteAllBytes(InScheme("wood", "Ding.wav"), [1, 2, 3, 4]);
        Directory.CreateDirectory(Path.Combine(_sounds, Calm));
        File.WriteAllBytes(InScheme(Calm, "Ding.wav" + SoundFolderMigration.PartialSuffix), [1, 2]);

        SoundFolderMigration.Run(_sounds, [BuiltIn("wood", ("NewMail", "custom:Ding.wav")), Own(Calm, ("NewMail", "custom:Ding.wav"))], []);

        Assert.Equal([(byte)1, 2, 3, 4], File.ReadAllBytes(InScheme(Calm, "Ding.wav")));
        Assert.Equal([(byte)1, 2, 3, 4], File.ReadAllBytes(InScheme("wood", "Ding.wav")));
        Assert.False(File.Exists(InScheme(Calm, "Ding.wav" + SoundFolderMigration.PartialSuffix)));
        Assert.False(File.Exists(shared));
        Assert.True(Done);
    }

    [Fact]
    public void ADifferentFileAlreadyInTheSchemesFolderIsKeptAndSoIsTheSharedOne()
    {
        var shared = Shared("Ding.wav", 1);
        Directory.CreateDirectory(Path.Combine(_sounds, "glass"));
        File.WriteAllBytes(InScheme("glass", "Ding.wav"), [9]);

        SoundFolderMigration.Run(_sounds, [BuiltIn("glass", ("NewMail", "custom:Ding.wav"))], []);

        Assert.Equal([(byte)9], File.ReadAllBytes(InScheme("glass", "Ding.wav")));
        Assert.Equal([(byte)1], File.ReadAllBytes(shared));
    }

    [Fact]
    public void ASchemeWhoseIdCannotBeAFolderIsLeftAlone()
    {
        var shared = Shared("Ding.wav", 1);

        SoundFolderMigration.Run(_sounds, [Own(@"user-..\evil", ("NewMail", "custom:Ding.wav")), Own("from-newer", ("NewMail", "custom:Ding.wav"))], []);

        Assert.True(File.Exists(shared));
        Assert.Empty(Directory.GetDirectories(_sounds));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_sounds)!, "evil", "Ding.wav")));
    }

    [Fact]
    public void AnEventNamingAFileThatIsNotThereIsSkipped()
    {
        SoundFolderMigration.Run(_sounds, [BuiltIn("glass", ("NewMail", "custom:Gone.wav"), ("SendFailed", "custom:..\\x.wav"))], []);

        Assert.False(Directory.Exists(Path.Combine(_sounds, "glass")));
        Assert.True(Done);
    }

    [Fact]
    public void WithNoSoundsFolderThereIsNothingToMoveAndItIsMarkedDone()
    {
        var fresh = Path.Combine(_sounds, "fresh-profile", "sounds");

        SoundFolderMigration.Run(fresh, [BuiltIn("glass", ("NewMail", "custom:Ding.wav"))], []);

        Assert.True(File.Exists(Path.Combine(fresh, SoundFolderMigration.MarkerFileName)));
    }

    [Fact]
    public void TheMarkerIsNeverTakenForASound()
    {
        SoundFolderMigration.Run(_sounds, [], []);
        Assert.False(SoundRef.IsSafeCustomFileName(SoundFolderMigration.MarkerFileName));
        Assert.False(SoundRef.IsSafeCustomFileName("Ding.wav" + SoundFolderMigration.PartialSuffix));
    }

    // ── At startup, reading config.ini and the rules itself ───────────────────

    private string Profile(string configText)
    {
        var profile = Directory.CreateDirectory(Path.Combine(_sounds, "profile-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        File.WriteAllText(Path.Combine(profile, "config.ini"), configText);
        var sounds = Directory.CreateDirectory(Path.Combine(profile, SoundCatalog.CustomFolderName)).FullName;
        File.WriteAllBytes(Path.Combine(sounds, "Ding.wav"), [1]);
        return profile;
    }

    private const string GlassUsesDing = "[global]\n[sounds.glass]\nNewMail = custom:Ding.wav\n";

    [Fact]
    public void AtStartupItReadsTheSchemesFromConfigIni()
    {
        var profile = Profile(GlassUsesDing);

        SoundFolderMigration.RunAtStartup(profile, () => []);

        Assert.True(File.Exists(Path.Combine(profile, "sounds", "glass", "Ding.wav")));
        Assert.False(File.Exists(Path.Combine(profile, "sounds", "Ding.wav")));
        Assert.True(File.Exists(Path.Combine(profile, "sounds", SoundFolderMigration.MarkerFileName)));
    }

    [Fact]
    public void AtStartupAConfigIniThatCannotBeReadMovesNothingAndIsTriedAgain()
    {
        var profile = Profile(GlassUsesDing);
        var config = Path.Combine(profile, "config.ini");

        using (new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            SoundFolderMigration.RunAtStartup(profile, () => []);

        Assert.False(File.Exists(Path.Combine(profile, "sounds", SoundFolderMigration.MarkerFileName)));
        SoundFolderMigration.RunAtStartup(profile, () => []);   // the next start, with the file readable
        Assert.True(File.Exists(Path.Combine(profile, "sounds", "glass", "Ding.wav")));
    }

    [Fact]
    public void AtStartupRulesThatCannotBeReadMoveNothingAndAreTriedAgain()
    {
        var profile = Profile(GlassUsesDing);

        SoundFolderMigration.RunAtStartup(profile, () => throw new IOException("rules.json is locked"));

        Assert.True(File.Exists(Path.Combine(profile, "sounds", "Ding.wav")));
        Assert.False(Directory.Exists(Path.Combine(profile, "sounds", "glass")));
        Assert.False(File.Exists(Path.Combine(profile, "sounds", SoundFolderMigration.MarkerFileName)));
    }

    [Fact]
    public void AtStartupARuleUsingTheFileKeepsItInPlace()
    {
        var profile = Profile(GlassUsesDing);

        SoundFolderMigration.RunAtStartup(profile, () => [new MailRule { Name = "Ding", Action = RuleAction.PlaySound, Sound = "custom:Ding.wav" }]);

        Assert.True(File.Exists(Path.Combine(profile, "sounds", "Ding.wav")));
        Assert.True(File.Exists(Path.Combine(profile, "sounds", "glass", "Ding.wav")));
    }

    [Fact]
    public void PlacingOnlyNamedFilesLeavesOthersAlone()
    {
        Shared("Ding.wav", 1);
        Shared("Pat.wav", 2);

        SoundFolderMigration.Place(_sounds, [BuiltIn("glass", ("NewMail", "custom:Ding.wav"), ("SendFailed", "custom:Pat.wav"))], [],
            removeFromShared: false, onlyNames: ["Pat.wav"]);

        Assert.True(File.Exists(InScheme("glass", "Pat.wav")));
        Assert.False(File.Exists(InScheme("glass", "Ding.wav")));
    }

    private string[] Snapshot() =>
        [.. Directory.GetFiles(_sounds, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_sounds, f)).Order(StringComparer.Ordinal)];
}

/// <summary>The sound service and scheme files with a folder per scheme (#948), against real files.</summary>
public sealed class SoundSchemeFolderServiceTests : IDisposable
{
    private sealed class Output : ISoundOutput
    {
        public ConcurrentQueue<SoundClip> Played { get; } = new();

        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
        {
            using var stream = clip.OpenStream?.Invoke();
            Played.Enqueue(clip);
        }

        public void Check(string filePath) { }
    }

    private const string Calm = "user-0123456789abcdef0123456789abcdef";

    private readonly string _sounds = Path.Combine(Path.GetTempPath(), $"QM-SFS-{Guid.NewGuid():N}");
    private readonly StubConfigService _config = new();
    private readonly Output _output = new();
    private readonly List<string> _sources = [];
    private SoundService? _service;

    public SoundSchemeFolderServiceTests() => Directory.CreateDirectory(_sounds);

    public void Dispose()
    {
        _service?.Dispose();
        try { Directory.Delete(_sounds, recursive: true); } catch (IOException) { }
        foreach (var folder in _sources)
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    private SoundService Service() => _service ??= new SoundService(_config, _sounds, _output, openBuiltIn: (_, _) => new MemoryStream());

    private string FileIn(string? folder, string name, params byte[] bytes)
    {
        var dir = folder is null ? _sounds : Directory.CreateDirectory(Path.Combine(_sounds, folder)).FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Source(string name, byte[] bytes)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"QM-SFS-SRC-{Guid.NewGuid():N}")).FullName;
        _sources.Add(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Waits for one sound to have played and returns it.</summary>
    private SoundClip PlayedOne()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_output.Played.IsEmpty && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Thread.Sleep(100);
        return Assert.Single(_output.Played);
    }

    // ── Where files go, and where they're looked for ──────────────────────────

    [Fact]
    public void AFileChosenForABuiltInSchemeGoesInThatSchemesFolder()
    {
        Assert.True(Service().TryAddCustomSound("glass", Source("Mine.wav", [1, 2]), out var name, out _));

        Assert.Equal("Mine.wav", name);
        Assert.Equal([(byte)1, 2], File.ReadAllBytes(Path.Combine(_sounds, "glass", "Mine.wav")));
        Assert.Equal(["Mine.wav"], Service().CustomSoundFiles("glass"));
        Assert.Empty(Service().CustomSoundFiles("wood"));
        Assert.Empty(Service().CustomSoundFiles(null));   // the rules' folder doesn't list the schemes' folders
        Assert.NotNull(Service().CustomSoundPath("glass", "Mine.wav"));
        Assert.Null(Service().CustomSoundPath("wood", "Mine.wav"));
    }

    [Fact]
    public void TheSameNameInTwoSchemesIsTwoFiles()
    {
        Service().TryAddCustomSound("glass", Source("Ding.wav", [1]), out _, out _);
        Assert.True(Service().TryAddCustomSound(Calm, Source("Ding.wav", [2]), out var name, out _));

        Assert.Equal("Ding.wav", name);   // not numbered: another scheme's file doesn't take the name
        Assert.Equal([(byte)1], File.ReadAllBytes(Path.Combine(_sounds, "glass", "Ding.wav")));
        Assert.Equal([(byte)2], File.ReadAllBytes(Path.Combine(_sounds, Calm, "Ding.wav")));
    }

    [Fact]
    public void ASchemePlaysItsOwnFile()
    {
        var path = FileIn("glass", "Ding.wav", 1);
        _config.Load().PlayEventSounds = true;
        _config.Load().SoundSchemes["glass"] = new SoundScheme("glass", "Glass", true, "glass", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" });

        Service().Play(SoundEvent.NewMail);

        Assert.Equal(path, PlayedOne().FilePath);
    }

    [Fact]
    public void ASchemeNeverPlaysAFileThatIsOnlyInTheSharedFolderOrAnotherSchemes()
    {
        FileIn(null, "Ding.wav", 1);
        FileIn("wood", "Ding.wav", 2);
        _config.Load().PlayEventSounds = true;
        _config.Load().SoundSchemes["glass"] = new SoundScheme("glass", "Glass", true, "glass", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" });

        Service().Play(SoundEvent.NewMail);

        Assert.Equal("builtin:arrived", PlayedOne().Key);   // missing, so its built-in sound
    }

    [Fact]
    public void ARulesOwnSoundPlaysFromTheRulesFolder()
    {
        var path = FileIn(null, "Pat.wav", 1);
        FileIn("glass", "Pat.wav", 2);

        Service().PlayRule("custom:Pat.wav");

        Assert.Equal(path, PlayedOne().FilePath);
    }

    [Fact]
    public void ARuleOnTheDefaultSoundPlaysTheSchemesFileFromTheSchemesFolder()
    {
        var path = FileIn(Calm, "Match.wav", 1);
        FileIn(null, "Match.wav", 2);
        _config.Load().SoundSchemeId = Calm;
        _config.Load().SoundSchemes[Calm] = new SoundScheme(Calm, "Calm", false, events: new Dictionary<string, string> { ["RuleMatched"] = "custom:Match.wav" });

        Service().PlayRule("builtin:matched");

        Assert.Equal(path, PlayedOne().FilePath);
    }

    [Fact]
    public void APreviewPlaysFromTheFolderOfTheSchemeShowing()
    {
        var path = FileIn(Calm, "Ding.wav", 1);
        FileIn(null, "Ding.wav", 2);

        Service().Preview("custom:Ding.wav", schemeId: Calm);

        Assert.Equal(path, PlayedOne().FilePath);
    }

    [Fact]
    public void AnIdThatCannotBeAFolderHasNoFilesAndNothingIsWritten()
    {
        const string evil = @"user-..\..\evil";
        FileIn(null, "Ding.wav", 1);

        Assert.False(Service().TryAddCustomSound(evil, Source("Ding.wav", [2]), out _, out var refusal));
        Assert.False(string.IsNullOrEmpty(refusal));
        Assert.Null(Service().CustomSoundPath(evil, "Ding.wav"));
        Assert.Empty(Service().CustomSoundFiles(evil));
        Assert.True(Service().TryDeleteSchemeSounds(evil));
        Assert.True(File.Exists(Path.Combine(_sounds, "Ding.wav")));
    }

    [Theory]
    [InlineData("glass", "glass")]
    [InlineData("user-0123456789ABCDEF0123456789abcdef", "user-0123456789abcdef0123456789abcdef")]
    [InlineData("user-mine", "user-mine")]
    [InlineData("user-", null)]
    [InlineData("user-a b", null)]
    [InlineData(@"user-a\b", null)]
    [InlineData("user-a.b", null)]
    [InlineData("other", null)]
    [InlineData(null, null)]
    public void ASchemesFolderIsItsIdWhenThatIsSafe(string? id, string? folder) =>
        Assert.Equal(folder, SoundCatalog.SchemeFolderName(id));

    // ── Deleting and copying a scheme's files ─────────────────────────────────

    [Fact]
    public void DeletingASchemesFilesTouchesNoOtherSchemeAndNoRule()
    {
        FileIn(Calm, "Ding.wav", 1);
        FileIn(Calm, "Other.wav", 1);
        var wood = FileIn("wood", "Ding.wav", 2);
        var rule = FileIn(null, "Ding.wav", 3);

        Assert.True(Service().TryDeleteSchemeSounds(Calm));
        Assert.True(Service().TryDeleteSchemeSounds(Calm));   // already gone is fine

        Assert.False(Directory.Exists(Path.Combine(_sounds, Calm)));
        Assert.True(File.Exists(wood));
        Assert.True(File.Exists(rule));
    }

    [Fact]
    public void CopyingASchemesFilesCopiesEveryOneAndOverwritesNothing()
    {
        FileIn("glass", "Ding.wav", 1);
        FileIn("glass", "Song.mp3", 2);
        FileIn(Calm, "Song.mp3", 9);

        Assert.True(Service().TryCopySchemeSounds("glass", Calm));

        Assert.Equal([(byte)1], File.ReadAllBytes(Path.Combine(_sounds, Calm, "Ding.wav")));
        Assert.Equal([(byte)9], File.ReadAllBytes(Path.Combine(_sounds, Calm, "Song.mp3")));
        Assert.True(File.Exists(Path.Combine(_sounds, "glass", "Ding.wav")));   // the original keeps its own
    }

    [Fact]
    public void CopyingASchemeWithNoFilesIsFine()
    {
        Assert.True(Service().TryCopySchemeSounds("wood", Calm));
        Assert.False(Directory.Exists(Path.Combine(_sounds, Calm)));
    }

    // ── Sharing a scheme ──────────────────────────────────────────────────────

    [Fact]
    public void ImportPutsTheSoundsInTheNewSchemesFolderAndExportReadsThemFromThere()
    {
        var ding = TestWav.Tone();
        FileIn("wood", "Ding.wav", ding);
        FileIn(null, "Ding.wav", TestWav.Tone(salt: 3));   // a rule's file of the same name, which must not be exported
        var file = Path.Combine(_sounds, "..", Guid.NewGuid().ToString("N") + ".qmsounds");
        try
        {
            Assert.Equal(0, SoundSchemeFile.Export(file, "Wood mine", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" }, Service(), "wood"));
            Assert.Equal(ding, SoundSchemeFile.Read(file).Sounds["Ding.wav"]);

            var imported = SoundSchemeFile.Import(file, Service(), Calm);

            Assert.Equal("custom:Ding.wav", imported.Events["NewMail"]);   // its own folder was empty, so no number
            Assert.Equal(ding, File.ReadAllBytes(Path.Combine(_sounds, Calm, "Ding.wav")));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ExportLeavesOutAFileThatIsNotInTheSchemesFolder()
    {
        FileIn(null, "Ding.wav", TestWav.Tone());
        var file = Path.Combine(_sounds, "..", Guid.NewGuid().ToString("N") + ".qmsounds");
        try
        {
            Assert.Equal(1, SoundSchemeFile.Export(file, "Glass", new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" }, Service(), "glass"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>Settings and each scheme's folder of sound files (#948).</summary>
public class SoundSchemeFolderSettingsTests
{
    private static SettingsViewModel MakeSettings(StubConfigService config, RecordingSoundService sounds) =>
        new(config, new StubCommandRegistry(), sounds: sounds);

    private static StubConfigService WithOwnScheme(string id = "user-calm", string name = "Calm")
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes[id] = new SoundScheme(id, name, false, events: new Dictionary<string, string> { ["NewMail"] = "custom:Ding.wav" });
        return config;
    }

    [Fact]
    public void DeletingASchemeAsksAboutItsFilesAndDeletesThemOnSave()
    {
        var config = WithOwnScheme();
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(config, sounds);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "user-calm");
        string? asked = null;
        vm.ConfirmRequested = (question, _) => { asked = question; return true; };

        Assert.True(vm.DeleteSelectedScheme());

        Assert.Equal("Delete the sound scheme Calm and its sound files?", asked);
        Assert.Empty(sounds.SchemeFoldersDeleted);   // not yet: Cancel would bring it back
        vm.SaveCommand.Execute(null);
        Assert.Equal(["user-calm"], sounds.SchemeFoldersDeleted);
        vm.DiscardUnsavedSchemeSounds();
        Assert.Equal(["user-calm"], sounds.SchemeFoldersDeleted);
    }

    [Fact]
    public void CancellingADeleteKeepsTheSchemesFiles()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithOwnScheme(), sounds);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "user-calm");
        vm.ConfirmRequested = (_, _) => true;
        vm.DeleteSelectedScheme();

        vm.DiscardUnsavedSchemeSounds();   // Settings closed without Save

        Assert.Empty(sounds.SchemeFoldersDeleted);
    }

    [Fact]
    public void AnsweringNoDeletesNothing()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithOwnScheme(), sounds);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "user-calm");
        vm.ConfirmRequested = (_, _) => false;

        Assert.False(vm.DeleteSelectedScheme());
        vm.SaveCommand.Execute(null);

        Assert.Empty(sounds.SchemeFoldersDeleted);
    }

    [Fact]
    public void SchemesMadeInSettingsLoseTheirFoldersOnCancel()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(new StubConfigService(), sounds);
        Assert.True(vm.CreateScheme("New one"));
        var made = vm.SelectedSoundScheme!.Id;
        Assert.True(vm.CreateScheme("Copy of it", made));
        var copy = vm.SelectedSoundScheme!.Id;

        vm.DiscardUnsavedSchemeSounds();

        Assert.Equal(new[] { made, copy }.Order(), sounds.SchemeFoldersDeleted.Order());
    }

    [Fact]
    public void SchemesMadeInSettingsKeepTheirFoldersOnSave()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(new StubConfigService(), sounds);
        vm.CreateScheme("New one");

        vm.SaveCommand.Execute(null);
        vm.DiscardUnsavedSchemeSounds();

        Assert.Empty(sounds.SchemeFoldersDeleted);
    }

    [Fact]
    public void ASchemeMadeAndDeletedInSettingsLosesItsFolderOnSave()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(new StubConfigService(), sounds);
        vm.CreateScheme("Brief");
        var id = vm.SelectedSoundScheme!.Id;
        vm.ConfirmRequested = (_, _) => true;
        vm.DeleteSelectedScheme();

        vm.SaveCommand.Execute(null);

        Assert.Equal([id], sounds.SchemeFoldersDeleted);
    }

    [Fact]
    public void CopyingASchemeCopiesItsFolder()
    {
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.Add("Ding.wav");
        var vm = MakeSettings(WithOwnScheme(), sounds);

        Assert.True(vm.CreateScheme("Calm too", "user-calm"));

        Assert.Equal([("user-calm", vm.SelectedSoundScheme!.Id)], sounds.SchemeCopies);
        Assert.Equal("New mail: Ding.wav", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail).AccessibleName);
    }

    [Fact]
    public void ACopyWhoseFilesCouldNotAllBeCopiedSaysSo()
    {
        var sounds = new RecordingSoundService { CopySchemeSoundsFails = true };
        var vm = MakeSettings(WithOwnScheme(), sounds);

        Assert.True(vm.CreateScheme("Calm too", "user-calm"));

        Assert.StartsWith("Not all of the sound files of Calm could be copied.", vm.SoundSchemeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewSchemeCopiesNoFiles()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(new StubConfigService(), sounds);
        vm.CreateScheme("Silent");
        Assert.Empty(sounds.SchemeCopies);
    }

    [Fact]
    public void AFileChosenGoesInTheFolderOfTheSchemeShowingBuiltInOrNot()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithOwnScheme(), sounds);
        vm.PickSoundFileRequested = () => @"C:\Music\Ding.wav";

        Assert.True(vm.TryChooseSoundFile());   // Glass, the built-in scheme in use
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "user-calm");
        Assert.True(vm.TryChooseSoundFile());

        Assert.Equal(["glass", "user-calm"], sounds.AddedTo);
    }

    [Fact]
    public void EachSchemeListsTheFilesInItsOwnFolder()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithOwnScheme(), sounds);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "user-calm");
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "wood");

        Assert.Equal(["glass", "user-calm", "wood"], sounds.ListedFor.Distinct());
        Assert.DoesNotContain(null, sounds.ListedFor);   // never the rules' folder
    }

    [Fact]
    public void PreviewPlaysFromTheFolderOfTheSchemeShowing()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(WithOwnScheme(), sounds);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "user-calm");

        vm.PreviewSoundCommand.Execute(null);

        Assert.Equal("user-calm", sounds.PreviewSchemeId);
    }

    [Fact]
    public void ImportingPutsTheSoundsInTheNewSchemesFolderAndCancelDeletesIt()
    {
        var file = Path.Combine(Path.GetTempPath(), $"QM-SFI-{Guid.NewGuid():N}.qmsounds");
        try
        {
            SoundSchemeFile.Export(file, "Rain", new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived" }, new RecordingSoundService(), "wood");
            var sounds = new RecordingSoundService();
            var vm = MakeSettings(new StubConfigService(), sounds);

            Assert.True(vm.ImportSchemeFrom(file));
            var id = vm.SelectedSoundScheme!.Id;
            Assert.True(SoundCatalog.IsUserSchemeId(id));

            vm.DiscardUnsavedSchemeSounds();
            Assert.Equal([id], sounds.SchemeFoldersDeleted);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AGalleryUpdateGoesIntoTheSchemesOwnFolderWhichCancelNeverDeletes()
    {
        var file = Path.Combine(Path.GetTempPath(), $"QM-SFG-{Guid.NewGuid():N}.qmsounds");
        try
        {
            SoundSchemeFile.Export(file, "Rain", new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived" }, new RecordingSoundService(), "wood");
            var config = new StubConfigService();
            config.Load().SoundSchemes["user-rain"] = new SoundScheme("user-rain", "My rain", false,
                galleryOrigin: new SoundGalleryOrigin("rain", new string('1', 64)));
            var sounds = new RecordingSoundService();
            var vm = MakeSettings(config, sounds);
            vm.UpdateChoiceRequested = _ => SoundSchemeUpdateChoice.Apply;

            Assert.True(vm.ImportSchemeFrom(file, new SoundGalleryEntry("rain", "Rain", "Pat", "", "CC0-1.0", "rain.qmsounds", 1, new string('2', 64), null)));
            Assert.Equal("user-rain", vm.SelectedSoundScheme!.Id);   // the same scheme, so the same folder

            vm.DiscardUnsavedSchemeSounds();
            Assert.Empty(sounds.SchemeFoldersDeleted);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AFailedImportDeletesWhatLandedInTheFolderOfTheSchemeItWouldHaveMade()
    {
        var file = Path.Combine(Path.GetTempPath(), $"QM-SFI-{Guid.NewGuid():N}.qmsounds");
        File.WriteAllText(file, "not a zip");
        try
        {
            var sounds = new RecordingSoundService();
            var vm = MakeSettings(new StubConfigService(), sounds);

            Assert.False(vm.ImportSchemeFrom(file));

            var deleted = Assert.Single(sounds.SchemeFoldersDeleted);
            Assert.True(SoundCatalog.IsUserSchemeId(deleted));
            Assert.DoesNotContain(vm.SoundSchemes, s => s.Id == deleted);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void RemoveFileDeletesFromTheSchemesFolderOnly()
    {
        var sounds = new RecordingSoundService();
        sounds.CustomFiles.Add("Ding.wav");
        var vm = MakeSettings(WithOwnScheme(), sounds);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(s => s.Id == "user-calm");
        vm.SelectedSoundEvent = vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail);
        vm.ConfirmRequested = (_, _) => true;

        Assert.True(vm.TryRemoveSoundFile());

        Assert.Equal(["user-calm"], sounds.RemovedFrom);
        Assert.Empty(sounds.SchemeFoldersDeleted);
    }
}
