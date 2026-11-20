using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>A settings file that cannot be imported, with the reason in words a user can act on.</summary>
public sealed class SettingsBundleException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A settings file (.qmsettings) opened and checked (issue #507, spec D1): a zip holding
/// <c>manifest.json</c> and the profile files of the categories it carries. Reading never trusts
/// the archive's paths — only the names in <see cref="KnownFiles"/> and theme files are accepted,
/// so an entry such as <c>..\..\x</c> can never be written anywhere.
/// </summary>
public sealed class SettingsBundle
{
    public const string FileExtension = ".qmsettings";
    public const string ManifestName = "manifest.json";
    public const string FoldersName = "folders.json";
    public const string GroupMembersName = "group-members.json";
    public const string CalendarName = "calendar.ics";
    public const string ThemesPrefix = "themes/";
    public const string SoundsPrefix = SoundCatalog.CustomFolderName + "/";

    // A real file is about 20 KB, plus any custom sounds. The cap only stops a hostile archive from filling memory.
    private const long MaxTotalBytes = 64L * 1024 * 1024;

    // Well under MaxTotalBytes, because going over it makes the whole file unreadable on import. A
    // sound that doesn't fit stays behind, and anything using it falls back to the built-in sound.
    // Counted once per different sound (#955): a file several schemes use is written once for each.
    internal const long MaxExportedSoundBytes = 32L * 1024 * 1024;

    // What every entry of an exported file may add up to, with each scheme's copy of a shared sound
    // counted in full: a little under MaxTotalBytes, so an import can always read what was exported.
    internal const long MaxExportedTotalBytes = MaxTotalBytes - 1L * 1024 * 1024;

    /// <summary>The profile files each category carries, by their name in the profile directory.</summary>
    public static readonly IReadOnlyDictionary<SettingsCategory, string[]> CategoryFiles =
        new Dictionary<SettingsCategory, string[]>
        {
            [SettingsCategory.Accounts]      = ["accounts.json"],
            [SettingsCategory.RulesAndViews] = ["rules.json", "views.json", "flags.json", "watches.json", "folderviews.json"],
            [SettingsCategory.AddressBook]   = ["contacts.json", "groups.json", GroupMembersName],
            [SettingsCategory.Templates]     = ["templates.json"],
            [SettingsCategory.Keyboard]      = ["hotkeys.json"],
            [SettingsCategory.Appearance]    = ["rowlayout.json"],
            [SettingsCategory.LocalCalendar] = [CalendarName],
            [SettingsCategory.Preferences]   = ["config.ini", "custom.lex"],
        };

    /// <summary>Every entry name a bundle may hold besides theme files.</summary>
    public static readonly IReadOnlySet<string> KnownFiles =
        new HashSet<string>(CategoryFiles.Values.SelectMany(f => f).Append(ManifestName).Append(FoldersName),
                            StringComparer.OrdinalIgnoreCase);

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private SettingsBundle(SettingsBundleManifest manifest, Dictionary<string, byte[]> entries)
    {
        Manifest = manifest;
        Entries = entries;
    }

    public SettingsBundleManifest Manifest { get; }

    /// <summary>Entry name (forward slashes, as in the zip) to its bytes.</summary>
    public IReadOnlyDictionary<string, byte[]> Entries { get; }

    public bool Has(SettingsCategory category) => Manifest.Categories.Contains(category);

    public byte[]? Get(string name) => Entries.TryGetValue(name, out var bytes) ? bytes : null;

    /// <summary>The entry as text, BOM-aware, or null when absent.</summary>
    public string? GetText(string name)
    {
        var bytes = Get(name);
        if (bytes is null) return null;
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public IEnumerable<KeyValuePair<string, byte[]>> ThemeEntries =>
        Entries.Where(e => e.Key.StartsWith(ThemesPrefix, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<KeyValuePair<string, byte[]>> SoundEntries =>
        Entries.Where(e => e.Key.StartsWith(SoundsPrefix, StringComparison.OrdinalIgnoreCase));

    // ── Reading ──────────────────────────────────────────────────────────────────

    public static SettingsBundle Open(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Open(stream);
        }
        catch (SettingsBundleException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsBundleException("The file could not be opened. " + ex.Message, ex);
        }
    }

    public static SettingsBundle Open(Stream stream)
    {
        const string notASettingsFile = "This file could not be read as a QuickMail settings file.";
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;   // a directory entry
                var name = entry.FullName.Replace('\\', '/');
                if (!IsAcceptedName(name)) continue;           // unknown or unsafe: ignored, never extracted
                total += entry.Length;
                if (total > MaxTotalBytes) throw new SettingsBundleException(notASettingsFile);
                using var es = entry.Open();
                using var ms = new MemoryStream();
                // Counted as it is read as well: the length above is only what the archive claims.
                var buffer = new byte[81920];
                long copied = 0;
                var allowed = MaxTotalBytes - (total - entry.Length);
                for (int read; (read = es.Read(buffer, 0, buffer.Length)) > 0;)
                {
                    copied += read;
                    if (copied > allowed) throw new SettingsBundleException(notASettingsFile);
                    ms.Write(buffer, 0, read);
                }
                entries[name] = ms.ToArray();
            }
        }
        catch (SettingsBundleException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new SettingsBundleException(notASettingsFile, ex);
        }

        if (!entries.TryGetValue(ManifestName, out var manifestBytes))
            throw new SettingsBundleException(notASettingsFile);

        SettingsBundleManifest? manifest;
        try
        {
            using var reader = new StreamReader(new MemoryStream(manifestBytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            manifest = JsonSerializer.Deserialize<SettingsBundleManifest>(reader.ReadToEnd());
        }
        catch (JsonException ex) { throw new SettingsBundleException(notASettingsFile, ex); }
        if (manifest is null || manifest.SchemaVersion < 1)
            throw new SettingsBundleException(notASettingsFile);
        if (manifest.SchemaVersion > SettingsBundleManifest.CurrentSchemaVersion)
        {
            var from = string.IsNullOrWhiteSpace(manifest.AppVersion) ? "a newer version of QuickMail" : $"a newer version of QuickMail ({manifest.AppVersion})";
            throw new SettingsBundleException($"This file was made by {from}. Update QuickMail and try again.");
        }

        return new SettingsBundle(manifest, entries);
    }

    /// <summary>
    /// A known profile file, a theme file with a plain name directly under its folder, or a sound file:
    /// a rule's, directly under <c>sounds/</c>, or a scheme's, in its scheme's folder under that (#948).
    /// </summary>
    internal static bool IsAcceptedName(string name)
    {
        if (KnownFiles.Contains(name)) return true;
        if (name.StartsWith(SoundsPrefix, StringComparison.OrdinalIgnoreCase))
            return SoundEntryPath(name) is not null;
        if (!name.StartsWith(ThemesPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var file = name[ThemesPrefix.Length..];
        return file.Length > 0
               && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
               && file.IndexOfAny(['/', '\\', ':']) < 0
               && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
               && file != "." && file != "..";
    }

    /// <summary>
    /// Where a <c>sounds/</c> entry goes, relative to the sounds folder: <c>&lt;file&gt;</c> for a rule's
    /// sound, or <c>&lt;scheme folder&gt;\&lt;file&gt;</c> for a scheme's (#948). Null for any other name,
    /// which is never extracted.
    /// </summary>
    internal static string? SoundEntryPath(string name)
    {
        if (!name.StartsWith(SoundsPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = name[SoundsPrefix.Length..].Split('/');
        return parts switch
        {
            [var file] when SoundRef.IsSafeCustomFileName(file) => file,
            [var folder, var file] when SoundRef.IsSafeCustomFileName(file) && SoundCatalog.SchemeFolderName(folder) is { } safe
                && safe.Equals(folder, StringComparison.OrdinalIgnoreCase) => Path.Combine(safe, file),
            _ => null,
        };
    }

    // ── Writing ──────────────────────────────────────────────────────────────────

    /// <summary>What an export needs that is not a file in the profile directory.</summary>
    public sealed class ExportInputs
    {
        public required string ProfileDir { get; init; }
        public required IReadOnlyCollection<SettingsCategory> Categories { get; init; }
        public string AppVersion { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public DateTime NowUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Per account, each known folder's id (FullName) to its display path (spec R5).</summary>
        public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> FolderPaths { get; init; } =
            new Dictionary<Guid, IReadOnlyDictionary<string, string>>();

        /// <summary>The Local Calendar's appointments, written as calendar.ics when that category is chosen.</summary>
        public IReadOnlyList<CalendarEvent> LocalCalendarEvents { get; init; } = [];
    }

    /// <summary>What an export wrote, for the result announcement.</summary>
    public sealed record ExportSummary(int CategoryCount, long Bytes);

    /// <summary>Writes a settings file to <paramref name="destination"/>.</summary>
    public static void Write(ExportInputs inputs, Stream destination)
    {
        var dir = inputs.ProfileDir;
        var accounts = ReadList<AccountModel>(Path.Combine(dir, "accounts.json"));
        var manifest = new SettingsBundleManifest
        {
            AppVersion = inputs.AppVersion,
            ExportedUtc = inputs.NowUtc,
            DeviceName = inputs.DeviceName,
            Categories = SettingsCategoryInfo.All.Select(c => c.Category).Where(inputs.Categories.Contains).ToList(),
            Accounts = accounts.Select(a => new SettingsBundleAccount
            {
                Id = a.Id,
                Username = a.Username,
                BackendKind = a.BackendKind,
                DisplayName = a.AccountLabel,
                IsShared = a.IsShared,
            }).ToList(),
        };

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var zip = new ZipOut(archive);
        AddText(zip, ManifestName, JsonSerializer.Serialize(manifest, JsonOptions));

        // Folder names travel with anything that names a folder: accounts (archive folder), rules and
        // views, and preferences (startup folder).
        if (manifest.Categories.Any(c => c is SettingsCategory.Accounts or SettingsCategory.RulesAndViews or SettingsCategory.Preferences)
            && inputs.FolderPaths.Count > 0)
        {
            var folders = inputs.FolderPaths.ToDictionary(kv => kv.Key.ToString("D"), kv => kv.Value);
            AddText(zip, FoldersName, JsonSerializer.Serialize(folders, JsonOptions));
        }

        foreach (var category in manifest.Categories)
        {
            switch (category)
            {
                case SettingsCategory.AddressBook:
                    WriteAddressBook(zip, dir);
                    break;
                case SettingsCategory.LocalCalendar:
                    if (inputs.LocalCalendarEvents.Count > 0)
                        AddText(zip, CalendarName, IcsModel.ExportCalendar(inputs.LocalCalendarEvents));
                    break;
                case SettingsCategory.Appearance:
                    CopyIfExists(zip, dir, "rowlayout.json");
                    var themes = Path.Combine(dir, "themes");
                    if (Directory.Exists(themes))
                        foreach (var file in Directory.GetFiles(themes, "*.json"))
                            AddBytes(zip, ThemesPrefix + Path.GetFileName(file), File.ReadAllBytes(file));
                    break;
                default:
                    foreach (var name in CategoryFiles[category])
                        CopyIfExists(zip, dir, name);
                    break;
            }
        }

        // Custom sounds travel with what can name one: rules, and event sounds in the preferences.
        if (manifest.Categories.Any(c => c is SettingsCategory.RulesAndViews or SettingsCategory.Preferences))
            WriteSounds(zip, dir);
    }

    // The rules' sounds directly under sounds/, and each scheme's in its own folder under that (#948).
    private static void WriteSounds(ZipOut zip, string profileDir)
    {
        var root = Path.Combine(profileDir, SoundCatalog.CustomFolderName);
        if (!Directory.Exists(root)) return;

        var folders = new List<(string Prefix, string Path)> { (SoundsPrefix, root) };
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (SoundCatalog.SchemeFolderName(name) is { } safe && safe.Equals(name, StringComparison.OrdinalIgnoreCase))
                folders.Add((SoundsPrefix + safe + "/", dir));
        }

        // Each scheme has its own copy of a file several of them use (#948), so the same sound can be
        // here several times. The limit counts each different sound once (#955), so that copying a
        // scheme doesn't make an export leave sounds out. Every copy is still written, as the import
        // expects each in its scheme's folder; what bounds them all is that the import must still be
        // able to read the file, so the whole file stays within what an import accepts.
        long distinct = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int leftOut = 0;
        foreach (var (prefix, folder) in folders)
        {
            foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                var length = new FileInfo(file).Length;
                if (!SoundRef.IsSafeCustomFileName(name) || length > SoundCatalog.MaxCustomFileBytes) continue;
                var bytes = File.ReadAllBytes(file);
                var key = Convert.ToHexString(SHA256.HashData(bytes));
                var isNew = !seen.Contains(key);
                if ((isNew && distinct + bytes.Length > MaxExportedSoundBytes)
                    || zip.Written + bytes.Length > MaxExportedTotalBytes)
                {
                    leftOut++;
                    continue;
                }
                if (isNew)
                {
                    seen.Add(key);
                    distinct += bytes.Length;
                }
                AddBytes(zip, prefix + name, bytes);
            }
        }
        if (leftOut > 0)
            LogService.Log($"Settings export: left out {leftOut} sound file(s), as the sounds already fill the file.");
    }

    // Local contacts only: synced ones come back from their server and would only arrive stale. A
    // group can list a synced contact, so the address of every such member travels on its own,
    // letting the import find the same person on the other computer (spec R8).
    private static void WriteAddressBook(ZipOut zip, string dir)
    {
        var contacts = ReadList<ContactModel>(Path.Combine(dir, "contacts.json"));
        var groups = ReadList<GroupModel>(Path.Combine(dir, "groups.json"));
        var local = contacts.Where(c => c.IsLocal).ToList();
        AddText(zip, "contacts.json", JsonSerializer.Serialize(local, JsonOptions));
        AddText(zip, "groups.json", JsonSerializer.Serialize(groups, JsonOptions));

        var byId = contacts.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var syncedMembers = groups.SelectMany(g => g.MemberContactIds).Distinct()
            .Where(id => byId.TryGetValue(id, out var c) && !c.IsLocal)
            .ToDictionary(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture), id => byId[id].EmailAddress);
        if (syncedMembers.Count > 0)
            AddText(zip, GroupMembersName, JsonSerializer.Serialize(syncedMembers, JsonOptions));
    }

    /// <summary>
    /// Each account's folder ids mapped to display paths, for <see cref="ExportInputs.FolderPaths"/>.
    /// A Graph folder's path is built up its parents' display names; an IMAP folder's id already is
    /// its path. The parent walk stops at a folder not in the list (Graph's hidden root) and at a cycle.
    /// </summary>
    public static Dictionary<Guid, IReadOnlyDictionary<string, string>> BuildFolderPaths(
        IReadOnlyDictionary<Guid, List<MailFolderModel>> folders)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        foreach (var (accountId, list) in folders)
        {
            var real = list.Where(f => !f.IsHeader && f.FullName.Length > 0).ToList();
            if (real.Count == 0) continue;
            var byId = real.GroupBy(f => f.FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var folder in byId.Values)
            {
                if (folder.ParentId is null) { paths[folder.FullName] = folder.FullName; continue; }
                var parts = new List<string> { folder.DisplayName };
                var seen = new HashSet<string>(StringComparer.Ordinal) { folder.FullName };
                var parent = folder.ParentId;
                while (parent is not null && seen.Add(parent) && byId.TryGetValue(parent, out var p))
                {
                    parts.Insert(0, p.DisplayName);
                    parent = p.ParentId;
                }
                paths[folder.FullName] = string.Join("/", parts);
            }
            result[accountId] = paths;
        }
        return result;
    }

    internal static List<T> ReadList<T>(string path)
    {
        if (!File.Exists(path)) return [];
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }

    private static void CopyIfExists(ZipOut zip, string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) AddBytes(zip, name, File.ReadAllBytes(path));
    }

    private static void AddText(ZipOut zip, string name, string text) =>
        AddBytes(zip, name, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));

    private static void AddBytes(ZipOut zip, string name, byte[] bytes)
    {
        var entry = zip.Archive.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
        zip.Written += bytes.Length;
    }

    /// <summary>The archive being written, and how many bytes its entries hold so far, uncompressed, which is what an import counts.</summary>
    private sealed class ZipOut(ZipArchive archive)
    {
        public ZipArchive Archive { get; } = archive;
        public long Written { get; set; }
    }
}
