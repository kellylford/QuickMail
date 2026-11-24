using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Folder rename (#1083): checking a proposed name, and carrying every stored folder reference across
/// a rename that has happened. Pure — the caller loads the models, calls this, and saves what the
/// report says changed — so the whole rewrite is testable without a server, a window or a disk.
///
/// <para>On IMAP a folder's full name is its path, so a rename moves the folder and every folder
/// inside it to new names, and every setting that names one of them has to follow or it silently
/// points at nothing: rules, saved views, the startup folder, the remembered move and copy
/// destinations, hidden folders, favorites and the chosen Archive folder. Microsoft 365 and POP3 name
/// folders by id, which a rename keeps; there only the stored display names of the renamed folder
/// change.</para>
/// </summary>
public static class FolderRenameReferences
{
    /// <summary>The longest name accepted, the same limit the name boxes have.</summary>
    public const int MaxNameLength = 255;

    public sealed class Report
    {
        /// <summary>Rules whose move-to or copy-to folder was rewritten.</summary>
        public List<string> UpdatedRules { get; } = [];
        /// <summary>Saved views a folder reference was rewritten in.</summary>
        public List<string> UpdatedViews { get; } = [];
        /// <summary>The startup folder named the folder or one inside it, and now has its new name.</summary>
        public bool StartupFolderUpdated { get; set; }

        /// <summary>Something in config.ini changed: the startup folder, its label, or a remembered destination.</summary>
        public bool ConfigChanged { get; set; }
        /// <summary>Something in views.json changed, a display name included.</summary>
        public bool ViewsChanged { get; set; }
        /// <summary>Something on the account changed: hidden folders, favorites or the Archive folder.</summary>
        public bool AccountChanged { get; set; }

        /// <summary>
        /// What else the rename changed, as one short sentence, or empty when nothing needed changing.
        /// Only references that would otherwise have broken are counted: a display name that follows
        /// along is not news.
        /// </summary>
        public string Summary()
        {
            var parts = new List<string>();
            if (UpdatedRules.Count > 0) parts.Add(Count(UpdatedRules.Count, "rule", "rules"));
            if (UpdatedViews.Count > 0) parts.Add(Count(UpdatedViews.Count, "saved view", "saved views"));
            if (StartupFolderUpdated)   parts.Add("the startup folder");
            if (parts.Count == 0) return string.Empty;
            var sentence = JoinWithAnd(parts) + " updated.";
            return char.ToUpperInvariant(sentence[0]) + sentence[1..];
        }

        private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    internal static string JoinWithAnd(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    /// <summary>
    /// Rewrites every reference to the renamed folder, and to the folders inside it, in place.
    /// <paramref name="rules"/> may be null when rules.json could not be read; the rest are still
    /// rewritten and the caller says the rules could not be. Only this account's references are
    /// touched: another account's "Projects" is another folder.
    /// </summary>
    public static Report Apply(
        FolderRename rename,
        List<MailRule>? rules,
        List<SavedView> views,
        ConfigModel config,
        AccountModel? account)
    {
        var report = new Report();

        // Rules: the folder they move to and the folder they copy to. Rewritten whether or not the
        // action is switched on, so turning it back on later still finds the folder.
        foreach (var rule in rules?.Where(r => r.AccountId == rename.AccountId) ?? [])
        {
            var changed = false;
            if (rename.Map(rule.TargetFolder) is { } target)     { rule.TargetFolder = target;     changed = true; }
            if (rename.Map(rule.CopyTargetFolder) is { } copyTo) { rule.CopyTargetFolder = copyTo; changed = true; }
            if (changed) report.UpdatedRules.Add(rule.Name);
        }

        // Saved views (real-folder ones; a virtual-folder view names no folder of any account).
        foreach (var view in views.Where(v => string.IsNullOrEmpty(v.VirtualFolderKey)))
        {
            var renamedHere = false;
            foreach (var vf in view.Folders.Where(f => f.AccountId == rename.AccountId))
            {
                var isTheFolder = rename.IsTheFolder(vf.FolderFullName);
                if (rename.Map(vf.FolderFullName) is { } mapped)
                {
                    vf.FolderFullName = mapped;
                    renamedHere = true;
                }
                if (isTheFolder && !string.Equals(vf.FolderDisplayName, rename.NewDisplayName, StringComparison.Ordinal))
                {
                    vf.FolderDisplayName = rename.NewDisplayName;
                    report.ViewsChanged = true;
                }
            }
            if (renamedHere)
            {
                report.UpdatedViews.Add(view.Name);
                report.ViewsChanged = true;
            }
        }

        // The startup folder, when it is a real folder on this account.
        if (Guid.TryParse(config.StartupFolderAccount, out var startupAccount) &&
            startupAccount == rename.AccountId && !string.IsNullOrEmpty(config.StartupFolder))
        {
            var isTheFolder = rename.IsTheFolder(config.StartupFolder);
            if (rename.Map(config.StartupFolder) is { } mapped)
            {
                config.StartupFolder = mapped;
                report.StartupFolderUpdated = true;
                report.ConfigChanged = true;
            }
            if (isTheFolder && !string.Equals(config.StartupFolderLabel, rename.NewDisplayName, StringComparison.Ordinal))
            {
                config.StartupFolderLabel = rename.NewDisplayName;
                report.ConfigChanged = true;
            }
        }

        // Where this account's messages were last moved and copied to (#515).
        if (config.Accounts.TryGetValue(rename.AccountId, out var overrides))
        {
            if (rename.Map(overrides.LastMoveFolder) is { } move) { overrides.LastMoveFolder = move; report.ConfigChanged = true; }
            if (rename.Map(overrides.LastCopyFolder) is { } copy) { overrides.LastCopyFolder = copy; report.ConfigChanged = true; }
        }

        if (account is not null && account.Id == rename.AccountId)
        {
            // Hidden folders (#963): a hidden folder stays hidden under its new name.
            for (int i = 0; i < account.HiddenFolders.Count; i++)
                if (rename.Map(account.HiddenFolders[i]) is { } hidden)
                {
                    account.HiddenFolders[i] = hidden;
                    report.AccountChanged = true;
                }

            // Favorites (#1056), each keeping when it was added and its place.
            foreach (var favorite in account.FavoriteFolders)
                if (rename.Map(favorite.FullName) is { } favored)
                {
                    favorite.FullName = favored;
                    report.AccountChanged = true;
                }

            // The folder chosen with Set as Archive Folder (#318).
            if (rename.Map(account.ArchiveFolderFullName) is { } archive)
            {
                account.ArchiveFolderFullName = archive;
                report.AccountChanged = true;
            }
        }

        return report;
    }

    // ── Checking a proposed name ─────────────────────────────────────────────

    /// <summary>
    /// The IMAP hierarchy separator for <paramref name="folder"/>'s account. A folder inside another
    /// says it in its own path — the character just before its name. A top-level folder's path has
    /// none, so the account's other folders are read the way the folder tree reads them.
    /// </summary>
    public static char ImapSeparator(MailFolderModel folder, IReadOnlyList<MailFolderModel> accountFolders)
    {
        var full = folder.FullName;
        var leaf = folder.DisplayName;
        if (leaf.Length > 0 && full.Length > leaf.Length + 1 && full.EndsWith(leaf, StringComparison.Ordinal))
            return full[full.Length - leaf.Length - 1];
        return FolderTreeBuilder.DetectSeparator(accountFolders);
    }

    /// <summary>
    /// Checks <paramref name="proposed"/> as a new name for <paramref name="folder"/>, and returns the
    /// name to use (trimmed) or the reason it cannot be. Each reason is a sentence the user can act
    /// on. The server has the last word; these catch what can be known without asking it, so a
    /// refusal arrives before anything changes rather than as a server error.
    /// </summary>
    public static (string? Name, string? Error) ValidateName(
        MailFolderModel folder, string? proposed, IReadOnlyList<MailFolderModel> accountFolders, BackendKind backend)
    {
        var name = (proposed ?? string.Empty).Trim();
        if (name.Length == 0)
            return (null, "Type a name for the folder.");
        if (string.Equals(name, folder.DisplayName, StringComparison.Ordinal))
            return (null, $"{folder.DisplayName} already has that name.");
        if (name.Length > MaxNameLength)
            return (null, $"A folder name can be at most {MaxNameLength} characters.");
        if (name.Any(char.IsControl))
            return (null, "A folder name cannot contain control characters.");

        char? separator = backend == BackendKind.ImapSmtp ? ImapSeparator(folder, accountFolders) : null;
        var forbidden = backend switch
        {
            // The separator would make the name a path. '*' and '%' are IMAP's LIST wildcards, which
            // many servers refuse in a name and the rest list unpredictably.
            BackendKind.ImapSmtp => new[] { separator!.Value, '*', '%' },
            // POP3's local folders refuse a slash themselves (Pop3MailService.ValidateName).
            BackendKind.Pop3Smtp => new[] { '/' },
            _ => [],
        };
        foreach (var c in forbidden)
            if (name.Contains(c))
                return (null, $"A folder name on this account cannot contain {c}.");

        if (SiblingNames(folder, accountFolders, separator).Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
            && !string.Equals(name, folder.DisplayName, StringComparison.OrdinalIgnoreCase))
            return (null, $"There is already a folder named {name} there.");

        return (name, null);
    }

    /// <summary>
    /// The names already taken beside <paramref name="folder"/>. Compared without regard to case by
    /// the caller: Exchange and Gmail refuse a second folder that differs only in case, POP3's local
    /// folders do too, and a server that would allow it still leaves two folders a listener cannot
    /// tell apart.
    /// </summary>
    private static IEnumerable<string> SiblingNames(
        MailFolderModel folder, IReadOnlyList<MailFolderModel> accountFolders, char? separator)
    {
        var others = accountFolders.Where(f => !f.IsHeader && !ReferenceEquals(f, folder) &&
                                               !string.Equals(f.FullName, folder.FullName, StringComparison.Ordinal));

        if (separator is not { } sep)
        {
            // Microsoft 365 and POP3 nest by parent id; a top-level POP3 folder has none.
            return others.Where(f => string.Equals(f.ParentId, folder.ParentId, StringComparison.Ordinal))
                         .Select(f => f.DisplayName);
        }

        // IMAP: a sibling shares the path up to the last separator. Its name is read from the path,
        // which is the server's: Inbox's display name is "Inbox", its path "INBOX". A path segment
        // that is not itself a folder ("Lists" in "Lists/Work") takes the name as well.
        var parent = ParentPath(folder.FullName, sep);
        var names = new List<string>();
        foreach (var f in others)
        {
            if (string.Equals(ParentPath(f.FullName, sep), parent, StringComparison.Ordinal))
                names.Add(f.FullName[parent.Length..]);
            if (f.FullName.Length > parent.Length && f.FullName.StartsWith(parent, StringComparison.Ordinal))
            {
                var rest = f.FullName[parent.Length..];
                var cut = rest.IndexOf(sep);
                if (cut > 0) names.Add(rest[..cut]);
            }
        }
        return names;
    }

    /// <summary>The path up to and including the last separator; empty for a top-level folder.</summary>
    private static string ParentPath(string fullName, char separator)
    {
        var cut = fullName.LastIndexOf(separator);
        return cut < 0 ? string.Empty : fullName[..(cut + 1)];
    }
}
