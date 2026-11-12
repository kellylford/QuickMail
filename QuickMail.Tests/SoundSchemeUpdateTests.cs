using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// A gallery update keeps the person's changes, and QuickMail says when one is waiting (#971): the
/// merge, the words of the question, when the daily check runs, and what config.ini keeps.
/// </summary>
public sealed class SoundSchemeUpdateTests
{
    private const string None = "none";

    /// <summary>Every event at None, then the ones given.</summary>
    private static Dictionary<string, string> AllEvents(params (string Key, string Value)[] set)
    {
        var events = SoundCatalog.Events.ToDictionary(i => i.ConfigKey, _ => None, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in set) events[key] = value;
        return events;
    }

    private static Dictionary<string, string> Only(params (string Key, string Value)[] set) =>
        set.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

    // ── The merge ───────────────────────────────────────────────────────────────

    [Fact]
    public void AnEventLeftAloneTakesTheNewSoundAndOneChangedKeepsTheirs()
    {
        var baseline = AllEvents(("NewMail", "builtin:wood/arrived"), ("SendFailed", "builtin:wood/failed"));
        var copy = AllEvents(("NewMail", "builtin:wood/arrived"), ("SendFailed", "builtin:glass/failed"));
        var incoming = Only(("NewMail", "builtin:softbell/arrived"), ("SendFailed", "builtin:softbell/failed"));

        var plan = SoundSchemeUpdate.Plan(baseline, copy, incoming, SoundSchemeUpdateMode.KeepChanges);

        Assert.Equal("builtin:softbell/arrived", plan.Events["NewMail"]);
        Assert.Equal("builtin:glass/failed", plan.Events["SendFailed"]);
        Assert.Equal((1, 0, 1), (plan.Updated, plan.Added, plan.Kept));
        Assert.Equal(SoundCatalog.Events.Count, plan.Events.Count);
    }

    [Fact]
    public void AChangeThatHappensToMatchTheNewVersionIsNotCounted()
    {
        var baseline = AllEvents(("NewMail", "builtin:wood/arrived"));
        var copy = AllEvents(("NewMail", "builtin:softbell/arrived"));
        var plan = SoundSchemeUpdate.Plan(baseline, copy, Only(("NewMail", "builtin:softbell/arrived")), SoundSchemeUpdateMode.KeepChanges);

        Assert.Equal("builtin:softbell/arrived", plan.Events["NewMail"]);
        Assert.Equal((0, 0, 0), (plan.Updated, plan.Added, plan.Kept));
    }

    [Fact]
    public void AnEventMissingFromTheCopyIsAdded()
    {
        // A copy saved before ListEnd existed: every event but that one.
        var baseline = AllEvents(("NewMail", "builtin:wood/arrived"));
        baseline.Remove("ListEnd");
        var copy = new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase);
        var incoming = Only(("NewMail", "builtin:wood/arrived"), ("ListEnd", "builtin:glass/edge"));

        var plan = SoundSchemeUpdate.Plan(baseline, copy, incoming, SoundSchemeUpdateMode.KeepChanges);

        Assert.Equal("builtin:glass/edge", plan.Events["ListEnd"]);
        Assert.Equal((0, 1, 0), (plan.Updated, plan.Added, plan.Kept));
    }

    [Fact]
    public void AnEventTheNewVersionDropsGoesSilentUnlessThePersonChangedIt()
    {
        var baseline = AllEvents(("NewMail", "builtin:wood/arrived"), ("MessageSent", "builtin:wood/sent"));
        var copy = AllEvents(("NewMail", "builtin:wood/arrived"), ("MessageSent", "builtin:glass/sent"));
        var plan = SoundSchemeUpdate.Plan(baseline, copy, Only(), SoundSchemeUpdateMode.KeepChanges);

        Assert.Equal(None, plan.Events["NewMail"]);                   // left alone: as the new version has it
        Assert.Equal("builtin:glass/sent", plan.Events["MessageSent"]); // changed: theirs
        Assert.Equal((1, 0, 1), (plan.Updated, plan.Added, plan.Kept));
    }

    [Fact]
    public void CustomFilesFollowTheSameRules()
    {
        var baseline = AllEvents(("NewMail", "custom:drop.wav"), ("SendFailed", "custom:thud.wav"));
        var copy = AllEvents(("NewMail", "custom:drop.wav"), ("SendFailed", "custom:Mine.wav"), ("TrashEmptied", "custom:Mine.wav"));
        // The new version's drop landed as "drop 2.wav": a different sound under the same name.
        var incoming = Only(("NewMail", "custom:drop 2.wav"), ("SendFailed", "custom:thud.wav"), ("ListEnd", "custom:edge.wav"));

        var plan = SoundSchemeUpdate.Plan(baseline, copy, incoming, SoundSchemeUpdateMode.KeepChanges);

        Assert.Equal("custom:drop 2.wav", plan.Events["NewMail"]);
        Assert.Equal("custom:Mine.wav", plan.Events["SendFailed"]);
        Assert.Equal("custom:Mine.wav", plan.Events["TrashEmptied"]);
        Assert.Equal("custom:edge.wav", plan.Events["ListEnd"]);

        // On Save, of the old version's files and the new ones, only those nothing names go; Mine.wav,
        // the person's own, is never a candidate.
        var candidates = new[] { "drop.wav", "thud.wav", "drop 2.wav", "edge.wav" };
        var inFolder = new[] { "drop.wav", "thud.wav", "drop 2.wav", "edge.wav", "Mine.wav", "Unused of mine.wav" };
        Assert.Equal(["drop.wav", "thud.wav"], SoundSchemeUpdate.FilesToDelete(candidates, inFolder, plan.Events).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SoundsCompareAsTheyPlay()
    {
        Assert.True(SoundSchemeUpdate.Same("custom:Drop.wav", "custom:drop.wav"));
        Assert.True(SoundSchemeUpdate.Same("builtin:wood/arrived", "builtin:wood/arrived"));
        Assert.False(SoundSchemeUpdate.Same("builtin:wood/arrived", "builtin:glass/arrived"));
        Assert.False(SoundSchemeUpdate.Same("builtin:arrived", "builtin:glass/arrived"));
    }

    [Fact]
    public void WithoutABaselineOnlyMissingEventsAreAdded()
    {
        var copy = AllEvents(("NewMail", "builtin:wood/arrived"));
        copy.Remove("ListEnd");
        var incoming = Only(("NewMail", "builtin:softbell/arrived"), ("SendFailed", "builtin:softbell/failed"), ("ListEnd", "builtin:glass/edge"));

        var plan = SoundSchemeUpdate.Plan(null, copy, incoming, SoundSchemeUpdateMode.AddMissingOnly);

        Assert.Equal("builtin:wood/arrived", plan.Events["NewMail"]);
        Assert.Equal(None, plan.Events["SendFailed"]);
        Assert.Equal("builtin:glass/edge", plan.Events["ListEnd"]);
        Assert.Equal((0, 1, 2), (plan.Updated, plan.Added, plan.Kept));
    }

    [Fact]
    public void ReplacingEverythingTakesTheNewVersionWhole()
    {
        var copy = AllEvents(("NewMail", "builtin:wood/arrived"), ("TrashEmptied", "custom:Mine.wav"));
        var plan = SoundSchemeUpdate.Plan(null, copy, Only(("NewMail", "builtin:softbell/arrived")), SoundSchemeUpdateMode.ReplaceEverything);

        Assert.Equal("builtin:softbell/arrived", plan.Events["NewMail"]);
        Assert.Equal(None, plan.Events["TrashEmptied"]);
        Assert.Equal((2, 0, 0), (plan.Updated, plan.Added, plan.Kept));
    }

    [Fact]
    public void ANoneSettingsFilledInIsMissingButAChosenNoneIsNot()
    {
        var values = AllEvents(("NewMail", "builtin:wood/arrived"));
        var stored = values.Keys.ToList();   // a copy shown and saved by 0.8.72: every event stored

        // No baseline: only the events newer than 0.8.71 count as missing when None.
        var untracked = SoundSchemeUpdate.CopyEvents(values, stored, baseline: null);
        Assert.DoesNotContain("ListEnd", untracked.Keys);
        Assert.Equal(None, untracked["MessageSent"]);   // stored, and 0.8.71 had it: a choice
        Assert.Equal("builtin:wood/arrived", untracked["NewMail"]);

        // A baseline: None for an event the gallery version didn't list is missing.
        var baseline = AllEvents();
        baseline.Remove("RuleMatched");
        var tracked = SoundSchemeUpdate.CopyEvents(values, stored, baseline);
        Assert.DoesNotContain("RuleMatched", tracked.Keys);
        Assert.Contains("ListEnd", tracked.Keys);

        // Not stored at all: missing, whatever it is.
        var partial = SoundSchemeUpdate.CopyEvents(values, ["NewMail"], baseline);
        Assert.Equal(["NewMail"], partial.Keys);

        // A sound chosen in Settings and not yet saved is the copy's, stored or not.
        values["ListEnd"] = "builtin:glass/edge";
        Assert.Equal("builtin:glass/edge", SoundSchemeUpdate.CopyEvents(values, ["NewMail"], null)["ListEnd"]);
    }

    // ── The question ────────────────────────────────────────────────────────────

    private static SoundSchemeUpdatePlan Counts(int updated, int added, int kept, SoundSchemeUpdateMode mode = SoundSchemeUpdateMode.KeepChanges) =>
        new(new Dictionary<string, string>(), updated, added, kept, mode);

    [Fact]
    public void TheQuestionSaysWhatChanges()
    {
        var question = SoundSchemeUpdate.Question("Maya Voicing Glass", Counts(3, 1, 2), filesDeleted: 0);
        Assert.Equal("Update Sound Scheme", question.Title);
        Assert.False(question.OffersReplaceEverything);
        Assert.Equal("Update Maya Voicing Glass with the gallery's newer version? 3 events get the new version's sounds, 1 new event is added, 2 events you changed are kept. Nothing changes until you save Settings.",
            question.Text);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, "1 event gets the new version's sound.")]
    [InlineData(0, 2, 0, 0, "2 new events are added.")]
    [InlineData(0, 0, 1, 0, "1 event you changed is kept.")]
    [InlineData(0, 0, 0, 0, "Your copy already has the new version's sounds, so none of them change.")]
    [InlineData(2, 0, 0, 1, "2 events get the new version's sounds. 1 sound file the new version doesn't use is deleted when you save.")]
    [InlineData(2, 0, 0, 3, "2 events get the new version's sounds. 3 sound files the new version doesn't use are deleted when you save.")]
    public void TheQuestionCountsInWords(int updated, int added, int kept, int files, string expected)
    {
        var text = SoundSchemeUpdate.Question("Rain", Counts(updated, added, kept), files).Text;
        Assert.Equal($"Update Rain with the gallery's newer version? {expected} Nothing changes until you save Settings.", text);
    }

    [Fact]
    public void WithoutABaselineTheQuestionSaysOnlyMissingEventsAreAddedAndOffersReplacingEverything()
    {
        var question = SoundSchemeUpdate.Question("Rain", Counts(0, 1, 4, SoundSchemeUpdateMode.AddMissingOnly), 0, filesDeletedIfReplaced: 2);
        Assert.True(question.OffersReplaceEverything);
        Assert.Equal(
            "Update Rain with the gallery's newer version? You added it before QuickMail kept track of the changes you make, so it can't tell yours from the gallery's. "
            + "Yes adds only what's missing from your copy: 1 event gets a sound, and nothing else changes. "
            + "No replaces every sound with the new version's, so any changes you made are lost. 2 sound files it doesn't use are deleted when you save. "
            + "Cancel leaves your copy as it is. Nothing changes until you save Settings.",
            question.Text);

        Assert.Contains("Yes adds only what's missing from your copy: 3 events get a sound,",
            SoundSchemeUpdate.Question("Rain", Counts(0, 3, 0, SoundSchemeUpdateMode.AddMissingOnly), 0).Text, StringComparison.Ordinal);
        Assert.Contains("Yes changes none of its sounds, since nothing is missing from your copy;",
            SoundSchemeUpdate.Question("Rain", Counts(0, 0, 4, SoundSchemeUpdateMode.AddMissingOnly), 0).Text, StringComparison.Ordinal);
    }

    // ── When to check ───────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, true, false, true, true)]       // never checked
    [InlineData(-23.9, true, false, true, false)]     // checked under a day ago
    [InlineData(-24.0, true, false, true, true)]      // a day ago
    [InlineData(-100.0, true, false, true, true)]
    [InlineData(-100.0, false, false, true, false)]   // setting off
    [InlineData(-100.0, true, true, true, false)]     // offline
    [InlineData(-100.0, true, false, false, false)]   // no scheme from the gallery
    [InlineData(5.0, true, false, true, true)]        // a last check in the future: the clock was wrong
    public void ACheckIsDueOnceADayWithTheSettingOnOnlineAndAGalleryScheme(double? hoursAgo, bool enabled, bool offline, bool hasGalleryScheme, bool due)
    {
        DateTime? last = hoursAgo is { } h ? Now.AddHours(h) : null;
        Assert.Equal(due, SoundSchemeUpdateCheck.IsDue(Now, last, enabled, offline, hasGalleryScheme));
    }

    private static SoundScheme FromGallery(string id, string name, string galleryId, char sha) =>
        new(id, name, isBuiltIn: false, galleryOrigin: new SoundGalleryOrigin(galleryId, new string(sha, 64)));

    private static SoundGalleryEntry Listed(string id, char sha) =>
        new(id, id, "Pat", "", "CC0-1.0", $"{id}-{new string(sha, 16)}.qmsounds", 10, new string(sha, 64), 3);

    [Fact]
    public void OnlyASchemeTheGalleryNowHasANewerFileForHasAnUpdate()
    {
        var schemes = new[]
        {
            FromGallery("user-1", "Wind", "wind", 'a'),        // newer file listed
            FromGallery("user-2", "Rain", "rain", 'b'),        // same file
            FromGallery("user-3", "Hail", "hail", 'c'),        // no longer listed
            FromGallery("user-4", "Bells", "bells", 'd'),      // newer file listed
            new SoundScheme("user-5", "Mine", isBuiltIn: false),
            SoundCatalog.BuiltInScheme("glass"),
        };
        var entries = new[] { Listed("wind", 'e'), Listed("rain", 'b'), Listed("bells", 'f') };

        Assert.Equal(4, SoundSchemeUpdateCheck.GallerySchemes(schemes).Count);
        Assert.Equal(["Bells", "Wind"], SoundSchemeUpdateCheck.WithUpdates(schemes, entries));
    }

    [Fact]
    public void TheNoticeNamesTheSchemesAndWhereToGo()
    {
        Assert.Equal("An update is available for the Maya Voicing Glass sound scheme. Settings, Sounds, Browse schemes.",
            SoundSchemeUpdateCheck.Notice(["Maya Voicing Glass"]));
        Assert.Equal("Updates are available for the Essentials and Rain sound schemes. Settings, Sounds, Browse schemes.",
            SoundSchemeUpdateCheck.Notice(["Essentials", "Rain"]));
        Assert.Equal("Updates are available for the A, B and C sound schemes. Settings, Sounds, Browse schemes.",
            SoundSchemeUpdateCheck.Notice(["A", "B", "C"]));
    }

    // ── The check ──────────────────────────────────────────────────────────────

    private sealed class CountingGallery(IReadOnlyList<SoundGalleryEntry>? entries = null, SoundGalleryException? failure = null) : ISoundGalleryService
    {
        public int Loads { get; private set; }

        public Task<IReadOnlyList<SoundGalleryEntry>> LoadAsync(CancellationToken ct)
        {
            Loads++;
            return failure is null
                ? Task.FromResult<IReadOnlyList<SoundGalleryEntry>>(entries ?? [])
                : Task.FromException<IReadOnlyList<SoundGalleryEntry>>(failure);
        }

        public Task<string> DownloadAsync(SoundGalleryEntry entry, CancellationToken ct) => throw new NotSupportedException();
    }

    private static StubConfigService WithGalleryScheme(char sha = 'a')
    {
        var config = new StubConfigService();
        config.Load().SoundSchemes["user-wind"] = FromGallery("user-wind", "Wind", "wind", sha);
        return config;
    }

    [Fact]
    public async Task TheCheckSaysWhenAnUpdateIsWaitingAndRemembersWhenItLooked()
    {
        var config = WithGalleryScheme();
        var gallery = new CountingGallery([Listed("wind", 'e')]);
        var checker = new SoundSchemeUpdateChecker(config, gallery);

        var notice = await checker.CheckIfDueAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal("An update is available for the Wind sound scheme. Settings, Sounds, Browse schemes.", notice);
        Assert.Equal(Now, config.Load().LastSoundSchemeUpdateCheck);

        // Again within the day: not asked.
        Assert.Null(await checker.CheckIfDueAsync(Now.AddHours(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, gallery.Loads);
    }

    [Fact]
    public async Task TheCheckDoesNothingWhenItIsOffOfflineOrThereIsNoGalleryScheme()
    {
        var off = WithGalleryScheme();
        off.Load().CheckSoundSchemeUpdates = false;
        var gallery = new CountingGallery([Listed("wind", 'e')]);
        Assert.Null(await new SoundSchemeUpdateChecker(off, gallery).CheckIfDueAsync(Now, TestContext.Current.CancellationToken));
        Assert.Null(await new SoundSchemeUpdateChecker(WithGalleryScheme(), gallery, () => true).CheckIfDueAsync(Now, TestContext.Current.CancellationToken));
        Assert.Null(await new SoundSchemeUpdateChecker(new StubConfigService(), gallery).CheckIfDueAsync(Now, TestContext.Current.CancellationToken));
        Assert.Equal(0, gallery.Loads);
        Assert.Null(off.Load().LastSoundSchemeUpdateCheck);
    }

    [Fact]
    public async Task AGalleryThatFailsIsSilentAndNotAskedAgainThatDay()
    {
        var config = WithGalleryScheme();
        var gallery = new CountingGallery(failure: new SoundGalleryException("unreachable", SoundGalleryProblem.Unreachable));
        var checker = new SoundSchemeUpdateChecker(config, gallery);

        Assert.Null(await checker.CheckIfDueAsync(Now, TestContext.Current.CancellationToken));
        Assert.Null(await checker.CheckIfDueAsync(Now.AddHours(1), TestContext.Current.CancellationToken));
        Assert.Equal(1, gallery.Loads);
    }

    [Fact]
    public async Task NoUpdateMeansNothingToSay()
    {
        var checker = new SoundSchemeUpdateChecker(WithGalleryScheme('e'), new CountingGallery([Listed("wind", 'e')]));
        Assert.Null(await checker.CheckIfDueAsync(Now, TestContext.Current.CancellationToken));
    }

    /// <summary>A gallery folder holding one of app-kit's signed lists, as index.signed.</summary>
    private static string GalleryWith(string sharedIndex)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"QM-SUC-{Guid.NewGuid():N}")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "SharedSoundTestData", sharedIndex), Path.Combine(dir, "index.signed"));
        return dir;
    }

    private static string TestKey => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SharedSoundTestData", "test-key.pub.pem"));

    [Fact]
    public async Task TheCheckReadsTheGalleryThroughTheSignedIndex()
    {
        // index-other-app.signed is a QuickMail list, signed with app-kit's test key, listing "wrong-ext".
        var good = GalleryWith("index-other-app.signed");
        var tampered = GalleryWith("index-tampered.signed");
        try
        {
            var config = new StubConfigService();
            config.Load().SoundSchemes["user-x"] = FromGallery("user-x", "Wrong Ext", "wrong-ext", 'b');

            using (var gallery = new SoundGalleryService(good, TestKey))
            {
                var notice = await new SoundSchemeUpdateChecker(config, gallery).CheckIfDueAsync(Now, TestContext.Current.CancellationToken);
                Assert.Equal("An update is available for the Wrong Ext sound scheme. Settings, Sounds, Browse schemes.", notice);
            }

            // A list whose signature doesn't check is never read, so it can't announce anything.
            config.Load().LastSoundSchemeUpdateCheck = null;
            using (var gallery = new SoundGalleryService(tampered, TestKey))
                Assert.Null(await new SoundSchemeUpdateChecker(config, gallery).CheckIfDueAsync(Now, TestContext.Current.CancellationToken));

            // Nor is a good list read with a key that isn't the one it was signed with.
            config.Load().LastSoundSchemeUpdateCheck = null;
            using (var gallery = new SoundGalleryService(good))
                Assert.Null(await new SoundSchemeUpdateChecker(config, gallery).CheckIfDueAsync(Now, TestContext.Current.CancellationToken));
            Assert.Equal(Now, config.Load().LastSoundSchemeUpdateCheck);
        }
        finally
        {
            Directory.Delete(good, recursive: true);
            Directory.Delete(tampered, recursive: true);
        }
    }

    // ── config.ini ──────────────────────────────────────────────────────────────

    private static ProfileContext MakeTempProfile() =>
        new(Path.Combine(Path.GetTempPath(), $"QM-SUP-{Guid.NewGuid():N}"));

    [Fact]
    public void TheCheckIsOnAndHasNeverRunByDefault()
    {
        var config = new ConfigService(MakeTempProfile()).Load();
        Assert.True(config.CheckSoundSchemeUpdates);
        Assert.Null(config.LastSoundSchemeUpdateCheck);
    }

    [Fact]
    public void TheSettingTheLastCheckAndTheGallerySoundsSurviveARestart()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);
        var config = service.Load();
        config.CheckSoundSchemeUpdates = false;
        config.LastSoundSchemeUpdateCheck = Now;
        var events = AllEvents(("NewMail", "builtin:wood/arrived"), ("SendFailed", "custom:Mine.wav"));
        var baseline = AllEvents(("NewMail", "builtin:wood/arrived"), ("SendFailed", "custom:thud.wav"));
        config.SoundSchemes = new Dictionary<string, SoundScheme>(StringComparer.OrdinalIgnoreCase)
        {
            ["user-wind"] = new SoundScheme("user-wind", "Wind", false, events: events,
                galleryOrigin: new SoundGalleryOrigin("wind", new string('a', 64)), galleryBaseline: baseline),
        };
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.False(reloaded.CheckSoundSchemeUpdates);
        Assert.Equal(Now, reloaded.LastSoundSchemeUpdateCheck);
        Assert.Equal(DateTimeKind.Utc, reloaded.LastSoundSchemeUpdateCheck!.Value.Kind);
        var scheme = reloaded.SoundSchemes["user-wind"];
        Assert.Equal(baseline.OrderBy(e => e.Key), scheme.GalleryBaseline!.OrderBy(e => e.Key));
        // The recorded sounds are never mistaken for the scheme's own events.
        Assert.Equal(events.OrderBy(e => e.Key), scheme.Events.OrderBy(e => e.Key));
        Assert.DoesNotContain(scheme.Events.Keys, k => k.StartsWith("GallerySound", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SettingsShowsAndSavesTheCheckbox()
    {
        var config = new StubConfigService();
        var vm = new ViewModels.SettingsViewModel(config, new StubCommandRegistry(), sounds: new RecordingSoundService());
        Assert.True(vm.CheckSoundSchemeUpdates);

        vm.CheckSoundSchemeUpdates = false;
        vm.SaveCommand.Execute(null);
        Assert.False(config.Load().CheckSoundSchemeUpdates);

        Assert.False(new ViewModels.SettingsViewModel(config, new StubCommandRegistry()).CheckSoundSchemeUpdates);
    }

    [Fact]
    public void RecordedSoundsWithoutAValidOriginAreDropped()
    {
        var lines = new[]
        {
            "[sounds.user-wind]",
            "Name = Wind",
            "GalleryId = Not An Id",
            "GallerySha256 = " + new string('a', 64),
            "GallerySound.NewMail = builtin:wood/arrived",
            "NewMail = builtin:glass/arrived",
        };
        var scheme = ConfigService.ParseFile(lines).SoundSchemes["user-wind"];
        Assert.Null(scheme.GalleryOrigin);
        Assert.Null(scheme.GalleryBaseline);
        Assert.Equal(["NewMail"], scheme.Events.Keys);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-11-12T15:00:00")]   // no time zone: not one QuickMail wrote
    public void AnUnreadableLastCheckIsAsIfNever(string value)
    {
        var config = ConfigService.ParseFile(["[global]", $"LastSoundSchemeUpdateCheck = {value}"]);
        Assert.Null(config.LastSoundSchemeUpdateCheck);
    }
}
