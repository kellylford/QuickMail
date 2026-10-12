using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>What importing a settings file does to one of its accounts (issue #507, spec R2).</summary>
public enum ImportAccountOutcome
{
    /// <summary>Matches an account here, which keeps its id and sign-in and takes the file's settings.</summary>
    Matched,
    /// <summary>New here: added with its own id.</summary>
    Added,
    /// <summary>Not here, and the file does not carry accounts, so it cannot be added.</summary>
    NotInFile,
    /// <summary>Left out: an ambiguous match, or a shared mailbox whose account is missing.</summary>
    Skipped,
}

/// <summary>One account in an import preview. <see cref="ToString"/> is the list item's spoken name.</summary>
public sealed class ImportAccountRow
{
    public required SettingsBundleAccount Source { get; init; }
    public required ImportAccountOutcome Outcome { get; init; }
    public Guid? LocalId { get; init; }
    public string Reason { get; init; } = string.Empty;

    public string Label => string.IsNullOrWhiteSpace(Source.DisplayName) ? Source.Username : Source.DisplayName;

    /// <summary>The list item's accessible name, set on the item container (CLAUDE.md).</summary>
    public string AccessibleName => ToString();

    public override string ToString() => Outcome switch
    {
        ImportAccountOutcome.Matched   => $"{Label}, already on this computer, settings will be updated",
        ImportAccountOutcome.Added     => Reason.Length > 0 ? $"{Label}, will be added; {Reason}" : $"{Label}, will be added",
        ImportAccountOutcome.NotInFile => $"{Label}, not on this computer and not in the file",
        _                              => $"{Label}, skipped: {Reason}",
    };
}

/// <summary>What an import will do, worked out without writing anything. Shown as the preview.</summary>
public sealed class SettingsImportPlan
{
    public required SettingsBundleManifest Manifest { get; init; }
    public required IReadOnlyList<ImportAccountRow> Accounts { get; init; }

    /// <summary>Source account id to the id it has on this computer, for every account that will be here.</summary>
    public required IReadOnlyDictionary<Guid, Guid> IdMap { get; init; }

    /// <summary>One line per category the file carries, e.g. "Rules, saved views, flags and watched conversations: 4 rules, 3 saved views".</summary>
    public required IReadOnlyList<string> CategoryLines { get; init; }

    public string Summary
    {
        get
        {
            var from = string.IsNullOrWhiteSpace(Manifest.DeviceName) ? "another computer" : Manifest.DeviceName;
            var when = Manifest.ExportedUtc == default
                ? string.Empty
                : ", saved " + Manifest.ExportedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
            var added = Accounts.Count(a => a.Outcome == ImportAccountOutcome.Added);
            var matched = Accounts.Count(a => a.Outcome == ImportAccountOutcome.Matched);
            var accounts = Manifest.Categories.Contains(SettingsCategory.Accounts)
                ? $" {Plural(added, "account")} will be added and {Plural(matched, "account")} updated; no account is removed."
                : string.Empty;
            return $"Settings from {from}{when}.{accounts} Importing replaces the other settings listed with the ones in the file. "
                 + "A copy of your current settings is kept, and QuickMail restarts to finish.";
        }
    }

    internal static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}

/// <summary>How an import went, written by the restart that applied it and read by the Settings Imported window.</summary>
public sealed class SettingsImportResult
{
    public bool Succeeded { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string BackupFolder { get; set; } = string.Empty;
    public List<string> Lines { get; set; } = [];

    /// <summary>Accounts new to this computer: these have no saved password or sign-in here yet.</summary>
    public List<Guid> AccountsNeedingSignIn { get; set; } = [];

    /// <summary>Replaces the usual first line, for a result that is only the Local Calendar's, recorded at a later start.</summary>
    public string Heading { get; set; } = string.Empty;

    /// <summary>The window title: what happened, so it is right before the report is read.</summary>
    public string Title => !Succeeded ? "Settings Not Imported" : Heading.Length > 0 ? "Local Calendar Imported" : "Settings Imported";

    public string Report =>
        string.Join(Environment.NewLine, Lines.Prepend(
            Heading.Length > 0 ? Heading
            : Succeeded ? $"Settings imported from {(string.IsNullOrWhiteSpace(DeviceName) ? "another computer" : DeviceName)}."
            : "Settings were not imported."));
}

/// <summary>An import that failed part-way; the profile has been put back from <see cref="BackupFolder"/>.</summary>
public sealed class SettingsImportFailedException(string backupFolder, IReadOnlyList<string> notRestored, Exception inner)
    : Exception(Describe(backupFolder, notRestored, inner), inner)
{
    public string BackupFolder { get; } = backupFolder;

    /// <summary>Files the restore could not put back; empty when everything was restored.</summary>
    public IReadOnlyList<string> NotRestored { get; } = notRestored;

    private static string Describe(string backupFolder, IReadOnlyList<string> notRestored, Exception inner) =>
        notRestored.Count == 0
            ? $"The import failed and your previous settings were restored from {backupFolder}. {inner.Message}"
            : $"The import failed, and these could not be put back: {string.Join(", ", notRestored)}. "
              + $"Copy them back by hand from the folder {backupFolder} in your QuickMail data folder, with QuickMail closed. {inner.Message}";
}

/// <summary>
/// Applies a settings file to a profile directory (issue #507, spec Revision 2). Runs at startup,
/// before any service has loaded a file (R1); the running app only ever stages a file with
/// <see cref="Stage"/> and restarts. Every write is preceded by a copy of the files it may touch
/// (D6), and a failure part-way restores that copy.
/// </summary>
public static class SettingsImporter
{
    public const string PendingFileName = "pending-import" + SettingsBundle.FileExtension;
    public const string ResultFileName = "import-result.json";
    public const string PendingCalendarFileName = "pending-calendar.ics";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The profile files an import can change, which is what the rollback copy holds.</summary>
    internal static readonly string[] ProfileFiles =
        SettingsBundle.CategoryFiles.Values.SelectMany(f => f)
            .Where(f => f is not SettingsBundle.GroupMembersName and not SettingsBundle.CalendarName)
            .Append(ImportFolderHeal.FileName)
            .Append(PendingCalendarFileName)
            .ToArray();

    // ── Planning ─────────────────────────────────────────────────────────────────

    public static SettingsImportPlan Plan(SettingsBundle bundle, string profileDir)
    {
        ValidateEntries(bundle);
        var local = SettingsBundle.ReadList<AccountModel>(Path.Combine(profileDir, "accounts.json"));
        var carriesAccounts = bundle.Has(SettingsCategory.Accounts);
        var incoming = carriesAccounts ? DeserializeList<AccountModel>(bundle.GetText("accounts.json")) : [];
        var incomingIds = incoming.Select(a => a.Id).ToHashSet();

        var rows = new List<ImportAccountRow>();
        var map = new Dictionary<Guid, Guid>();
        var claimed = new HashSet<Guid>();
        var takenIds = local.Select(a => a.Id).ToHashSet();

        // Shared mailboxes last, so their parent's outcome is known when they are reached.
        foreach (var src in bundle.Manifest.Accounts.OrderBy(a => a.IsShared))
        {
            // A shared mailbox reads through its parent: with the parent left out, it cannot work here.
            if (src.IsShared && carriesAccounts
                && incoming.FirstOrDefault(a => a.Id == src.Id)?.ParentAccountId is var parent
                && (parent is null || !map.ContainsKey(parent.Value)))
            {
                rows.Add(new ImportAccountRow { Source = src, Outcome = ImportAccountOutcome.Skipped,
                    Reason = "the account it belongs to is not being imported" });
                continue;
            }

            var sameId = local.FirstOrDefault(l => l.Id == src.Id && SameIdentity(l, src));
            var candidates = sameId is not null && !claimed.Contains(sameId.Id)
                ? [sameId]
                : local.Where(l => !claimed.Contains(l.Id) && SameIdentity(l, src)).ToList();

            if (candidates.Count == 1)
            {
                claimed.Add(candidates[0].Id);
                map[src.Id] = candidates[0].Id;
                rows.Add(new ImportAccountRow { Source = src, Outcome = ImportAccountOutcome.Matched, LocalId = candidates[0].Id });
                continue;
            }
            if (candidates.Count > 1)
            {
                // Never guess (Principle 3): which one the rules meant is not knowable.
                rows.Add(new ImportAccountRow { Source = src, Outcome = ImportAccountOutcome.Skipped,
                    Reason = "it matches more than one account on this computer" });
                continue;
            }
            if (!carriesAccounts || !incomingIds.Contains(src.Id))
            {
                rows.Add(new ImportAccountRow { Source = src, Outcome = ImportAccountOutcome.NotInFile });
                continue;
            }

            // Keep the source id unless something here already has it.
            var id = takenIds.Contains(src.Id) ? Guid.NewGuid() : src.Id;
            takenIds.Add(id);
            map[src.Id] = id;
            // The same address as another kind of account here (one converted to Microsoft 365 on one
            // computer and not the other): added as a separate account, and the preview says so.
            var otherKind = local.Any(l => l.IsShared == src.IsShared
                && string.Equals(l.Username.Trim(), src.Username.Trim(), StringComparison.OrdinalIgnoreCase));
            rows.Add(new ImportAccountRow { Source = src, Outcome = ImportAccountOutcome.Added, LocalId = id,
                Reason = otherKind ? "this computer also has this address as a different kind of account" : string.Empty });
        }

        return new SettingsImportPlan
        {
            Manifest = bundle.Manifest,
            Accounts = rows,
            IdMap = map,
            CategoryLines = DescribeCategories(bundle, profileDir),
        };
    }

    private static bool SameIdentity(AccountModel local, SettingsBundleAccount src) =>
        local.BackendKind == src.BackendKind
        && local.IsShared == src.IsShared
        && string.Equals(local.Username.Trim(), src.Username.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every JSON file in the bundle must parse, so a damaged file is refused at the preview rather
    /// than found at startup, part-way through, or written out for a service to discard.
    /// </summary>
    private static void ValidateEntries(SettingsBundle bundle)
    {
        foreach (var name in bundle.Entries.Keys)
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            // As text, which drops a byte-order mark: QuickMail writes hotkeys.json with one.
            try { using var doc = JsonDocument.Parse(bundle.GetText(name) ?? string.Empty); }
            catch (JsonException ex)
            {
                throw new SettingsBundleException($"This settings file is damaged: {name} could not be read.", ex);
            }
        }
    }

    private static List<string> DescribeCategories(SettingsBundle bundle, string profileDir)
    {
        // What each line replaces here, so the preview says what will go as well as what will come.
        int Here(string name) => CountJsonArray(ReadIfExists(Path.Combine(profileDir, name)));
        int LocalContactsHere() => SettingsBundle.ReadList<ContactModel>(Path.Combine(profileDir, "contacts.json")).Count(c => c.IsLocal);
        int ThemesHere() => Directory.Exists(Path.Combine(profileDir, "themes")) ? Directory.GetFiles(Path.Combine(profileDir, "themes"), "*.json").Length : 0;

        var lines = new List<string>();
        foreach (var category in bundle.Manifest.Categories)
        {
            var name = SettingsCategoryInfo.NameOf(category);
            string detail = category switch
            {
                SettingsCategory.Accounts =>
                    SettingsImportPlan.Plural(DeserializeList<AccountModel>(bundle.GetText("accounts.json")).Count, "account")
                    + ", none removed",
                SettingsCategory.RulesAndViews =>
                    $"{SettingsImportPlan.Plural(CountJsonArray(bundle.GetText("rules.json")), "rule")} and "
                    + $"{SettingsImportPlan.Plural(CountJsonArray(bundle.GetText("views.json")), "saved view")}, "
                    + $"replacing the {Here("rules.json")} and {Here("views.json")} here",
                SettingsCategory.AddressBook =>
                    $"{SettingsImportPlan.Plural(CountJsonArray(bundle.GetText("contacts.json")), "contact")} and "
                    + $"{SettingsImportPlan.Plural(CountJsonArray(bundle.GetText("groups.json")), "group")}, "
                    + $"replacing the {LocalContactsHere()} and {Here("groups.json")} here; synced contacts are kept",
                SettingsCategory.Templates =>
                    $"{SettingsImportPlan.Plural(CountJsonArray(bundle.GetText("templates.json")), "template")}, replacing the {Here("templates.json")} here",
                SettingsCategory.Keyboard =>
                    $"{SettingsImportPlan.Plural(CountJsonArray(bundle.GetText("hotkeys.json")), "customized shortcut")}, replacing the {Here("hotkeys.json")} here",
                SettingsCategory.Appearance =>
                    $"{SettingsImportPlan.Plural(bundle.ThemeEntries.Count(), "custom theme")}, replacing the {ThemesHere()} here",
                SettingsCategory.LocalCalendar =>
                    SettingsImportPlan.Plural(CountEvents(bundle.GetText(SettingsBundle.CalendarName)), "appointment")
                    + ", added or updated, never removed",
                _ => "replaced, apart from settings that belong to this computer",
            };
            lines.Add($"{name}: {detail}");
        }
        return lines;
    }

    private static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static int CountJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static int CountEvents(string? ics)
    {
        if (string.IsNullOrEmpty(ics)) return 0;
        var count = 0;
        var at = 0;
        while ((at = ics.IndexOf("BEGIN:VEVENT", at, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; at++; }
        return count;
    }

    // ── Staging and the startup apply ────────────────────────────────────────────

    /// <summary>
    /// Copies a checked settings file into the profile, for the next start to apply. The running app
    /// calls this and then restarts; it never writes the profile files itself (R1).
    /// </summary>
    public static void Stage(string bundlePath, string profileDir) => Stage(File.ReadAllBytes(bundlePath), profileDir);

    /// <summary>As <see cref="Stage(string, string)"/>, from the bytes that were checked and previewed.</summary>
    public static void Stage(byte[] bundle, string profileDir) =>
        File.WriteAllBytes(Path.Combine(profileDir, PendingFileName), bundle);

    /// <summary>
    /// At startup, before any service reads a file: applies a staged settings file, if there is one,
    /// and records how it went in <see cref="ResultFileName"/>. The staged file is removed whatever
    /// happens, so a file that cannot be applied is never retried at every launch.
    /// </summary>
    public static SettingsImportResult? ApplyPendingAtStartup(string profileDir, DateTime now)
    {
        var pending = Path.Combine(profileDir, PendingFileName);
        if (!File.Exists(pending)) return null;

        SettingsImportResult result;
        try
        {
            result = Apply(SettingsBundle.Open(pending), profileDir, now);
        }
        catch (SettingsBundleException ex)
        {
            result = Failure(ex.Message);
        }
        catch (SettingsImportFailedException ex)
        {
            LogService.Log("Settings import failed; previous settings restored", ex.InnerException ?? ex);
            result = Failure(ex.Message);
            result.BackupFolder = ex.BackupFolder;
        }
        catch (Exception ex)
        {
            LogService.Log("Settings import failed", ex);
            result = Failure("The import could not be completed. " + ex.Message);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception ex) { LogService.Log("Settings import: could not remove the staged file", ex); }
        }

        try { AtomicFile.WriteAllText(Path.Combine(profileDir, ResultFileName), JsonSerializer.Serialize(result, Indented)); }
        catch (Exception ex) { LogService.Log("Settings import: could not record the result", ex); }
        return result;
    }

    private static SettingsImportResult Failure(string message) =>
        new() { Succeeded = false, Lines = [message] };

    /// <summary>Reads and removes the result an earlier start recorded, or null when there is none.</summary>
    public static SettingsImportResult? TakeResult(string profileDir)
    {
        var path = Path.Combine(profileDir, ResultFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SettingsImportResult>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogService.Log("Settings import: the recorded result could not be read", ex);
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Applies a settings file to <paramref name="profileDir"/>. Throws
    /// <see cref="SettingsImportFailedException"/> after restoring the profile when any step fails.
    /// </summary>
    public static SettingsImportResult Apply(SettingsBundle bundle, string profileDir, DateTime now)
    {
        SettingsImportPlan plan;
        try
        {
            plan = Plan(bundle, profileDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SettingsBundleException(
                $"Your current settings could not be read before importing, so nothing was changed. {ex.Message}", ex);
        }
        string backup;
        try
        {
            backup = CreateBackup(profileDir, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing has been written yet, so nothing needs restoring: say so plainly.
            throw new SettingsBundleException(
                $"Your current settings could not be copied before importing, so nothing was changed. {ex.Message}", ex);
        }
        var backupName = Path.GetFileName(backup);
        try
        {
            var result = ApplyCore(bundle, plan, profileDir);
            result.BackupFolder = backupName;
            result.Lines.Add($"A copy of your previous settings is in the folder {backupName} in your QuickMail data folder.");
            return result;
        }
        catch (Exception ex)
        {
            var notRestored = Restore(profileDir, backup);
            throw new SettingsImportFailedException(backupName, notRestored, ex);
        }
    }

    private static SettingsImportResult ApplyCore(SettingsBundle bundle, SettingsImportPlan plan, string profileDir)
    {
        var map = plan.IdMap;
        string Rewrite(string text) => AccountIdRewriter.Rewrite(text, map);
        var result = new SettingsImportResult { Succeeded = true, DeviceName = plan.Manifest.DeviceName };

        // ── Accounts: matched, never deleted ──
        var accountsPath = Path.Combine(profileDir, "accounts.json");
        var accounts = SettingsBundle.ReadList<AccountModel>(accountsPath);
        if (bundle.Has(SettingsCategory.Accounts))
        {
            var incoming = DeserializeList<AccountModel>(Rewrite(bundle.GetText("accounts.json") ?? string.Empty));
            var applied = new List<AccountModel>();
            foreach (var row in plan.Accounts.Where(r => r.Outcome is ImportAccountOutcome.Matched or ImportAccountOutcome.Added))
            {
                var account = incoming.FirstOrDefault(a => a.Id == row.LocalId);
                if (account is null) continue;   // matched by the manifest but not in accounts.json: nothing to update
                var at = accounts.FindIndex(a => a.Id == account.Id);
                if (at >= 0)
                {
                    // A conversion in progress is this computer's state, not a setting.
                    account.GraphConversionPending = accounts[at].GraphConversionPending;
                    // A different way of signing in than the one saved here means the saved one no longer applies.
                    if (!account.IsShared && account.AuthType != accounts[at].AuthType)
                        result.AccountsNeedingSignIn.Add(account.Id);
                    accounts[at] = account;
                }
                else
                {
                    account.GraphConversionPending = false;   // the other computer's conversion, not one here
                    accounts.Add(account);
                    if (!account.IsShared) result.AccountsNeedingSignIn.Add(account.Id);
                }
                applied.Add(account);
            }
            if (applied.Any(a => a.IsDefault))
                foreach (var other in accounts.Where(a => !applied.Contains(a)))
                    other.IsDefault = false;
            WriteJson(accountsPath, accounts);

            var added = plan.Accounts.Count(r => r.Outcome == ImportAccountOutcome.Added);
            var matched = plan.Accounts.Count(r => r.Outcome == ImportAccountOutcome.Matched);
            result.Lines.Add($"Accounts: {SettingsImportPlan.Plural(added, "account")} added, {SettingsImportPlan.Plural(matched, "account")} updated.");
        }
        foreach (var row in plan.Accounts.Where(r => r.Outcome == ImportAccountOutcome.Skipped))
            result.Lines.Add($"{row.Label} was not imported: {row.Reason}.");

        var accountIds = accounts.Select(a => a.Id).ToHashSet();
        var carriedIds = plan.IdMap.Values.Where(accountIds.Contains).ToHashSet();

        foreach (var category in plan.Manifest.Categories)
        {
            switch (category)
            {
                case SettingsCategory.RulesAndViews:
                    foreach (var name in SettingsBundle.CategoryFiles[category])
                        ReplaceText(bundle, profileDir, name, Rewrite);
                    DisableOrphanedRules(profileDir, accountIds, result);
                    DropOrphanedViewFolders(profileDir, accountIds, result);
                    result.Lines.Add(Done(plan, category));
                    break;
                case SettingsCategory.Templates:
                case SettingsCategory.Keyboard:
                    foreach (var name in SettingsBundle.CategoryFiles[category])
                        ReplaceText(bundle, profileDir, name, Rewrite);
                    result.Lines.Add(Done(plan, category));
                    break;
                case SettingsCategory.Appearance:
                    ReplaceText(bundle, profileDir, "rowlayout.json", Rewrite);
                    ReplaceThemes(bundle, profileDir);
                    result.Lines.Add(Done(plan, category));
                    break;
                case SettingsCategory.AddressBook:
                    ApplyAddressBook(bundle, profileDir, result);
                    break;
                case SettingsCategory.Preferences:
                    ApplyPreferences(bundle, profileDir, Rewrite, accountIds, carriedIds);
                    ReplaceBytes(bundle, profileDir, "custom.lex");
                    result.Lines.Add("Preferences and custom dictionary: updated. Settings that belong to this computer were kept.");
                    break;
                case SettingsCategory.LocalCalendar:
                    // mail.db is not open yet; the calendar is applied once it is (ApplyPendingCalendarAsync).
                    var ics = bundle.Get(SettingsBundle.CalendarName);
                    if (ics is not null)
                        File.WriteAllBytes(Path.Combine(profileDir, PendingCalendarFileName), ics);
                    break;
            }
        }

        // Folder names for the heal that runs when each account's folders first load (spec R5).
        var folders = bundle.GetText(SettingsBundle.FoldersName);
        if (folders is not null)
            ImportFolderHeal.Save(profileDir, Rewrite(folders),
                accounts.Where(a => a.BackendKind == BackendKind.MicrosoftGraph).Select(a => a.Id).ToHashSet());   // only Graph ids differ between computers

        result.Lines.RemoveAll(string.IsNullOrEmpty);
        return result;
    }

    // ── Category appliers ────────────────────────────────────────────────────────

    // The preview line for a category, said as done: "in place of the 3 here", not "replacing".
    private static string Done(SettingsImportPlan plan, SettingsCategory category) =>
        (plan.CategoryLines.FirstOrDefault(l => l.StartsWith(SettingsCategoryInfo.NameOf(category), StringComparison.Ordinal)) ?? string.Empty)
            .Replace(", replacing the ", ", in place of the ", StringComparison.Ordinal);

    /// <summary>Makes the profile's file match the bundle's: written when the bundle has it, removed when it does not.</summary>
    private static void ReplaceText(SettingsBundle bundle, string profileDir, string name, Func<string, string> rewrite)
    {
        var path = Path.Combine(profileDir, name);
        var text = bundle.GetText(name);
        if (text is null) { if (File.Exists(path)) File.Delete(path); return; }
        AtomicFile.WriteAllText(path, rewrite(text), new UTF8Encoding(false));
    }

    /// <summary>As <see cref="ReplaceText"/>, byte for byte: custom.lex is UTF-16 with a BOM and must stay so.</summary>
    private static void ReplaceBytes(SettingsBundle bundle, string profileDir, string name)
    {
        var path = Path.Combine(profileDir, name);
        var bytes = bundle.Get(name);
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); return; }
        File.WriteAllBytes(path, bytes);
    }

    private static void ReplaceThemes(SettingsBundle bundle, string profileDir)
    {
        var dir = Path.Combine(profileDir, "themes");
        if (Directory.Exists(dir))
            foreach (var file in Directory.GetFiles(dir, "*.json"))
                File.Delete(file);
        var themes = bundle.ThemeEntries.ToList();
        if (themes.Count == 0) return;
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in themes)
            File.WriteAllBytes(Path.Combine(dir, name[SettingsBundle.ThemesPrefix.Length..]), bytes);
    }

    // A rule naming an account that is not here would act on nothing; turned off and named, never deleted.
    private static void DisableOrphanedRules(string profileDir, HashSet<Guid> accountIds, SettingsImportResult result)
    {
        var path = Path.Combine(profileDir, "rules.json");
        var rules = SettingsBundle.ReadList<MailRule>(path);
        var turnedOff = rules.Where(r => r.AccountId is { } id && !accountIds.Contains(id) && r.IsEnabled).ToList();
        if (turnedOff.Count == 0) return;
        foreach (var rule in turnedOff) rule.IsEnabled = false;
        WriteJson(path, rules);
        result.Lines.Add($"Turned off {(turnedOff.Count == 1 ? "a rule" : "rules")} for an account that is not on this computer: "
                         + string.Join(", ", turnedOff.Select(r => r.Name)) + ".");
    }

    private static void DropOrphanedViewFolders(string profileDir, HashSet<Guid> accountIds, SettingsImportResult result)
    {
        var path = Path.Combine(profileDir, "views.json");
        var views = SettingsBundle.ReadList<SavedView>(path);
        var changed = views.Where(v => v.Folders.RemoveAll(f => !accountIds.Contains(f.AccountId)) > 0).ToList();
        if (changed.Count == 0) return;
        WriteJson(path, views);
        result.Lines.Add($"Removed folders of an account that is not on this computer from {(changed.Count == 1 ? "the saved view" : "the saved views")}: "
                         + string.Join(", ", changed.Select(v => v.Name)) + ".");
    }

    private static void ApplyPreferences(SettingsBundle bundle, string profileDir, Func<string, string> rewrite,
                                         HashSet<Guid> accountIds, HashSet<Guid> carriedIds)
    {
        var text = bundle.GetText("config.ini");
        if (text is null) return;
        var path = Path.Combine(profileDir, "config.ini");
        var local = File.Exists(path) ? ConfigService.ParseFile(File.ReadAllLines(path)) : new ConfigModel();
        // The file is read on top of this computer's settings, so a setting it does not mention (one
        // newer than the QuickMail that wrote it) keeps its value here instead of going back to the
        // default. Per-account sections start empty: those are the file's alone.
        var baseline = File.Exists(path) ? ConfigService.ParseFile(File.ReadAllLines(path)) : new ConfigModel();
        baseline.Accounts = [];
        var incoming = ConfigService.ParseFile(SplitLines(rewrite(text)), into: baseline);
        SettingsConfigTransfer.Apply(incoming, local, carriedIds);

        // Anything still naming an account that is not here falls back rather than pointing at nothing.
        if (Guid.TryParse(local.StartupFolderAccount, out var startupAccount) && !accountIds.Contains(startupAccount))
        {
            local.StartupFolder = string.Empty;
            local.StartupFolderAccount = string.Empty;
            local.StartupFolderLabel = string.Empty;
        }
        var calendarAccount = local.DefaultCalendarSource.Split('|')[0];
        if (Guid.TryParse(calendarAccount, out var calId) && !accountIds.Contains(calId))
            local.DefaultCalendarSource = string.Empty;
        foreach (var id in local.Accounts.Keys.Where(id => !accountIds.Contains(id)).ToList())
            local.Accounts.Remove(id);

        AtomicFile.WriteAllText(path, ConfigService.BuildFileText(local), Encoding.UTF8);
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>
    /// Replaces the local address book and its groups with the file's, keeping contacts synced from
    /// a server. Contact ids differ between profiles, so imported contacts are renumbered above
    /// every id kept here and each group's members rewritten to match (spec R8).
    /// </summary>
    private static void ApplyAddressBook(SettingsBundle bundle, string profileDir, SettingsImportResult result)
    {
        var contactsPath = Path.Combine(profileDir, "contacts.json");
        var groupsPath = Path.Combine(profileDir, "groups.json");
        var kept = SettingsBundle.ReadList<ContactModel>(contactsPath).Where(c => !c.IsLocal).ToList();
        var incoming = DeserializeList<ContactModel>(bundle.GetText("contacts.json")).Where(c => c.IsLocal).ToList();
        var groups = DeserializeList<GroupModel>(bundle.GetText("groups.json"));
        var syncedMembers = DeserializeDictionary(bundle.GetText(SettingsBundle.GroupMembersName));

        var nextId = kept.Count > 0 ? kept.Max(c => c.Id) + 1 : 1;
        var newIds = new Dictionary<int, int>();
        foreach (var contact in incoming)
        {
            newIds.TryAdd(contact.Id, nextId);
            contact.Id = nextId++;
        }

        var dropped = 0;
        foreach (var group in groups)
        {
            var members = new List<int>();
            foreach (var id in group.MemberContactIds)
            {
                if (newIds.TryGetValue(id, out var mapped)) { members.Add(mapped); continue; }
                if (syncedMembers.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var email))
                {
                    var here = kept.FirstOrDefault(c => string.Equals(c.EmailAddress, email, StringComparison.OrdinalIgnoreCase));
                    if (here is not null) { members.Add(here.Id); continue; }
                }
                dropped++;
            }
            group.MemberContactIds = members.Distinct().ToList();
        }

        WriteJson(contactsPath, kept.Concat(incoming).ToList());
        WriteJson(groupsPath, groups);
        var line = $"Address book: {SettingsImportPlan.Plural(incoming.Count, "contact")}, {SettingsImportPlan.Plural(groups.Count, "group")}.";
        if (dropped > 0)
            line += $" {SettingsImportPlan.Plural(dropped, "group member")} synced from a server could not be found here and {(dropped == 1 ? "was" : "were")} left out.";
        result.Lines.Add(line);
    }

    // ── The Local Calendar, once the local store is open ─────────────────────────

    public const string FailedCalendarFileName = "pending-calendar.failed.ics";

    /// <summary>
    /// Adds and updates Local Calendar appointments from a staged calendar.ics (spec R3). Never
    /// deletes one: mail.db is not part of the rollback copy. Called at startup once the local store
    /// is open; in --online mode there is none, and the file waits for a normal start.
    ///
    /// <para>Tried once. Whatever goes wrong, the file is set aside under
    /// <see cref="FailedCalendarFileName"/> and the user told where it is, so a file that cannot be
    /// applied can never stop QuickMail starting, or be reported at every start.</para>
    /// </summary>
    public static async Task ApplyPendingCalendarAsync(string profileDir, ILocalStoreService store)
    {
        var path = Path.Combine(profileDir, PendingCalendarFileName);
        if (!File.Exists(path)) return;
        string line;
        try
        {
            var existing = await store.LoadCalendarEventsAsync();
            var plan = CalendarIcsImporter.Plan(await File.ReadAllTextAsync(path), existing);
            var localUids = existing.Where(e => e.AccountId == CalendarEvent.LocalAccountId).Select(e => e.Uid).ToHashSet(StringComparer.Ordinal);
            foreach (var evt in plan.Events)
                await store.UpsertCalendarEventAsync(evt);
            var updated = plan.Events.Count(e => localUids.Contains(e.Uid));
            line = $"Local Calendar: {SettingsImportPlan.Plural(plan.Events.Count - updated, "appointment")} added, {updated} updated.";
            File.Delete(path);
        }
#pragma warning disable CA1031 // Startup must survive any failure here; the file is set aside and the user told.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogService.Log("Settings import: Local Calendar", ex);
            line = SetAsideCalendar(path)
                ? $"Local Calendar: the appointments could not be added. They are kept in {FailedCalendarFileName} in your QuickMail data folder; File, Import Calendar File can bring them in."
                : "Local Calendar: the appointments could not be added.";
        }
        AppendResultLine(profileDir, line);
    }

    private static bool SetAsideCalendar(string path)
    {
        try
        {
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, FailedCalendarFileName), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Settings import: could not set the calendar file aside", ex);
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private static void AppendResultLine(string profileDir, string line)
    {
        var path = Path.Combine(profileDir, ResultFileName);
        try
        {
            // No result waiting means the import's own was shown at an earlier start (an --online one):
            // this is the calendar's alone, and must not announce a second import.
            var result = File.Exists(path)
                ? JsonSerializer.Deserialize<SettingsImportResult>(File.ReadAllText(path)) ?? new SettingsImportResult { Succeeded = true }
                : new SettingsImportResult { Succeeded = true, Heading = "The Local Calendar from your imported settings." };
            result.Lines.Insert(Math.Max(0, result.Lines.Count - 1), line);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(result, Indented));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LogService.Log("Settings import: could not record the calendar result", ex);
        }
    }

    // ── Rollback copy ────────────────────────────────────────────────────────────

    internal static string CreateBackup(string profileDir, DateTime now)
    {
        var name = "backup-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(profileDir, name);
        for (var n = 2; Directory.Exists(dir); n++)
            dir = Path.Combine(profileDir, $"{name}-{n}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in ProfileFiles)
            {
                var source = Path.Combine(profileDir, file);
                if (File.Exists(source)) File.Copy(source, Path.Combine(dir, file));
            }
            var themes = Path.Combine(profileDir, "themes");
            if (Directory.Exists(themes))
            {
                var target = Directory.CreateDirectory(Path.Combine(dir, "themes")).FullName;
                foreach (var file in Directory.GetFiles(themes, "*.json"))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
            return dir;
        }
        catch
        {
            // An incomplete copy is not a backup; leaving it would look like one.
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Puts every file an import may touch back as the backup holds it, removing any the profile did
    /// not have. Returns the names it could not put back, so the user is never told everything was
    /// restored when something was not. A file that already matches the backup needs nothing done.
    /// </summary>
    internal static List<string> Restore(string profileDir, string backupDir)
    {
        var failed = new List<string>();
        foreach (var file in ProfileFiles)
        {
            var saved = Path.Combine(backupDir, file);
            var live = Path.Combine(profileDir, file);
            try
            {
                if (File.Exists(saved))
                {
                    if (!File.Exists(live) || !File.ReadAllBytes(live).AsSpan().SequenceEqual(File.ReadAllBytes(saved)))
                        File.Copy(saved, live, overwrite: true);
                }
                else if (File.Exists(live))
                {
                    File.Delete(live);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Log($"Settings import: could not restore {file}", ex);
                failed.Add(file);
            }
        }

        var themes = Path.Combine(profileDir, "themes");
        var savedThemes = Path.Combine(backupDir, "themes");
        try
        {
            if (Directory.Exists(themes))
                foreach (var file in Directory.GetFiles(themes, "*.json")) File.Delete(file);
            if (Directory.Exists(savedThemes))
            {
                Directory.CreateDirectory(themes);
                foreach (var file in Directory.GetFiles(savedThemes, "*.json"))
                    File.Copy(file, Path.Combine(themes, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Settings import: could not restore the themes", ex);
            failed.Add("themes");
        }
        return failed;
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────────

    private static List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }

    private static Dictionary<string, string> DeserializeDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }

    private static void WriteJson<T>(string path, T value) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Indented));
}
