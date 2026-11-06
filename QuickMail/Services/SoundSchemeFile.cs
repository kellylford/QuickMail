using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed record SoundSchemeImport(string Name, Dictionary<string, string> Events, int SoundsAdded, int SoundsDropped,
    string? Author = null, string? License = null);

/// <summary>
/// A sound scheme shared as a file (#932): a zip holding <c>scheme.json</c> and the custom sounds it
/// uses under <c>sounds/</c>. Reading never trusts the archive: only those names are accepted, every
/// size is capped as it is read rather than as the archive claims, every stored sound is checked, and
/// every sound file goes through the same checks as one chosen in Settings, decoding included.
/// </summary>
public static class SoundSchemeFile
{
    public const string FileExtension = ".qmsounds";
    public const string FileFilter = "QuickMail sound scheme (*.qmsounds)|*.qmsounds";

    private const string SchemeEntry = "scheme.json";
    private const string SoundsPrefix = SoundCatalog.CustomFolderName + "/";
    private const int CurrentFormat = 1;
    private const long MaxSchemeJsonBytes = 64 * 1024;
    private const int MaxEntries = 64;
    private const int MaxEvents = 100;

    /// <summary>The largest scheme file accepted: every event with its own custom file at the largest size, with room to spare.</summary>
    public const long MaxFileBytes = 80L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class SchemeJson
    {
        [JsonPropertyName("format")] public int Format { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("license")] public string? License { get; set; }
        [JsonPropertyName("events")] public Dictionary<string, string>? Events { get; set; }
    }

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

    /// <summary>Writes a scheme and the custom sounds it uses. Throws IOException or UnauthorizedAccessException.</summary>
    /// <param name="author">Who made it, if known, so the credit a licence such as CC-BY asks for travels.</param>
    /// <param name="license">Its licence, if known.</param>
    public static void Export(string path, string name, IReadOnlyDictionary<string, string> events, ISoundService sounds,
        string? author = null, string? license = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var json = JsonSerializer.Serialize(new SchemeJson { Format = CurrentFormat, Name = name, Author = author, License = license, Events = new(events) }, JsonOptions);
            using (var es = zip.CreateEntry(SchemeEntry).Open())
                es.Write(Encoding.UTF8.GetBytes(json));

            long total = 0;
            var files = events.Values
                .Select(v => SoundRef.TryParse(v, out var s) && s.Kind == SoundRefKind.Custom ? s.Name : null)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                // A file that has gone stays behind; the import sets that event to None and says so.
                if (sounds.CustomSoundPath(file) is not { } source) continue;
                var bytes = File.ReadAllBytes(source);
                if (bytes.Length > SoundCatalog.MaxCustomFileBytes || total + bytes.Length > MaxFileBytes) continue;
                total += bytes.Length;
                using var es = zip.CreateEntry(SoundsPrefix + file).Open();
                es.Write(bytes);
            }
        }
        File.WriteAllBytes(path, stream.ToArray());   // built in memory first, so a failure leaves no half-written file
    }

    /// <summary>Reads a scheme file and copies in the sounds it carries. Throws <see cref="SoundSchemeFileException"/>.</summary>
    public static SoundSchemeImport Import(string path, ISoundService sounds)
    {
        var (scheme, files) = Read(path);

        var name = CleanName(scheme.Name);
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, dropped = 0;
        var landed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var temp = Path.Combine(Path.GetTempPath(), "QuickMail-sounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (key, value) in (scheme.Events ?? []).Take(MaxEvents))
            {
                if (key.Length is 0 or > 40 || !key.All(char.IsAsciiLetterOrDigit)) continue;
                if (!SoundRef.TryParse(value, out var sound)) continue;   // unreadable: the event plays nothing
                if (sound.Kind == SoundRefKind.Custom)
                {
                    if (!landed.TryGetValue(sound.Name, out var fileName))
                    {
                        fileName = AddSound(sound.Name, files, sounds, temp);
                        landed[sound.Name] = fileName;
                        if (fileName is not null && files.ContainsKey(sound.Name)) added++;
                    }
                    if (fileName is null)
                    {
                        dropped++;
                        sound = SoundRef.None;
                    }
                    else
                    {
                        sound = SoundRef.Custom(fileName);
                    }
                }
                events[key] = sound.ToStored();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging in the temp folder failed: full, or locked down. Sounds copied in so far stay,
            // as files only; nothing names them until a scheme does.
            throw new SoundSchemeFileException("The scheme's sounds could not be copied into QuickMail. " + ex.Message, ex);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogService.Log("Could not tidy up after a sound scheme import", ex); }
        }
        return new SoundSchemeImport(name, events, added, dropped, CleanCredit(scheme.Author, 60), CleanCredit(scheme.License, 40));
    }

    /// <summary>Where a sound the scheme names ends up: copied in from the file, or already here. Null if neither.</summary>
    private static string? AddSound(string name, Dictionary<string, byte[]> files, ISoundService sounds, string temp)
    {
        if (!files.TryGetValue(name, out var bytes))
            return sounds.CustomSoundPath(name) is null ? null : name;

        // Through the same door as a file chosen in Settings: decoded, length-checked, and numbered
        // rather than overwriting a different sound of the same name already here.
        Directory.CreateDirectory(temp);
        var staged = Path.Combine(temp, name);
        File.WriteAllBytes(staged, bytes);
        if (sounds.TryAddCustomSound(staged, out var fileName, out var refusal)) return fileName;
        LogService.Log($"Sound scheme import: refused the sound {name}: {refusal}");
        return null;
    }

    /// <summary>The name to show for an imported scheme: one line, trimmed, and not too long.</summary>
    public static string CleanName(string? name)
    {
        var text = Cut(new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim(), SoundCatalog.MaxSchemeNameLength);
        return text.Length == 0 ? "Imported scheme" : text;
    }

    private static string? CleanCredit(string? text, int max)
    {
        var clean = Cut(new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim(), max);
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>Text cut to at most <paramref name="max"/> characters, never between the two halves of an emoji or other surrogate pair.</summary>
    public static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var end = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..end].TrimEnd();
    }

    private static (SchemeJson Scheme, Dictionary<string, byte[]> Files) Read(string path)
    {
        const string notASchemeFile = "This file could not be read as a QuickMail sound scheme.";
        SchemeJson? scheme = null;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxFileBytes) throw new SoundSchemeFileException(notASchemeFile);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) throw new SoundSchemeFileException(notASchemeFile);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                long limit;
                if (name.Equals(SchemeEntry, StringComparison.OrdinalIgnoreCase)) limit = MaxSchemeJsonBytes;
                else if (name.StartsWith(SoundsPrefix, StringComparison.OrdinalIgnoreCase)
                         && SoundRef.IsSafeCustomFileName(name[SoundsPrefix.Length..])) limit = SoundCatalog.MaxCustomFileBytes;
                else continue;   // unknown or unsafe: ignored, never extracted

                var bytes = ReadCapped(entry, limit, notASchemeFile);
                total += bytes.Length;
                if (total > MaxFileBytes) throw new SoundSchemeFileException(notASchemeFile);
                if (limit == MaxSchemeJsonBytes)
                {
                    using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    scheme = JsonSerializer.Deserialize<SchemeJson>(reader.ReadToEnd());
                }
                else
                {
                    files[name[SoundsPrefix.Length..]] = bytes;
                }
            }
        }
        catch (SoundSchemeFileException) { throw; }
        catch (JsonException ex) { throw new SoundSchemeFileException(notASchemeFile, ex); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new SoundSchemeFileException(notASchemeFile, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SoundSchemeFileException("The file could not be opened. " + ex.Message, ex);
        }

        if (scheme is null || scheme.Format < 1) throw new SoundSchemeFileException(notASchemeFile);
        if (scheme.Format > CurrentFormat)
            throw new SoundSchemeFileException("This sound scheme was made by a newer version of QuickMail. Update QuickMail and try again.");
        return (scheme, files);
    }

    // Counted as it is read: the length in the archive is only what the archive claims.
    private static byte[] ReadCapped(ZipArchiveEntry entry, long limit, string error)
    {
        if (entry.Length > limit) throw new SoundSchemeFileException(error);
        using var es = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long copied = 0;
        for (int read; (read = es.Read(buffer, 0, buffer.Length)) > 0;)
        {
            copied += read;
            if (copied > limit) throw new SoundSchemeFileException(error);
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
