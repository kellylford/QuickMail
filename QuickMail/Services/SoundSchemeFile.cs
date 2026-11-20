using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>A sound scheme file that cannot be imported, with the reason in words a person can act on.</summary>
public sealed class SoundSchemeFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What importing a sound scheme file produced: a scheme to add, not yet saved.</summary>
/// <param name="Name">The name the file gives it, cleaned up; the caller makes it unique.</param>
/// <param name="Events">Every event's stored sound, with custom files renamed to where they landed.</param>
/// <param name="SoundsAdded">How many sound files were copied in.</param>
/// <param name="SoundsDropped">Events whose sound file was missing from the file or refused, now set to None.</param>
/// <param name="Author">Who made it, if the file says; kept so the credit travels with it.</param>
/// <param name="License">Its licence, if the file says.</param>
/// <param name="Description">One line about it, if the file says.</param>
public sealed record SoundSchemeImport(string Name, Dictionary<string, string> Events, int SoundsAdded, int SoundsDropped,
    string? Author = null, string? License = null, string? Description = null);

/// <summary>What a scheme file holds once read and checked, before any sound is put in place.</summary>
/// <param name="Name">Its name, cleaned; "Imported scheme" if it had none.</param>
/// <param name="Author">Who made it, cleaned, or null.</param>
/// <param name="License">Its licence, cleaned, or null.</param>
/// <param name="Description">Its description, cleaned, or null.</param>
/// <param name="Events">Only QuickMail's events, each with a stored sound QuickMail can use; a value it couldn't is <c>none</c>.</param>
/// <param name="Sounds">The sound files the events use that the file carried and passed every check, by name.</param>
/// <param name="Dropped">How many events used a sound file that couldn't be used, and are now <c>none</c>.</param>
public sealed record SoundSchemeFileContents(string Name, string? Author, string? License, string? Description,
    Dictionary<string, string> Events, Dictionary<string, byte[]> Sounds, int Dropped);

/// <summary>
/// A sound scheme shared as a file (#932): a zip holding <c>scheme.json</c> and the custom sounds it
/// uses under <c>sounds/</c>. Reading and writing follow app-kit's <c>sounds/SCHEMES.md</c>, the spec
/// every Idea Place app shares, and app-kit's shared test files check that QuickMail reads them as
/// the others do. Reading never trusts the archive: only those names are accepted, every size is
/// counted as it is read rather than taken from the archive, every stored sound is checked, and
/// every sound file then goes through the same checks as one chosen in Settings, decoding included.
/// </summary>
public static class SoundSchemeFile
{
    public const string FileExtension = ".qmsounds";
    public const string FileFilter = "QuickMail sound scheme (*.qmsounds)|*.qmsounds";

    /// <summary>QuickMail's id in app-kit's <c>sounds/apps.json</c>, written into every scheme file.</summary>
    public const string AppId = "quickmail";

    /// <summary>
    /// The app a scheme file is read for: its id and name, its events in order, its built-in sounds,
    /// and whether its own files may be MP3s. Always QuickMail in the app; the tests read app-kit's
    /// shared files as The Chat Place too, from app-kit's apps.json, to check every rule of the reader.
    /// </summary>
    internal sealed record SchemeApp(string Id, string Name, IReadOnlyList<string> EventKeys, IReadOnlyList<string> BuiltInSounds, bool AllowMp3)
    {
        public static readonly SchemeApp QuickMail = new(AppId, "QuickMail",
            [.. SoundCatalog.Events.Select(e => e.ConfigKey)], SoundCatalog.BuiltInSounds, AllowMp3: true);

        public bool TryParse(string? value, out SoundRef sound) => SoundRef.TryParse(value, out sound, BuiltInSounds, AllowMp3);

        public string Unreadable => $"This file could not be read as a sound scheme for {Name}.";

        public string NewerVersion => $"This sound scheme was made by a newer version of {Name}. Update {Name} and try again.";

        public string ForAnotherApp(string appName) => $"This is a sound scheme for {appName}, so {Name} can't use it.";
    }

    /// <summary>The apps app-kit knows, so a scheme file for one of them can be refused by name.</summary>
    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.Ordinal)
    {
        ["quickmail"] = "QuickMail",
        ["thechatplace"] = "The Chat Place",
    };

    private const string SchemeEntry = "scheme.json";
    private const string SoundsPrefix = SoundCatalog.CustomFolderName + "/";
    private const int CurrentFormat = 1;
    internal const int MaxSchemeJsonBytes = 64 * 1024;
    private const int MaxEntries = 64;
    private const int MaxEvents = 100;
    internal const int MaxJsonDepth = 8;
    internal const int MaxAuthorLength = 60;
    internal const int MaxLicenseLength = 40;
    internal const int MaxDescriptionLength = 300;
    private const int MaxAppNameLength = 40;
    private const string ImportedName = "Imported scheme";

    /// <summary>The largest scheme file accepted, counting every byte read from it.</summary>
    public const long MaxFileBytes = 80L * 1024 * 1024;

    internal const string TooLarge = "This sound scheme is too large to use.";

    /// <summary>The words for a scheme file made for another app.</summary>
    internal static string ForAnotherApp(string appName) => SchemeApp.QuickMail.ForAnotherApp(appName);

    // Every entry gets the same time and attributes, so the same scheme always gives the same bytes:
    // the gallery names a file by its hash (SCHEMES.md, "Writing one").
    private static readonly DateTimeOffset EntryTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every event's sound in a scheme, as it should travel: a built-in sound with no pack of its own
    /// gets the scheme's, so it sounds the same on the other computer whatever scheme it lands in.
    /// </summary>
    public static Dictionary<string, string> Portable(SoundScheme scheme)
    {
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in SoundCatalog.Events)
        {
            var sound = SoundCatalog.Resolve(info.Event, scheme);
            if (sound.Kind == SoundRefKind.BuiltIn && sound.Pack is null)
                sound = SoundRef.BuiltIn(sound.Name, SoundCatalog.PackFor(sound, scheme));
            events[info.ConfigKey] = sound.ToStored();
        }
        return events;
    }

    // ── Writing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a scheme and the custom sounds it uses. A scheme file holds only 16-bit PCM WAVs, the
    /// kind every app reads alike, so a sound of the person's own that isn't one (an MP3, a 24-bit
    /// WAV) goes in converted to one, named <c>&lt;its name&gt;.wav</c> (numbered if that's taken), and
    /// its events point at that. A file that's missing, can't be converted, is over 5 MB or 30
    /// seconds as a WAV, or would take the file past its limit is left out, and its events are
    /// written as <c>none</c>. Throws IOException or UnauthorizedAccessException.
    /// </summary>
    /// <param name="author">Who made it, if known, so the credit a licence such as CC-BY asks for travels.</param>
    /// <param name="license">Its licence, if known.</param>
    /// <param name="description">One line about it, if known.</param>
    /// <param name="toSchemeWav">Converts a sound file to a scheme-file WAV, or returns null; <see cref="SchemeWavConverter.Convert"/> unless a test says otherwise.</param>
    /// <param name="schemeId">The scheme being exported, whose folder its sound files are read from (#948).</param>
    /// <returns>How many events were written as <c>none</c> because their sound file was left out.</returns>
    public static int Export(string path, string name, IReadOnlyDictionary<string, string> events, ISoundService sounds, string schemeId,
        string? author = null, string? license = null, string? description = null, Func<string, byte[]?>? toSchemeWav = null)
    {
        toSchemeWav ??= SchemeWavConverter.Convert;
        var chosen = new List<(string Key, SoundRef Sound)>();
        foreach (var info in SoundCatalog.Events)
            chosen.Add((info.ConfigKey, events.TryGetValue(info.ConfigKey, out var value) && SoundRef.TryParse(value, out var parsed) ? parsed : SoundRef.None));

        // Every file name the scheme uses: a converted sound's new name must not be one of them, or
        // another file in the export, even differing only in case, which Windows treats as the same file.
        var used = new HashSet<string>(chosen.Where(c => c.Sound.Kind == SoundRefKind.Custom).Select(c => c.Sound.Name), StringComparer.OrdinalIgnoreCase);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exportedAs = new Dictionary<string, string?>(StringComparer.Ordinal);
        long total = 0;

        string? Prepare(string file)
        {
            if (sounds.CustomSoundPath(schemeId, file) is not { } source) return null;
            var bytes = ReadSmallFile(source);
            var exportName = file;
            if (bytes is null || SchemeWavProblem(bytes) is not null)
            {
                bytes = toSchemeWav(source);
                if (bytes is null) return null;
                exportName = WavNameFor(file, used, taken);
            }
            if (total + bytes.Length > MaxFileBytes - MaxSchemeJsonBytes) return null;
            total += bytes.Length;
            files[exportName] = bytes;
            taken.Add(exportName);
            return exportName;
        }

        var written = new List<KeyValuePair<string, string>>();
        int leftOutEvents = 0;
        foreach (var (key, chosenSound) in chosen)
        {
            var sound = chosenSound;
            if (sound.Kind == SoundRefKind.Custom)
            {
                if (!exportedAs.TryGetValue(sound.Name, out var exportName))
                    exportedAs[sound.Name] = exportName = Prepare(sound.Name);
                if (exportName is null)
                {
                    sound = SoundRef.None;
                    leftOutEvents++;
                }
                else
                {
                    sound = SoundRef.Custom(exportName);
                }
            }
            written.Add(new(key, sound.ToStored()));
        }

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, SchemeEntry, SchemeJson(name, author, license, description, written));
            foreach (var (file, bytes) in files)
                AddEntry(zip, SoundsPrefix + file, bytes);
        }
        File.WriteAllBytes(path, stream.ToArray());   // built in memory first, so a failure leaves no half-written file
        return leftOutEvents;
    }

    /// <summary>
    /// The name a converted sound goes in the scheme file under: its own name with <c>.wav</c>, or
    /// that numbered (<c>bell 2.wav</c>) if another file the scheme uses, or one already in the export,
    /// has it.
    /// </summary>
    private static string WavNameFor(string file, HashSet<string> used, HashSet<string> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        bool Free(string candidate) => SoundRef.IsSafeCustomFileName(candidate) && !taken.Contains(candidate)
            && (!used.Contains(candidate) || candidate.Equals(file, StringComparison.OrdinalIgnoreCase));
        var name = stem + ".wav";
        for (var n = 2; !Free(name) && n < 1000; n++)
            name = Cut(stem, 90) + $" {n}.wav";
        return Free(name) ? name : $"Converted sound {Guid.NewGuid():N}.wav";
    }

    /// <summary>A file's bytes if it is no larger than a sound may be; null if it's larger or can't be read, so it's left out.</summary>
    private static byte[]? ReadSmallFile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > SoundCatalog.MaxCustomFileBytes) return null;
            var bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked, or gone since it was found: one sound left out, not the whole export failed.
            LogService.Log($"Sound scheme export: could not read {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        entry.ExternalAttributes = 0;
        using var es = entry.Open();
        es.Write(bytes);
    }

    /// <summary>scheme.json: indented by 2, non-ASCII left as it is, every event in QuickMail's order.</summary>
    private static byte[] SchemeJson(string name, string? author, string? license, string? description,
        IEnumerable<KeyValuePair<string, string>> events)
    {
        using var ms = new MemoryStream();
        using (var json = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("format", CurrentFormat);
            json.WriteString("app", AppId);
            json.WriteString("name", CleanName(name));
            WriteOptional(json, "author", CleanText(author, MaxAuthorLength));
            WriteOptional(json, "license", CleanText(license, MaxLicenseLength));
            var about = CleanText(description, MaxDescriptionLength);
            if (about.Length > 0) json.WriteString("description", about);
            json.WriteStartObject("events");
            foreach (var (key, value) in events) json.WriteString(key, value);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static void WriteOptional(Utf8JsonWriter json, string key, string value)
    {
        if (value.Length > 0) json.WriteString(key, value);
        else json.WriteNull(key);
    }

    // ── Reading ────────────────────────────────────────────────────────────────

    /// <summary>Reads a scheme file and copies in the sounds it carries. Throws <see cref="SoundSchemeFileException"/>.</summary>
    /// <param name="schemeId">The scheme it becomes, whose folder its sounds go in (#948). A sound the file
    /// names but doesn't carry is kept only if that folder already has it, as it can for a gallery update.</param>
    /// <param name="fromGallery">The file came from the gallery, so its sounds must be ones every app reads alike, whatever the file says.</param>
    public static SoundSchemeImport Import(string path, ISoundService sounds, string schemeId, bool fromGallery = false)
    {
        var contents = Read(path, name => sounds.CustomSoundPath(schemeId, name) is not null, fromGallery);

        int added = 0, dropped = contents.Dropped;
        var landed = new Dictionary<string, string?>(StringComparer.Ordinal);
        var temp = Path.Combine(Path.GetTempPath(), "QuickMail-sounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (name, bytes) in contents.Sounds.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                var fileName = AddSound(name, bytes, sounds, schemeId, temp);
                landed[name] = fileName;
                if (fileName is not null) added++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging in the temp folder failed: full, or locked down. Sounds copied in so far are in
            // the scheme's folder, which the caller deletes for a scheme that was never made (#948).
            throw new SoundSchemeFileException("The scheme's sounds could not be copied into QuickMail. " + ex.Message, ex);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogService.Log("Could not tidy up after a sound scheme import", ex); }
        }

        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in contents.Events)
        {
            var stored = value;
            // A file the scheme carried follows it to the name it landed under; one already here stays as it is.
            if (SoundRef.TryParse(value, out var sound) && sound.Kind == SoundRefKind.Custom && landed.TryGetValue(sound.Name, out var fileName))
            {
                if (fileName is null)
                {
                    stored = SoundRef.None.ToStored();
                    dropped++;
                }
                else
                {
                    stored = SoundRef.Custom(fileName).ToStored();
                }
            }
            events[key] = stored;
        }
        return new SoundSchemeImport(contents.Name, events, added, dropped, contents.Author, contents.License, contents.Description);
    }

    /// <summary>Where a sound the scheme carried ends up, or null if it was refused.</summary>
    private static string? AddSound(string name, byte[] bytes, ISoundService sounds, string schemeId, string temp)
    {
        // Through the same door as a file chosen in Settings: decoded, length-checked, and numbered
        // rather than overwriting a different sound of the same name already here.
        Directory.CreateDirectory(temp);
        var staged = Path.Combine(temp, name);
        File.WriteAllBytes(staged, bytes);
        if (sounds.TryAddCustomSound(schemeId, staged, out var fileName, out var refusal)) return fileName;
        LogService.Log($"Sound scheme import: refused the sound {name}: {refusal}");
        return null;
    }

    /// <summary>
    /// Reads and checks a scheme file, as SCHEMES.md "Reading one" sets out, without putting anything
    /// in place. Throws <see cref="SoundSchemeFileException"/>, and nothing else.
    /// </summary>
    /// <param name="haveFile">Whether a sound the file names but doesn't carry is already in QuickMail's sounds folder, so the event keeps it.</param>
    /// <param name="fromGallery">Hold the sounds to the scheme-file rules even if the file is QuickMail 0.8.69's.</param>
    public static SoundSchemeFileContents Read(string path, Func<string, bool>? haveFile = null, bool fromGallery = false) =>
        Read(path, SchemeApp.QuickMail, haveFile, fromGallery);

    /// <summary><see cref="Read(string, Func{string, bool}?, bool)"/> for a given app; the tests use it to read app-kit's files as The Chat Place.</summary>
    internal static SoundSchemeFileContents Read(string path, SchemeApp app, Func<string, bool>? haveFile = null, bool fromGallery = false)
    {
        haveFile ??= _ => false;
        var unreadable = app.Unreadable;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxFileBytes) throw new SoundSchemeFileException(TooLarge);
            // The entry count from the directory itself, before ZipArchive builds an entry for each:
            // a hostile file could claim millions.
            var methods = CentralDirectory(stream);
            if (methods is null || methods.Count > MaxEntries) throw new SoundSchemeFileException(unreadable);
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (methods.Count != zip.Entries.Count) throw new SoundSchemeFileException(unreadable);
            List<ushort> compression = methods;

            // Only scheme.json and sounds/<safe name>; the first of a repeated name counts. Anything
            // else, a name with a NUL in it included, is never read or written anywhere.
            int? meta = null;
            var entries = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < zip.Entries.Count; i++)
            {
                var name = zip.Entries[i].FullName.Replace('\\', '/');
                if (name == SchemeEntry) meta ??= i;
                else if (name.StartsWith(SoundsPrefix, StringComparison.Ordinal) && app.TryParse("custom:" + name[SoundsPrefix.Length..], out _))
                    entries.TryAdd(name[SoundsPrefix.Length..], i);
            }
            bool Readable(int i) => !zip.Entries[i].IsEncrypted && compression[i] is 0 or 8;   // stored or DEFLATE only

            long budget = MaxFileBytes;
            if (meta is not { } metaIndex || !Readable(metaIndex)) throw new SoundSchemeFileException(unreadable);
            var metaBytes = ReadCapped(zip.Entries[metaIndex], MaxSchemeJsonBytes, ref budget) ?? throw new SoundSchemeFileException(unreadable);
            var (contents, legacy) = ParseSchemeJson(metaBytes, app);

            // A file QuickMail 0.8.69 wrote (no "app") may hold the MP3s and other sounds QuickMail
            // plays; those are checked when they're added, as a file chosen in Settings is. Anything
            // newer, or from the gallery, holds only sounds every app reads alike.
            var strict = fromGallery || !legacy;
            var droppedFiles = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in FilesUsed(contents.Events, app))
            {
                // Two names that differ only in case are one file on Windows: the first in event order counts.
                if (!seen.Add(name))
                {
                    droppedFiles.Add(name);
                    continue;
                }
                if (!entries.TryGetValue(name, out var index))
                {
                    if (!haveFile(name)) droppedFiles.Add(name);
                    continue;
                }
                byte[]? sound = null;
                if (Readable(index))
                {
                    try
                    {
                        sound = ReadCapped(zip.Entries[index], SoundCatalog.MaxCustomFileBytes, ref budget);
                        if (sound is not null && strict && SchemeWavProblem(sound) is { } problem)
                        {
                            LogService.Log($"Sound scheme import: dropped {name}: {problem}");
                            sound = null;
                        }
                    }
                    // Whatever a damaged entry makes the decompressor throw, it costs that sound only.
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException
                                                   or InvalidOperationException or ArgumentException or OverflowException)
                    {
                        LogService.Log($"Sound scheme import: could not read {name}", ex);
                    }
                }
                if (sound is null) droppedFiles.Add(name);
                else contents.Sounds[name] = sound;
            }

            var dropped = 0;
            foreach (var (key, value) in contents.Events.ToList())
            {
                if (app.TryParse(value, out var s) && s.Kind == SoundRefKind.Custom && droppedFiles.Contains(s.Name))
                {
                    contents.Events[key] = SoundRef.None.ToStored();
                    dropped++;
                }
            }
            return contents with { Dropped = dropped };
        }
        catch (SoundSchemeFileException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException
                                       or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new SoundSchemeFileException(unreadable, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SoundSchemeFileException("The file could not be opened. " + ex.Message, ex);
        }
    }

    /// <summary>The custom files the events use, each once, in QuickMail's event order.</summary>
    private static List<string> FilesUsed(Dictionary<string, string> events, SchemeApp app)
    {
        var files = new List<string>();
        foreach (var key in app.EventKeys)
        {
            if (events.TryGetValue(key, out var value) && app.TryParse(value, out var sound)
                && sound.Kind == SoundRefKind.Custom && !files.Contains(sound.Name, StringComparer.Ordinal))
                files.Add(sound.Name);
        }
        return files;
    }

    /// <summary>scheme.json's contents, and whether it is from QuickMail 0.8.69, which wrote no "app".</summary>
    private static (SoundSchemeFileContents Contents, bool Legacy) ParseSchemeJson(byte[] bytes, SchemeApp app)
    {
        JsonDocument doc;
        try
        {
            doc = ParseStrictJson(bytes, allowBom: true);
        }
        catch (JsonException ex)
        {
            throw new SoundSchemeFileException(app.Unreadable, ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SoundSchemeFileException(app.Unreadable);

            var format = Property(root, "format") is { } f ? WholeNumber(f) : null;
            if (format is not { } version || version < 1) throw new SoundSchemeFileException(app.Unreadable);
            if (version > CurrentFormat) throw new SoundSchemeFileException(app.NewerVersion);

            // Missing or null means QuickMail: 0.8.69 wrote no "app".
            var owner = Property(root, "app");
            var legacy = owner is null || owner.Value.ValueKind == JsonValueKind.Null;
            var ownerId = legacy ? AppId : owner!.Value.ValueKind == JsonValueKind.String ? JsonText(owner.Value) : null;
            if (ownerId != app.Id)
                throw new SoundSchemeFileException(app.ForAnotherApp(AppNameFor(ownerId)));

            if (Property(root, "events") is not { ValueKind: JsonValueKind.Object } eventsElement)
                throw new SoundSchemeFileException(app.Unreadable);

            var events = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in DistinctProperties(eventsElement).Take(MaxEvents))
            {
                // Only the app's own keys, exactly; a value it can't use plays nothing.
                if (key is null || !app.EventKeys.Contains(key, StringComparer.Ordinal)) continue;
                var text = value.ValueKind == JsonValueKind.String ? JsonText(value) : null;
                events[key] = text is not null && text == text.Trim() && app.TryParse(text, out var sound)
                    ? sound.ToStored() : SoundRef.None.ToStored();
            }

            string? Optional(string name, int max) =>
                Property(root, name) is { ValueKind: JsonValueKind.String } v && CleanText(JsonText(v), max) is { Length: > 0 } clean ? clean : null;

            var contents = new SoundSchemeFileContents(
                Name: Optional("name", SoundCatalog.MaxSchemeNameLength) ?? ImportedName,
                Author: Optional("author", MaxAuthorLength),
                License: Optional("license", MaxLicenseLength),
                Description: Optional("description", MaxDescriptionLength),
                Events: events,
                Sounds: new Dictionary<string, byte[]>(StringComparer.Ordinal),
                Dropped: 0);
            return (contents, legacy);
        }
    }

    /// <summary>What to call the app a scheme file is for: app-kit's name for it, else the value cleaned, else "another app".</summary>
    private static string AppNameFor(string? id)
    {
        if (id is not null && KnownApps.TryGetValue(id, out var known)) return known;
        var clean = CleanText(id, MaxAppNameLength);
        return clean.Length > 0 ? clean : "another app";
    }

    /// <summary>
    /// An entry's bytes, counted as they are read, never taken from what the archive says. Null if the
    /// entry alone is over <paramref name="limit"/>; throws once the whole file is over its limit.
    /// </summary>
    private static byte[]? ReadCapped(ZipArchiveEntry entry, long limit, ref long budget)
    {
        using var es = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        for (int read; (read = es.Read(buffer, 0, buffer.Length)) > 0;)
        {
            ms.Write(buffer, 0, read);
            budget -= read;
            if (budget < 0) throw new SoundSchemeFileException(TooLarge);
            if (ms.Length > limit) return null;
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Each entry's compression method, from the archive's central directory, in the order
    /// <see cref="ZipArchive.Entries"/> lists them. <see cref="ZipArchive"/> doesn't say, and reads
    /// Deflate64 as well, which other apps can't: only stored and DEFLATE entries are readable in every
    /// app (SCHEMES.md). Null if the directory can't be found or read.
    /// </summary>
    private static List<ushort>? CentralDirectory(Stream stream)
    {
        const int EndRecordSize = 22;
        var length = stream.Length;
        if (length < EndRecordSize) return null;
        var tailLength = (int)Math.Min(length, EndRecordSize + ushort.MaxValue);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);
        for (var at = tailLength - EndRecordSize; at >= 0; at--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at)) != 0x06054b50) continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 10));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at + 12));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at + 16));
            // A ZIP64 directory: never needed for 64 entries in 80 MB, so not one QuickMail writes or reads.
            // Any of these fields at its maximum sends ZipArchive to the ZIP64 record instead, so this
            // parser would be reading a different directory from it.
            if (count == ushort.MaxValue || offset == uint.MaxValue || size == uint.MaxValue
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 4)) == ushort.MaxValue
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 6)) == ushort.MaxValue
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 8)) == ushort.MaxValue) return null;
            if ((long)offset + size > length || size > 1024 * 1024) return null;
            var directory = new byte[size];
            stream.Position = offset;
            stream.ReadExactly(directory);
            var methods = new List<ushort>(count);
            var p = 0;
            for (var i = 0; i < count; i++)
            {
                if (p + 46 > directory.Length || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(p)) != 0x02014b50) return null;
                methods.Add(BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 10)));
                p += 46 + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 28))
                        + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 30))
                        + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 32));
            }
            return methods;
        }
        return null;
    }

    // ── Sounds a scheme file may hold ──────────────────────────────────────────

    /// <summary>
    /// Why these bytes aren't a sound a scheme file may hold, or null if they are: a 16-bit PCM WAV
    /// (format tag 1, not WAVE_FORMAT_EXTENSIBLE), mono or stereo, 8 to 192 kHz, at most 30 seconds and
    /// 5 MB (SCHEMES.md, "Sounds in a scheme file"). Every app reads those alike. Never throws.
    /// </summary>
    internal static string? SchemeWavProblem(ReadOnlySpan<byte> data)
    {
        if (data.Length > SoundCatalog.MaxCustomFileBytes) return "It's larger than 5 MB.";
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
            return "It isn't a WAV file.";
        // Only what the RIFF chunk says it holds is read, as every app's WAV reader does: a file whose
        // RIFF size leaves out its fmt or data chunk is refused here as it is elsewhere.
        long riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (riffSize < 4) return "It isn't a WAV file.";
        data = data[..(int)Math.Min(data.Length, 8 + riffSize)];
        int channels = 0, rate = 0;
        var haveFormat = false;
        long at = 12;
        while (at + 8 <= data.Length)
        {
            var id = data.Slice((int)at, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data[((int)at + 4)..]);
            var body = at + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (haveFormat) { at = body + size + (size & 1); continue; }   // the first counts
                if (size < 16 || body + 16 > data.Length) return "Its format can't be read.";
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)body..]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data[((int)body + 2)..]);
                rate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[((int)body + 4)..]), int.MaxValue);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(data[((int)body + 14)..]);
                if (tag != 1) return "It has to be a 16-bit PCM WAV file.";
                // As every app's WAV reader counts it: whole bytes per sample.
                if ((bits + 7) / 8 != 2 || channels is not (1 or 2)) return "It has to be a 16-bit WAV file, mono or stereo.";
                if (rate is < 8000 or > 192000) return "Its sample rate isn't one that can be played.";
                haveFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!haveFormat) return "Its format can't be read.";
                var frameSize = 2 * channels;
                if ((double)(size / frameSize) / rate > SoundCatalog.MaxCustomSoundLength.TotalSeconds) return "It's longer than 30 seconds.";
                var available = Math.Min(size, data.Length - body);
                return available / frameSize == 0 ? "It has no sound in it." : null;
            }
            at = body + size + (size & 1);
        }
        return "It has no sound in it.";
    }

    // ── Text and JSON from a file (SCHEMES.md) ─────────────────────────────────

    /// <summary>The name to show for an imported scheme: cleaned, and "Imported scheme" if that leaves nothing.</summary>
    public static string CleanName(string? name)
    {
        var text = CleanText(name, SoundCatalog.MaxSchemeNameLength);
        return text.Length == 0 ? ImportedName : text;
    }

    /// <summary>
    /// Text from a file or the gallery, as SCHEMES.md cleans it, in this order: control characters and
    /// unpaired surrogates removed, trimmed, cut to <paramref name="max"/> UTF-16 units without
    /// splitting a character, and trimmed again. Empty if nothing is left.
    /// </summary>
    public static string CleanText(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var kept = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                kept.Append(c).Append(text[++i]);
                continue;
            }
            if (char.IsSurrogate(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Control) continue;
            kept.Append(c);
        }
        return Cut(kept.ToString().Trim(), max).Trim();
    }

    /// <summary>Text cut to at most <paramref name="max"/> characters, never between the two halves of an emoji or other surrogate pair.</summary>
    public static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var end = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..end].TrimEnd();
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Strict JSON (RFC 8259), as SCHEMES.md has it: valid UTF-8, no NaN or Infinity, no comments or
    /// trailing commas, nested at most 8 deep counting the outermost. A byte-order mark is allowed only
    /// where <paramref name="allowBom"/> says (scheme.json; never the gallery's index). Throws JsonException.
    /// </summary>
    internal static JsonDocument ParseStrictJson(byte[] bytes, bool allowBom)
    {
        var body = bytes.AsMemory();
        if (body.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            if (!allowBom) throw new JsonException("A byte-order mark isn't allowed here.");
            body = body[3..];
        }
        try
        {
            StrictUtf8.GetCharCount(body.Span);
        }
        catch (DecoderFallbackException ex)
        {
            throw new JsonException("Not UTF-8.", ex);
        }
        return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
    }

    /// <summary>A property's value, the last one if the name is repeated (SCHEMES.md: "the last one counts"); null if absent.</summary>
    internal static JsonElement? Property(JsonElement obj, string name)
    {
        JsonElement? found = null;
        foreach (var property in obj.EnumerateObject())
            if (property.NameEquals(name)) found = property.Value;
        return found;
    }

    /// <summary>
    /// An object's properties in order, each name once, holding its last value, as a JSON reader that
    /// keeps the last of a repeated key lists them. A name that isn't valid text is listed as null.
    /// </summary>
    private static List<(string? Name, JsonElement Value)> DistinctProperties(JsonElement obj)
    {
        var list = new List<(string? Name, JsonElement Value)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            string? name;
            try { name = property.Name; }
            catch (InvalidOperationException) { name = null; }   // an unpaired surrogate escape
            if (name is not null && at.TryGetValue(name, out var index)) list[index] = (name, property.Value);
            else
            {
                if (name is not null) at[name] = list.Count;
                list.Add((name, property.Value));
            }
        }
        return list;
    }

    /// <summary>A JSON whole number that fits in 32 bits, or null: 1.5, 1e3, "1" and 99999999999 are not.</summary>
    internal static int? WholeNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    /// <summary>
    /// A JSON string's text. An escaped unpaired surrogate (<c>"\ud800"</c>) is valid JSON, but
    /// <see cref="JsonElement.GetString"/> refuses it, so it is unescaped here instead and left for
    /// <see cref="CleanText"/> to remove, as other apps do.
    /// </summary>
    internal static string? JsonText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return Unescape(value.GetRawText());
        }
    }

    private static string Unescape(string raw)
    {
        var text = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length - 1; i++)   // inside the quotes
        {
            var c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length - 1)
            {
                text.Append(c);
                continue;
            }
            var e = raw[++i];
            switch (e)
            {
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'u' when i + 4 < raw.Length - 1 && int.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    text.Append((char)code);
                    i += 4;
                    break;
                default: text.Append(e); break;   // \" \\ \/
            }
        }
        return text.ToString();
    }
}
