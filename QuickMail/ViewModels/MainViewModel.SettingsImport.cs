using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Settings export and import (issue #507): the main window's side of it. Import itself is applied
/// at startup (<see cref="SettingsImporter.ApplyPendingAtStartup"/>); what is left for the running
/// app is pointing an imported account's folder references at its real folders the first time
/// they load (spec Revision 2 R5).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The profile directory, set by App startup. Null in tests, where the heal never runs.</summary>
    public string? ProfileDirectory { get; set; }

    /// <summary>Raised to open the Export Settings window.</summary>
    public event EventHandler? ExportSettingsRequested;

    /// <summary>Raised to start an import: the View asks for the file and shows the preview.</summary>
    public event EventHandler? ImportSettingsRequested;

    public void RequestExportSettings() => ExportSettingsRequested?.Invoke(this, EventArgs.Empty);

    public void RequestImportSettings() => ImportSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Each account's known folders, id to display path, for a settings file.</summary>
    public Dictionary<Guid, IReadOnlyDictionary<string, string>> BuildFolderPathsForExport() =>
        SettingsBundle.BuildFolderPaths(_cachedFolders);

    /// <summary>
    /// Called with an account's freshly loaded folders. When a settings import left folder names
    /// waiting for this account, heals its rules, saved views, startup folder and archive folder
    /// against these folders, saves what changed, and says what it could not match.
    /// </summary>
    private void HealImportedFolderReferences(Guid accountId, IReadOnlyList<MailFolderModel> folders)
    {
        if (ProfileDirectory is null) return;
        // Only a Microsoft 365 (Graph) account names folders by an id that can differ between
        // computers. An IMAP folder's name is its path, the same everywhere, and running the
        // remapper's leaf-name match over one could retarget a rule to an unrelated folder of the
        // same name; a rule for an IMAP folder that is gone fails as it would on any computer.
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account?.BackendKind != BackendKind.MicrosoftGraph) return;
        var paths = ImportFolderHeal.Take(ProfileDirectory, accountId);
        if (paths is null) return;

        try
        {
            var rules = _ruleService.LoadRules();
            var views = _viewService.Load();
            var cfg = _configService.Load();
            var report = ImportFolderHeal.Heal(accountId, folders, paths, rules, views, cfg, account, out var archiveChanged);

            if (report.AnythingChanged)
            {
                _ruleService.SaveRules(rules);
                _viewService.Save(views);
                _configService.Save(cfg);
            }
            if (archiveChanged)
                _accountService.SaveAccounts([.. Accounts]);

            // Shown in the status bar as well as spoken, and logged: the names of rules turned off must
            // not be lost to a muted announcement or to speech at startup.
            var summary = report.Summary();
            if (archiveChanged && account.ArchiveFolderFullName is null)
                summary += (summary.Length > 0 ? ". " : "") + "The archive folder from your other computer was not found, so the default is used";
            if (summary.Length > 0)
            {
                var text = $"{account.AccountLabel}, after importing settings: {summary}";
                LogService.Log("Settings import folder heal: " + text);
                SetStatusEvenIfUnchanged(text, AnnouncementCategory.Result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The names were taken, so this is not retried; the references stay as imported, which is
            // what they would be without the heal.
            LogService.Log("Settings import: folder heal failed", ex);
        }
    }
}
