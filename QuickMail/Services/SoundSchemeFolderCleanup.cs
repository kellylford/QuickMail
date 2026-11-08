using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Removes scheme folders left behind with no scheme (#955): a new, copied or imported scheme that
/// Settings had open when QuickMail stopped, or a scheme folder a settings file brought without the
/// preferences that use it. Settings export carries every scheme folder, so a forgotten one counts
/// against its size limit.
/// <para>
/// Only a folder known to have been left behind is ever removed. Each scheme folder made for a scheme
/// not saved yet is written down in <see cref="JournalFileName"/> when it is made, and crossed off
/// when the scheme is saved or its folder deleted. A folder that isn't written down is never touched,
/// so a config.ini that has been deleted, reset or lost can't make every scheme of the person's own
/// look like it belongs to nothing.
/// </para>
/// <para>
/// Even a folder that is written down is kept if config.ini mentions its scheme anywhere, if a
/// settings-import backup's config.ini does, or if the caller says a scheme still uses it, such as one
/// open in Settings; and nothing at all is removed if config.ini, or any backup's, can't be read. It
/// looks only at folders named exactly like the folder of a scheme of the person's own, never at a
/// built-in scheme's folder or a rule's sound, and never through a link. The log gives counts only.
/// </para>
/// </summary>
public static class SoundSchemeFolderCleanup
{
    /// <summary>In the sounds folder: the folders of schemes not saved yet, one per line. Not a sound file name, so never listed or exported.</summary>
    public const string JournalFileName = "unsaved-schemes.txt";

    // Any scheme id of the person's own written anywhere in a config.ini: a section header, the
    // scheme in use, or something a newer QuickMail wrote. Counted as owning its folder.
    private static readonly Regex UserSchemeIdPattern = new(
        Regex.Escape(SoundCatalog.UserSchemePrefix) + "[A-Za-z0-9-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly object JournalGate = new();

    /// <summary>At startup, after any settings import and the one-time move.</summary>
    public static void RunAtStartup(string profileDir) => Run(profileDir, []);

    /// <summary>
    /// Writes down scheme folders made for schemes that aren't saved yet, before anything goes in
    /// them, so they can be removed if they are never saved. Never throws; false if it couldn't.
    /// </summary>
    public static bool NoteUnsaved(string soundsFolder, IEnumerable<string> schemeIds) =>
        ChangeJournal(soundsFolder, journal => journal.UnionWith(FolderNames(schemeIds)));

    /// <summary>Crosses off schemes now saved, or whose folders are gone. Never throws; false if it couldn't.</summary>
    public static bool ForgetUnsaved(string soundsFolder, IEnumerable<string> schemeIds) =>
        ChangeJournal(soundsFolder, journal => journal.ExceptWith(FolderNames(schemeIds)));

    /// <summary>
    /// Removes each folder written down as unsaved that nothing owns, and crosses off those that
    /// something does. Never throws.
    /// </summary>
    /// <param name="profileDir">The profile folder, holding config.ini, the sounds folder and any import backups.</param>
    /// <param name="alsoKeep">Schemes that own their folders whatever config.ini says, such as those open in Settings.</param>
    /// <returns>How many folders were removed, or null if it couldn't be sure what is owned and so removed nothing.</returns>
    public static int? Run(string profileDir, IEnumerable<string> alsoKeep)
    {
        try
        {
            var sounds = Path.Combine(profileDir, SoundCatalog.CustomFolderName);
            if (!File.Exists(Path.Combine(sounds, JournalFileName))) return 0;
            if (OwnedFolders(profileDir) is not { } saved) return null;
            var owned = new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase);
            owned.UnionWith(FolderNames(alsoKeep));

            int? removed = null;
            ChangeJournal(sounds, journal =>
            {
                removed = RemoveUnowned(sounds, journal, owned);
                // Crossed off: each one removed, or saved in a config.ini and so kept for good. One
                // kept only because the caller is still using it stays written down, as does one
                // that couldn't be removed, to be looked at again.
                journal.RemoveWhere(name => saved.Contains(name) || !Directory.Exists(Path.Combine(sounds, name)));
            });
            return removed;
        }
        catch (Exception ex)
        {
            LogService.Log("Sound files: could not check for scheme folders left with no scheme; nothing was removed", ex);
            return null;
        }
    }

    /// <summary>
    /// The folder names config.ini and every import backup's config.ini own, or null if any of them
    /// can't be read or doesn't look like a config.ini with sound schemes in it.
    /// </summary>
    internal static HashSet<string>? OwnedFolders(string profileDir)
    {
        var configFile = Path.Combine(profileDir, "config.ini");
        if (!File.Exists(configFile)) return null;
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!AddOwned(File.ReadAllLines(configFile), owned)) return null;

        foreach (var backup in Directory.GetDirectories(profileDir, "backup-*"))
        {
            var saved = Path.Combine(backup, "config.ini");
            // A backup from before sound schemes owns nothing, but is no reason to stop.
            if (File.Exists(saved)) AddOwned(File.ReadAllLines(saved), owned);
        }
        return owned;
    }

    /// <summary>Adds the folders a config.ini owns. False if it doesn't look like one written since sounds came in.</summary>
    private static bool AddOwned(string[] lines, HashSet<string> owned)
    {
        // Every QuickMail with sound schemes writes PlayEventSounds. Without it, the file is from
        // before sounds, or isn't whole, and its having no schemes proves nothing.
        if (!lines.Any(l => l.TrimStart().StartsWith("PlayEventSounds", StringComparison.OrdinalIgnoreCase))) return false;

        owned.UnionWith(FolderNames(ConfigService.ParseFile(lines).SoundSchemes.Keys));
        foreach (var line in lines)
        {
            foreach (Match match in UserSchemeIdPattern.Matches(line))
            {
                if (SoundCatalog.SchemeFolderName(match.Value) is { } folder) owned.Add(folder);
            }
        }
        return true;
    }

    /// <summary>Removes each folder in <paramref name="candidates"/> that is named like a scheme of the person's own and isn't owned.</summary>
    /// <returns>How many were removed.</returns>
    internal static int RemoveUnowned(string soundsFolder, IEnumerable<string> candidates, HashSet<string> owned)
    {
        int removed = 0, failed = 0;
        foreach (var name in candidates)
        {
            if (!IsUserSchemeFolderName(name) || owned.Contains(name)) continue;
            var dir = Path.Combine(soundsFolder, name);
            try
            {
                if (!Directory.Exists(dir)) continue;
                // Never through a link: what it points at isn't QuickMail's to delete.
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Log("Sound files: could not remove a scheme folder left with no scheme", ex);
                failed++;
            }
        }
        if (removed > 0 || failed > 0)
            LogService.Log($"Sound files: removed {removed} scheme folder(s) left with no scheme" + (failed > 0 ? $"; {failed} could not be removed." : "."));
        return removed;
    }

    /// <summary>Exactly the folder name a scheme of the person's own gets: never a built-in scheme's, or anything else.</summary>
    internal static bool IsUserSchemeFolderName(string name) =>
        SoundCatalog.IsUserSchemeId(name)
        && SoundCatalog.SchemeFolderName(name) is { } folder
        && folder.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folders written down as unsaved; empty if there are none or the list can't be read.</summary>
    internal static HashSet<string> ReadJournal(string soundsFolder)
    {
        var path = Path.Combine(soundsFolder, JournalFileName);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return names;
        foreach (var line in File.ReadAllLines(path))
        {
            var name = line.Trim();
            if (IsUserSchemeFolderName(name)) names.Add(name.ToLowerInvariant());
        }
        return names;
    }

    private static IEnumerable<string> FolderNames(IEnumerable<string> schemeIds) =>
        schemeIds.Where(SoundCatalog.IsUserSchemeId).Select(SoundCatalog.SchemeFolderName).OfType<string>();

    private static bool ChangeJournal(string soundsFolder, Action<HashSet<string>> change)
    {
        try
        {
            lock (JournalGate)
            {
                var journal = ReadJournal(soundsFolder);
                change(journal);
                var path = Path.Combine(soundsFolder, JournalFileName);
                if (journal.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return true;
                }
                Directory.CreateDirectory(soundsFolder);
                Helpers.AtomicFile.WriteAllText(path, string.Join(Environment.NewLine, journal.Order(StringComparer.Ordinal)) + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Sound files: could not update the list of unsaved scheme folders", ex);
            return false;
        }
    }
}
