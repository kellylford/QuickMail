using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Removes scheme folders that no scheme owns any more (#955). A scheme's folder can be left behind
/// with nothing in config.ini naming it: QuickMail stopping while Settings had a new, copied or
/// imported scheme open, or a settings file bringing sounds without the preferences that use them.
/// Settings export carries every scheme folder, so a forgotten one counts against its size limit.
/// <para>
/// Deliberately timid. It looks only at folders named exactly like the folder of a scheme of the
/// person's own (<c>user-</c> and an id), never at a built-in scheme's folder, never at a file in the
/// sounds folder itself (rules' sounds), and never through a link. A folder is kept if config.ini
/// mentions its scheme anywhere, if a settings-import backup's config.ini does (so undoing an import
/// by hand still finds the replaced schemes' sounds), or if the caller says a scheme still uses it,
/// such as one open in Settings. If config.ini, or any backup's, can't be read, or doesn't look like
/// one written by a QuickMail with sound schemes, nothing is removed at all. The log gives counts
/// only.
/// </para>
/// </summary>
public static class SoundSchemeFolderCleanup
{
    // Any scheme id of the person's own written anywhere in a config.ini: a section header, the
    // scheme in use, or something a newer QuickMail wrote. Counted as owning its folder.
    private static readonly Regex UserSchemeIdPattern = new(
        Regex.Escape(SoundCatalog.UserSchemePrefix) + "[A-Za-z0-9-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>At startup, after any settings import and the one-time move: only what config.ini and its backups own is kept.</summary>
    public static void RunAtStartup(string profileDir) => Run(profileDir, []);

    /// <summary>
    /// Removes every scheme folder of the person's own that nothing owns. Never throws.
    /// </summary>
    /// <param name="profileDir">The profile folder, holding config.ini, the sounds folder and any import backups.</param>
    /// <param name="alsoKeep">Schemes that own their folders whatever config.ini says, such as those open in Settings.</param>
    /// <returns>How many folders were removed, or null if it couldn't be sure what is owned and so removed nothing.</returns>
    public static int? Run(string profileDir, IEnumerable<string> alsoKeep)
    {
        try
        {
            var sounds = Path.Combine(profileDir, SoundCatalog.CustomFolderName);
            if (!Directory.Exists(sounds)) return 0;
            if (OwnedFolders(profileDir) is not { } owned) return null;
            foreach (var id in alsoKeep)
            {
                if (SoundCatalog.SchemeFolderName(id) is { } folder) owned.Add(folder);
            }
            return RemoveUnowned(sounds, owned);
        }
        catch (Exception ex)
        {
            LogService.Log("Sound files: could not check for scheme folders no scheme uses; nothing was removed", ex);
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

        foreach (var id in ConfigService.ParseFile(lines).SoundSchemes.Keys)
        {
            if (SoundCatalog.SchemeFolderName(id) is { } folder) owned.Add(folder);
        }
        foreach (var line in lines)
        {
            foreach (Match match in UserSchemeIdPattern.Matches(line))
            {
                if (SoundCatalog.SchemeFolderName(match.Value) is { } folder) owned.Add(folder);
            }
        }
        return true;
    }

    /// <summary>Removes each folder named like a scheme of the person's own that isn't in <paramref name="owned"/>.</summary>
    /// <returns>How many were removed.</returns>
    internal static int RemoveUnowned(string soundsFolder, HashSet<string> owned)
    {
        int removed = 0, failed = 0;
        foreach (var dir in Directory.GetDirectories(soundsFolder))
        {
            var name = Path.GetFileName(dir);
            if (!IsUserSchemeFolderName(name) || owned.Contains(name)) continue;
            try
            {
                // Never through a link: what it points at isn't QuickMail's to delete.
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Log("Sound files: could not remove a scheme folder no scheme uses", ex);
                failed++;
            }
        }
        if (removed > 0 || failed > 0)
            LogService.Log($"Sound files: removed {removed} scheme folder(s) no scheme uses" + (failed > 0 ? $"; {failed} could not be removed." : "."));
        return removed;
    }

    /// <summary>Exactly the folder name a scheme of the person's own gets: never a built-in scheme's, or anything else.</summary>
    internal static bool IsUserSchemeFolderName(string name) =>
        SoundCatalog.IsUserSchemeId(name)
        && SoundCatalog.SchemeFolderName(name) is { } folder
        && folder.Equals(name, StringComparison.OrdinalIgnoreCase);
}
