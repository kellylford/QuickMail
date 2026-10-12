using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// After a settings import, points each imported account's folder references at the folders it
/// really has here (issue #507, spec Revision 2 R5).
///
/// <para>An IMAP rule names its folder by path, which is the same on every computer. A Microsoft
/// 365 (Graph) rule names it by an opaque id, and <see cref="FolderReferenceRemapper"/> can only
/// match by name. So the settings file carries each folder's display path beside its id; when an
/// account's folders first load after the import, a reference that does not resolve is turned
/// back into its path and handed to the remapper, which matches it or turns the rule off and
/// names it — never a guess. Pending accounts are kept in <see cref="FileName"/> until healed.</para>
/// </summary>
public static class ImportFolderHeal
{
    public const string FileName = "import-folders.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Records the folder paths from a settings file (ids already rewritten) for the accounts here.</summary>
    internal static void Save(string profileDir, string foldersJson, IReadOnlySet<Guid> accountIds)
    {
        var all = Parse(foldersJson);
        var pending = all.Where(kv => Guid.TryParse(kv.Key, out var id) && accountIds.Contains(id))
                         .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(profileDir, FileName);
        if (pending.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(pending, Indented));
    }

    /// <summary>
    /// Removes and returns the folder paths waiting for <paramref name="accountId"/>, or null when it
    /// has none. Removing first means a heal is attempted once, not at every folder refresh.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Take(string profileDir, Guid accountId)
    {
        var path = Path.Combine(profileDir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var all = Parse(File.ReadAllText(path));
            var key = all.Keys.FirstOrDefault(k => Guid.TryParse(k, out var id) && id == accountId);
            if (key is null) return null;
            var paths = all[key];
            all.Remove(key);
            if (all.Count == 0) File.Delete(path);
            else AtomicFile.WriteAllText(path, JsonSerializer.Serialize(all, Indented));
            return paths;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LogService.Log("Settings import: could not read the folder names waiting to be matched", ex);
            return null;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
        ?? new Dictionary<string, Dictionary<string, string>>();

    /// <summary>
    /// Heals one account's references against the folders it has here. Mutates the models in place;
    /// the caller saves them. <paramref name="archiveChanged"/> reports a change to the account's
    /// archive folder, which lives in accounts.json rather than the files the report covers.
    /// </summary>
    public static FolderReferenceRemapper.Report Heal(
        Guid accountId,
        IReadOnlyList<MailFolderModel> folders,
        IReadOnlyDictionary<string, string> paths,
        List<MailRule> rules,
        List<SavedView> views,
        ConfigModel config,
        AccountModel? account,
        out bool archiveChanged)
    {
        var known = folders.Where(f => !f.IsHeader).Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);

        // A reference that resolves here is left alone; one that does not becomes the path it had
        // on the other computer, which the remapper matches by name.
        string? Translate(string? reference) =>
            string.IsNullOrEmpty(reference) || known.Contains(reference) || !paths.TryGetValue(reference, out var p)
                ? reference
                : p;

        foreach (var rule in rules.Where(r => r.AccountId == accountId))
        {
            rule.TargetFolder = Translate(rule.TargetFolder);
            rule.CopyTargetFolder = Translate(rule.CopyTargetFolder);
        }
        foreach (var vf in views.SelectMany(v => v.Folders).Where(f => f.AccountId == accountId))
            vf.FolderFullName = Translate(vf.FolderFullName) ?? string.Empty;
        if (Guid.TryParse(config.StartupFolderAccount, out var startup) && startup == accountId)
            config.StartupFolder = Translate(config.StartupFolder) ?? string.Empty;

        archiveChanged = false;
        if (account?.ArchiveFolderFullName is { Length: > 0 } archive && !known.Contains(archive))
        {
            account.ArchiveFolderFullName = MatchArchive(Translate(archive)!, folders);
            archiveChanged = true;
        }

        return FolderReferenceRemapper.Remap(accountId, folders, rules, views, config);
    }

    // The remapper's matching for the one reference it does not cover: the whole name, else a leaf
    // name only when it is unique. No match clears the setting, so archiving falls back to the
    // account's default rather than filing into a folder that is not there.
    private static string? MatchArchive(string reference, IReadOnlyList<MailFolderModel> folders)
    {
        var real = folders.Where(f => !f.IsHeader).ToList();
        if (real.Any(f => f.FullName == reference)) return reference;
        var exact = real.Where(f => string.Equals(f.DisplayName, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].FullName;
        var slash = reference.LastIndexOf('/');
        var leaf = slash >= 0 ? reference[(slash + 1)..] : reference;
        var byLeaf = real.Where(f => string.Equals(f.DisplayName, leaf, StringComparison.OrdinalIgnoreCase)).ToList();
        return byLeaf.Count == 1 ? byLeaf[0].FullName : null;
    }
}
