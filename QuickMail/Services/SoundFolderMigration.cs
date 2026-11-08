using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Puts each sound scheme's files in its own folder (#948). QuickMail 0.8.69 kept every custom sound
/// in one shared folder; now a scheme plays only from <c>sounds\&lt;scheme id&gt;</c>, and a rule's own
/// sound from the sounds folder itself. On the first start after upgrading, each file a scheme uses
/// is copied into that scheme's folder, into each of them if more than one scheme uses it, and only
/// then removed from the shared folder. A file a rule uses stays where it is as well, since that is
/// where rules play from, and a file nothing uses is left alone.
/// <para>
/// Safe to stop at any point: a copy is written under a temporary name and renamed into place, a
/// shared file is removed only once every copy of it is in place, and nothing is ever overwritten.
/// A run that didn't finish leaves no marker, so the next start simply does it again; one that did
/// writes <see cref="MarkerFileName"/>, and later starts do nothing. Deleting the marker makes it look
/// again.
/// </para>
/// </summary>
public static class SoundFolderMigration
{
    /// <summary>Written in the sounds folder once every file is in place. Not a sound file name, so never listed or exported.</summary>
    public const string MarkerFileName = "scheme-folders.done";

    /// <summary>What a copy is called until it's complete. Not a sound file name, so a half-written one is never played.</summary>
    internal const string PartialSuffix = ".moving";

    /// <summary>
    /// The move at startup, reading the schemes from config.ini and the rules from rules.json itself.
    /// Not through <see cref="ConfigService.Load"/>, which answers an unreadable file with the
    /// defaults, and so with no schemes: the move would find nothing to do and never run again. If
    /// either file can't be read, nothing is moved, and it is tried again at the next start.
    /// </summary>
    /// <param name="profileDir">The profile folder, holding config.ini and the sounds folder.</param>
    /// <param name="loadRules">Reads the rules; throws if rules.json can't be read.</param>
    public static void RunAtStartup(string profileDir, Func<IEnumerable<MailRule>> loadRules)
    {
        try
        {
            var soundsFolder = Path.Combine(profileDir, SoundCatalog.CustomFolderName);
            if (File.Exists(Path.Combine(soundsFolder, MarkerFileName))) return;
            var configFile = Path.Combine(profileDir, "config.ini");
            var schemes = File.Exists(configFile)
                ? ConfigService.ParseFile(File.ReadAllLines(configFile)).SoundSchemes.Values.ToList()
                : [];
            var ruleSounds = loadRules().Select(r => r.Sound).ToList();
            Run(soundsFolder, schemes, ruleSounds);
        }
        catch (Exception ex)
        {
            // Better to move nothing now than to move a file a rule plays, or to mark it done unseen.
            LogService.Log("Sound files: could not check whether they need moving into scheme folders; will try again next time", ex);
        }
    }

    /// <summary>
    /// Moves the shared files into the schemes' folders, unless that has already been done. Never
    /// throws: a sound file in the wrong place must not stop QuickMail starting.
    /// </summary>
    /// <param name="soundsFolder">The profile's sounds folder.</param>
    /// <param name="schemes">Every scheme in config.ini.</param>
    /// <param name="ruleSounds">The stored sound of every rule.</param>
    public static void Run(string soundsFolder, IEnumerable<SoundScheme> schemes, IEnumerable<string?> ruleSounds)
    {
        try
        {
            var marker = Path.Combine(soundsFolder, MarkerFileName);
            if (File.Exists(marker)) return;
            if (Directory.Exists(soundsFolder) && !Place(soundsFolder, schemes, ruleSounds, removeFromShared: true))
                return;   // something couldn't be done this time; tried again at the next start

            Directory.CreateDirectory(soundsFolder);
            File.WriteAllText(marker,
                "QuickMail has put each sound scheme's files in its own folder here. Delete this file to have it look again at the next start." + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Sound files: could not finish putting them in their schemes' folders; will try again next time", ex);
        }
    }

    /// <summary>
    /// Copies each file in the sounds folder that a scheme uses into that scheme's folder, and with
    /// <paramref name="removeFromShared"/>, then removes it from the sounds folder if every copy is in
    /// place and no rule uses it. Running it again changes nothing.
    /// </summary>
    /// <param name="onlyNames">If given, only these files are looked at: what a settings file just brought in.</param>
    /// <returns>True if everything that should have been done was.</returns>
    internal static bool Place(string soundsFolder, IEnumerable<SoundScheme> schemes, IEnumerable<string?> ruleSounds, bool removeFromShared,
        IReadOnlyCollection<string>? onlyNames = null)
    {
        var only = onlyNames is null ? null : new HashSet<string>(onlyNames, StringComparer.OrdinalIgnoreCase);
        // Each file name a scheme uses, and the folders of the schemes that use it. Windows treats
        // names differing only in case as one file, and so does this.
        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in schemes)
        {
            if (SoundCatalog.SchemeFolderName(scheme.Id) is not { } folder) continue;   // from a newer QuickMail, or unsafe
            foreach (var value in scheme.Events.Values)
            {
                if (!SoundRef.TryParse(value, out var sound) || sound.Kind != SoundRefKind.Custom) continue;
                if (only is not null && !only.Contains(sound.Name)) continue;
                if (!wanted.TryGetValue(sound.Name, out var folders))
                    wanted[sound.Name] = folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                folders.Add(folder);
            }
        }

        var forRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in ruleSounds)
        {
            if (SoundRef.TryParse(value, out var sound) && sound.Kind == SoundRefKind.Custom) forRules.Add(sound.Name);
        }

        bool complete = true;
        int copies = 0, moved = 0, kept = 0;
        foreach (var (name, folders) in wanted)
        {
            var shared = Path.Combine(soundsFolder, name);
            if (!File.Exists(shared)) continue;   // already moved, or never there

            var everyCopyInPlace = true;
            foreach (var folder in folders.Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    switch (EnsureCopy(shared, Path.Combine(soundsFolder, folder), name))
                    {
                        case CopyResult.Copied:
                            copies++;
                            break;
                        case CopyResult.Conflict:
                            // The scheme already has a different file of that name. Its own stays,
                            // and so does the shared one, so neither sound is lost.
                            everyCopyInPlace = false;
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogService.Log("Sound files: could not copy one into its scheme's folder", ex);
                    everyCopyInPlace = false;
                    complete = false;
                }
            }

            if (!removeFromShared) continue;
            if (!everyCopyInPlace || forRules.Contains(name))
            {
                kept++;
                continue;
            }
            try
            {
                File.Delete(shared);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Log("Sound files: could not remove one from the shared folder after copying it", ex);
                complete = false;
            }
        }

        if (copies > 0 || moved > 0 || kept > 0)
            LogService.Log($"Sound files: {copies} copied into scheme folders, {moved} removed from the shared folder, {kept} kept there as well.");
        return complete;
    }

    private enum CopyResult { Copied, AlreadyThere, Conflict }

    /// <summary>Makes sure a scheme's folder has a copy of a file, writing it under a temporary name first.</summary>
    private static CopyResult EnsureCopy(string source, string folder, string name)
    {
        var target = Path.Combine(folder, name);
        if (Directory.Exists(target)) return CopyResult.Conflict;
        if (File.Exists(target)) return SameBytes(source, target) ? CopyResult.AlreadyThere : CopyResult.Conflict;

        Directory.CreateDirectory(folder);
        var partial = target + PartialSuffix;
        if (File.Exists(partial)) File.Delete(partial);   // left by a run that stopped midway
        File.Copy(source, partial);
        File.Move(partial, target);   // never over anything: throws if something has taken the name since
        return CopyResult.Copied;
    }

    private static bool SameBytes(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
}
