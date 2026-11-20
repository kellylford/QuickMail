using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>How a gallery update is applied to the copy of a scheme already here (#971).</summary>
public enum SoundSchemeUpdateMode
{
    /// <summary>
    /// The copy has the gallery version's sounds recorded: an event the person left alone takes the new
    /// version's sound, one they changed keeps theirs, and one the copy is missing is added.
    /// </summary>
    KeepChanges,

    /// <summary>Nothing recorded, so nothing can be told apart: only the events the copy is missing are added.</summary>
    AddMissingOnly,

    /// <summary>Every event takes the new version's sound, as an update did before 0.8.72.</summary>
    ReplaceEverything,
}

/// <summary>What the person chose when asked about an update.</summary>
public enum SoundSchemeUpdateChoice
{
    /// <summary>Leave the copy as it is.</summary>
    Cancel,

    /// <summary>Apply the update as described: keeping their changes, or adding only what's missing.</summary>
    Apply,

    /// <summary>Replace every sound with the new version's, offered only when changes can't be told apart.</summary>
    ReplaceEverything,
}

/// <summary>The question asked before an update is applied.</summary>
/// <param name="Title">The dialog's title.</param>
/// <param name="Text">What will change, in plain words.</param>
/// <param name="OffersReplaceEverything">Whether replacing everything is offered as a second choice.</param>
public sealed record SoundSchemeUpdateQuestion(string Title, string Text, bool OffersReplaceEverything);

/// <summary>What an update does to a copy, worked out before anything changes.</summary>
/// <param name="Events">Every event's stored sound once the update is applied.</param>
/// <param name="Updated">Events the copy had whose sound becomes the new version's, a sound and not silence.</param>
/// <param name="Added">Events the copy was missing that the new version gives a sound.</param>
/// <param name="Kept">Events whose sound in the copy stays, though the new version has another.</param>
/// <param name="Mode">How it was worked out.</param>
/// <param name="Silenced">Events the copy had a sound for that go silent, because the new version has none for them.</param>
public sealed record SoundSchemeUpdatePlan(
    IReadOnlyDictionary<string, string> Events, int Updated, int Added, int Kept, SoundSchemeUpdateMode Mode, int Silenced = 0);

/// <summary>
/// Updating a gallery scheme without losing the person's changes (#971): the merge, and the words that
/// say what it will do. Pure, so every case can be tested without Settings.
/// </summary>
public static class SoundSchemeUpdate
{
    public const string Title = "Update Sound Scheme";

    private static readonly string None = SoundRef.None.ToStored();

    /// <summary>Whether two stored sounds are the same sound, as QuickMail would play them.</summary>
    public static bool Same(string? a, string? b)
    {
        if (SoundRef.TryParse(a, out var x) && SoundRef.TryParse(b, out var y))
            return string.Equals(x.ToStored(), y.ToStored(), StringComparison.OrdinalIgnoreCase);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNone(string? stored) => Same(stored, None);

    /// <summary>
    /// The copy as an update sees it: the events it has a sound chosen for. An event is missing if the
    /// copy doesn't store it, or stores None for an event the gallery version it came from didn't have,
    /// which is only Settings filling the event in when it saved, not a choice: with recorded sounds, an
    /// event they don't list; without, one newer than the QuickMail that added it.
    /// </summary>
    /// <param name="values">Every event's sound in the copy now, as edited.</param>
    /// <param name="storedKeys">The events the copy stores, as saved.</param>
    /// <param name="baseline">The gallery version's sounds, if recorded.</param>
    public static Dictionary<string, string> CopyEvents(
        IReadOnlyDictionary<string, string> values, IEnumerable<string> storedKeys, IReadOnlyDictionary<string, string>? baseline)
    {
        var stored = new HashSet<string>(storedKeys, StringComparer.OrdinalIgnoreCase);
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in SoundCatalog.Events)
        {
            var key = info.ConfigKey;
            if (!values.TryGetValue(key, out var value)) continue;
            if (IsNone(value))
            {
                var versionLacksIt = baseline is null
                    ? SoundCatalog.EventsNewerThanUntrackedSchemes.Contains(key)
                    : !baseline.ContainsKey(key);
                if (!stored.Contains(key) || versionLacksIt) continue;
            }
            copy[key] = value;
        }
        return copy;
    }

    /// <summary>Works out what an update does, for every event QuickMail has.</summary>
    /// <param name="baseline">The gallery version's sounds the copy came from; used only by <see cref="SoundSchemeUpdateMode.KeepChanges"/>.</param>
    /// <param name="copy">The copy's events, from <see cref="CopyEvents"/>: an event not in it is missing.</param>
    /// <param name="incoming">The new version's events; one it doesn't list has no sound in it.</param>
    /// <param name="mode">How to apply it.</param>
    public static SoundSchemeUpdatePlan Plan(
        IReadOnlyDictionary<string, string>? baseline, IReadOnlyDictionary<string, string> copy,
        IReadOnlyDictionary<string, string> incoming, SoundSchemeUpdateMode mode)
    {
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int updated = 0, added = 0, kept = 0, silenced = 0;
        foreach (var info in SoundCatalog.Events)
        {
            var key = info.ConfigKey;
            var next = incoming.TryGetValue(key, out var n) ? n : None;
            if (!copy.TryGetValue(key, out var current))
            {
                // Missing from the copy, in every mode: the new version's, if it has one.
                events[key] = next;
                if (!IsNone(next)) added++;
                continue;
            }

            var takeNew = mode switch
            {
                SoundSchemeUpdateMode.ReplaceEverything => true,
                SoundSchemeUpdateMode.KeepChanges => baseline is not null && baseline.TryGetValue(key, out var was) && Same(current, was),
                _ => false,
            };
            events[key] = takeNew ? next : current;
            if (Same(current, next)) continue;
            // Taking the new version's silence isn't getting its sound: said apart, so it isn't a surprise.
            if (takeNew && IsNone(next)) silenced++;
            else if (takeNew) updated++;
            else kept++;
        }
        return new SoundSchemeUpdatePlan(events, updated, added, kept, mode, silenced);
    }

    /// <summary>The custom sound files among some stored sounds.</summary>
    public static HashSet<string> CustomFiles(IEnumerable<string> storedSounds)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in storedSounds)
        {
            if (SoundRef.TryParse(value, out var sound) && sound.Kind == SoundRefKind.Custom) names.Add(sound.Name);
        }
        return names;
    }

    /// <summary>
    /// The sound files an update would leave unused and so delete on Save: of <paramref name="candidates"/>,
    /// those still in the scheme's folder that the updated scheme doesn't name.
    /// </summary>
    public static List<string> FilesToDelete(IEnumerable<string> candidates, IEnumerable<string> inFolder, IReadOnlyDictionary<string, string> events)
    {
        var used = CustomFiles(events.Values);
        var present = new HashSet<string>(inFolder, StringComparer.OrdinalIgnoreCase);
        return [.. candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(f => present.Contains(f) && !used.Contains(f))];
    }

    /// <summary>
    /// What to ask before applying an update. With the gallery version's sounds recorded, it says what
    /// changes, such as "3 events get the new version's sounds, 1 new event is added, 2 events you
    /// changed are kept." Without, it says only missing events are added, and offers replacing
    /// everything as a second choice.
    /// </summary>
    /// <param name="schemeName">The copy's name.</param>
    /// <param name="plan">The plan for the way it will be applied: <see cref="SoundSchemeUpdateMode.KeepChanges"/> or <see cref="SoundSchemeUpdateMode.AddMissingOnly"/>.</param>
    /// <param name="filesDeleted">How many sound files the plan leaves unused, to be deleted on Save.</param>
    /// <param name="filesDeletedIfReplaced">The same, for replacing everything; used only without recorded sounds.</param>
    public static SoundSchemeUpdateQuestion Question(string schemeName, SoundSchemeUpdatePlan plan, int filesDeleted, int filesDeletedIfReplaced = 0)
    {
        var start = $"Update {schemeName} with the gallery's newer version?";
        const string untilSave = "Nothing changes until you save Settings.";

        if (plan.Mode != SoundSchemeUpdateMode.AddMissingOnly)
        {
            var parts = new List<string>();
            if (plan.Updated > 0)
                parts.Add(plan.Updated == 1 ? "1 event gets the new version's sound" : $"{plan.Updated} events get the new version's sounds");
            if (plan.Added > 0)
                parts.Add(plan.Added == 1 ? "1 new event is added" : $"{plan.Added} new events are added");
            if (plan.Silenced > 0)
                parts.Add(plan.Silenced == 1 ? "1 event the new version no longer has goes silent" : $"{plan.Silenced} events the new version no longer has go silent");
            if (plan.Kept > 0)
                parts.Add(plan.Kept == 1 ? "1 event you changed is kept" : $"{plan.Kept} events you changed are kept");
            var what = parts.Count > 0 ? string.Join(", ", parts) + "." : "Your copy already has the new version's sounds, so none of them change.";
            var files = filesDeleted > 0 ? " " + FilesSentence(filesDeleted, "the new version doesn't use") : string.Empty;
            return new SoundSchemeUpdateQuestion(Title, $"{start} {what}{files} {untilSave}", OffersReplaceEverything: false);
        }

        // Worded for the dialog's buttons, which say what they do (#971).
        var missing = plan.Added switch
        {
            0 => "Add only what's missing: nothing is missing from your copy, so none of its sounds change.",
            1 => "Add only what's missing: 1 event your copy doesn't have gets a sound, and nothing else changes.",
            _ => $"Add only what's missing: {plan.Added} events your copy doesn't have get a sound, and nothing else changes.",
        };
        var replaceFiles = filesDeletedIfReplaced > 0 ? " " + FilesSentence(filesDeletedIfReplaced, "it doesn't use") : string.Empty;
        var text = $"{start} You added it before QuickMail kept track of the changes you make, so it can't tell yours from the gallery's. "
            + missing
            + $" Replace everything: every event gets the new version's sound, so any changes you made are lost.{replaceFiles}"
            + $" Cancel leaves your copy as it is. {untilSave}";
        return new SoundSchemeUpdateQuestion(Title, text, OffersReplaceEverything: true);
    }

    private static string FilesSentence(int count, string unused) =>
        count == 1 ? $"1 sound file {unused} is deleted when you save." : $"{count} sound files {unused} are deleted when you save.";
}

/// <summary>
/// When QuickMail checks the gallery for updates to the schemes someone added (#971): at most once a
/// day, only with the setting on, only online, and only if there is a gallery scheme to check. Pure.
/// </summary>
public static class SoundSchemeUpdateCheck
{
    /// <summary>The least time between two checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>How long after the main window loads before the first check, so it never adds to startup.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(2);

    /// <summary>How often a QuickMail left running looks again whether a check is due.</summary>
    public static readonly TimeSpan LookAgainEvery = TimeSpan.FromHours(1);

    /// <summary>Whether to check now.</summary>
    /// <param name="nowUtc">The time now, in UTC.</param>
    /// <param name="lastCheckUtc">When the last check was, in UTC, or null if never.</param>
    /// <param name="enabled">Settings → Sounds, "Check for updates to sound schemes from the gallery".</param>
    /// <param name="offline">Whether the computer has no network.</param>
    /// <param name="hasGalleryScheme">Whether any scheme here was added from the gallery.</param>
    public static bool IsDue(DateTime nowUtc, DateTime? lastCheckUtc, bool enabled, bool offline, bool hasGalleryScheme)
    {
        if (!enabled || offline || !hasGalleryScheme) return false;
        // A last check in the future means the clock was wrong, then or now: check, which puts it right.
        return lastCheckUtc is not { } last || last > nowUtc || nowUtc - last >= Interval;
    }

    /// <summary>The schemes of the person's own that came from the gallery.</summary>
    public static List<SoundScheme> GallerySchemes(IEnumerable<SoundScheme> schemes) =>
        [.. schemes.Where(s => !s.IsBuiltIn && SoundCatalog.IsUserSchemeId(s.Id) && s.GalleryOrigin is not null)];

    /// <summary>
    /// The names of the gallery schemes here that the gallery now lists a different file for, in order
    /// of name: the same test the gallery window uses to say "Update available."
    /// </summary>
    public static List<string> WithUpdates(IEnumerable<SoundScheme> schemes, IEnumerable<SoundGalleryEntry> entries) =>
        [.. Updates(schemes, entries).Select(u => u.Name)];

    /// <summary>
    /// The gallery schemes here that the gallery now lists a different file for, in order of name, each
    /// with its gallery id and the SHA-256 of the newer file.
    /// </summary>
    public static List<SoundSchemeUpdateFound> Updates(IEnumerable<SoundScheme> schemes, IEnumerable<SoundGalleryEntry> entries)
    {
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries) listed.TryAdd(entry.Id, entry.Sha256);
        var found = new List<SoundSchemeUpdateFound>();
        foreach (var scheme in GallerySchemes(schemes))
        {
            var origin = scheme.GalleryOrigin!;
            if (listed.TryGetValue(origin.Id, out var sha) && !string.Equals(sha, origin.Sha256, StringComparison.Ordinal))
                found.Add(new SoundSchemeUpdateFound(scheme.Name, origin.Id, sha));
        }
        return [.. found.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Of the updates found, the ones not said yet: a scheme is announced once per newer version, so the
    /// same waiting update isn't said again every day. <paramref name="announced"/> is gallery id to the
    /// SHA-256 last announced for it.
    /// </summary>
    public static List<SoundSchemeUpdateFound> NotYetAnnounced(IEnumerable<SoundSchemeUpdateFound> found, IReadOnlyDictionary<string, string> announced) =>
        [.. found.Where(f => !announced.TryGetValue(f.GalleryId, out var sha) || !string.Equals(sha, f.Sha256, StringComparison.Ordinal))];

    /// <summary>
    /// What to remember as announced after a check: what was remembered, for gallery schemes still here
    /// only (so a deleted scheme's entry goes), plus the updates about to be said.
    /// </summary>
    public static Dictionary<string, string> Remember(IReadOnlyDictionary<string, string> announced,
        IEnumerable<SoundScheme> schemes, IEnumerable<SoundSchemeUpdateFound> saying)
    {
        var here = new HashSet<string>(GallerySchemes(schemes).Select(s => s.GalleryOrigin!.Id), StringComparer.Ordinal);
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, sha) in announced)
        {
            if (here.Contains(id)) kept[id] = sha;
        }
        foreach (var update in saying) kept[update.GalleryId] = update.Sha256;
        return kept;
    }

    /// <summary>What the status bar shows, and a screen reader hears, when updates are waiting.</summary>
    public static string Notice(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => $"An update is available for the {names[0]} sound scheme. Settings, Sounds, Browse schemes.",
        _ => $"Updates are available for the {string.Join(", ", names.Take(names.Count - 1))} and {names[^1]} sound schemes. Settings, Sounds, Browse schemes.",
    };
}

/// <summary>A gallery scheme here with a newer version in the gallery.</summary>
/// <param name="Name">The copy's name here.</param>
/// <param name="GalleryId">Its id in the gallery.</param>
/// <param name="Sha256">The SHA-256 of the newer file the gallery lists.</param>
public sealed record SoundSchemeUpdateFound(string Name, string GalleryId, string Sha256);

/// <summary>
/// Checks the gallery for updates to the schemes someone added (#971), through the same signed,
/// verified index the gallery window reads. Failures are logged and otherwise silent.
/// </summary>
/// <param name="config">Where the setting and the last check's time are kept.</param>
/// <param name="gallery">The gallery.</param>
/// <param name="isOffline">Whether the computer has no network now.</param>
public sealed class SoundSchemeUpdateChecker(IConfigService config, ISoundGalleryService gallery, Func<bool>? isOffline = null)
{
    /// <summary>
    /// If a check is due, checks, and returns what to tell the person, or null when there's nothing to
    /// say: not due, nothing newer, nothing newer than what was already said, or the gallery couldn't be
    /// read. The time is recorded before the gallery is asked, so a gallery that fails is not asked again
    /// until tomorrow either. Call on the UI thread: it saves config.ini, before and after the gallery is
    /// read, and resumes on the caller's thread to do so.
    /// </summary>
    public async Task<string?> CheckIfDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        var cfg = config.Load();
        var schemes = cfg.SoundSchemes.Values.ToList();
        if (!SoundSchemeUpdateCheck.IsDue(nowUtc, cfg.LastSoundSchemeUpdateCheck, cfg.CheckSoundSchemeUpdates,
                isOffline?.Invoke() == true, SoundSchemeUpdateCheck.GallerySchemes(schemes).Count > 0))
            return null;

        cfg.LastSoundSchemeUpdateCheck = nowUtc;
        config.Save(cfg);

        IReadOnlyList<SoundGalleryEntry> entries;
        try
        {
            // Back on the caller's thread: config.ini is saved again below.
            entries = await gallery.LoadAsync(ct);
        }
        catch (SoundGalleryException ex)
        {
            LogService.Log($"Sound scheme update check: the gallery could not be read ({ex.Kind})", ex);
            return null;
        }

        // The schemes as they are now: Settings may have saved while the gallery was being read.
        cfg = config.Load();
        var found = SoundSchemeUpdateCheck.Updates(cfg.SoundSchemes.Values, entries);
        var fresh = SoundSchemeUpdateCheck.NotYetAnnounced(found, cfg.AnnouncedSoundSchemeUpdates);
        LogService.Log($"Sound scheme update check: {found.Count} of the gallery schemes here have an update, {fresh.Count} not yet said.");

        // Remembered before it is said, so it is said once per newer version, not every day; and the
        // entries of schemes no longer here are let go, so the list doesn't only ever grow.
        var announced = SoundSchemeUpdateCheck.Remember(cfg.AnnouncedSoundSchemeUpdates, cfg.SoundSchemes.Values, fresh);
        if (!SameEntries(announced, cfg.AnnouncedSoundSchemeUpdates))
        {
            cfg.AnnouncedSoundSchemeUpdates = announced;
            config.Save(cfg);
        }
        return fresh.Count == 0 ? null : SoundSchemeUpdateCheck.Notice([.. fresh.Select(f => f.Name)]);
    }

    private static bool SameEntries(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var v) && string.Equals(v, e.Value, StringComparison.Ordinal));
}
