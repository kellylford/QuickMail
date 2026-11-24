using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Rename Folder (#1083). The server renames the folder; this carries along everything QuickMail
/// keeps that names it — see <see cref="FolderRenameReferences"/> for the settings, plus the message
/// cache, the remembered list views and what is in memory — so nothing is left pointing at a name
/// that no longer exists. The standard folders, views, accounts and headings cannot be renamed, and
/// each says why rather than doing nothing.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Raised after a rename, once the main window's folder tree has caught up. The Rules window
    /// listens, so a rule open in its editor cannot save the old name back over the updated one.
    /// From inside a picker it is raised only when the picker has closed
    /// (<see cref="CommitPendingFolderTreeRebuild"/>): the windows that listen are behind it.
    /// </summary>
    public event Action<FolderRename>? FolderRenamed;

    // Renames made inside a modal picker, announced once it has closed.
    private readonly List<FolderRename> _pendingFolderRenames = [];

    private bool _folderRenameInProgress;

    /// <summary>
    /// Why the folder tree item <paramref name="node"/> cannot be renamed, or null when it can. Each
    /// reason says what the item is instead, so the user is not left to guess why nothing happened.
    /// </summary>
    public string? RenameFolderRefusal(FolderTreeNode? node)
    {
        if (node is null)
            return "Select a folder in the folder tree first.";
        if (node.IsCalendarNode || (node.Folder is { } cal && IsCalendarFolderName(cal.FullName)))
            return $"'{node.Label}' is a calendar, not a mail folder, so it cannot be renamed.";
        if (node.IsHeader && node.AccountId is not null)
            return $"'{node.Label}' is an account, not a folder. To rename an account, use Manage Accounts.";
        if (node.IsHeader)
            return $"'{node.Label}' is a heading, not a folder, so it cannot be renamed.";
        if (node.Folder is null)
            return $"'{node.Label}' is part of a folder path on the server, not a folder, so it cannot be renamed.";
        return RenameFolderRefusal(node.Folder, node.Label);
    }

    /// <summary>
    /// Why <paramref name="folder"/> cannot be renamed, or null when it can. What the folder pickers
    /// ask, which hold folders rather than tree items.
    /// </summary>
    public string? RenameFolderRefusal(MailFolderModel? folder, string? label = null)
    {
        if (folder is null)
            return "Select a folder first.";
        var name = string.IsNullOrEmpty(label) ? folder.DisplayName : label;
        if (folder.IsHeader)
            return $"'{name}' is an account, not a folder. To rename an account, use Manage Accounts.";
        if (IsCalendarFolderName(folder.FullName))
            return $"'{name}' is a calendar, not a mail folder, so it cannot be renamed.";
        if (folder.FullName.StartsWith(ViewPrefix, StringComparison.Ordinal) ||
            folder.FullName.StartsWith(ViewAllPrefix, StringComparison.Ordinal))
            return $"'{name}' is a saved view, not a folder. To rename a view, use Manage Views on the View menu.";
        if (folder.AccountId == Guid.Empty || folder.FullName.Length == 0 || folder.FullName[0] == '\0' ||
            Accounts.All(a => a.Id != folder.AccountId))
            return $"'{name}' is a view, not a folder, so it cannot be renamed.";
        // The standard folders, as Outlook has it: the server, other mail programs and QuickMail's
        // own virtual folders all find them by what they are, and a renamed Sent folder is one every
        // other program would go on looking for. POP3's four built-in folders carry a Kind too.
        if (folder.Kind != SpecialFolderKind.None ||
            folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return $"{folder.DisplayName} is a standard folder, so it cannot be renamed.";
        return null;
    }

    /// <summary>
    /// Renames <paramref name="folder"/> to <paramref name="proposedName"/> on the server and carries
    /// every reference to it, and to the folders inside it, across. Never throws: every failure comes
    /// back as a <see cref="FolderRenameResult"/> whose message says why, for the caller to show and
    /// say, and nothing local changes unless the server rename succeeded.
    /// </summary>
    /// <param name="insideModal">
    /// True when called from a modal folder picker. Files and the cache are updated straight away, but
    /// the folder tree rebuild and <see cref="FolderRenamed"/> wait for
    /// <see cref="CommitPendingFolderTreeRebuild"/>, because rebuilding the main window while the
    /// picker's message loop runs is the re-entrancy crash in CLAUDE.md's Modal Dialog Rules.
    /// </param>
    public async Task<FolderRenameResult> RenameFolderAsync(MailFolderModel folder, string? proposedName, bool insideModal = false)
    {
        if (RenameFolderRefusal(folder) is { } refusal)
            return FolderRenameResult.Refused(refusal);
        if (_folderRenameInProgress)
            return FolderRenameResult.Refused("Another folder is being renamed. Try again when it has finished.");

        var account = Accounts.First(a => a.Id == folder.AccountId);
        var accountFolders = _cachedFolders.TryGetValue(account.Id, out var cached) && cached.Count > 0
            ? cached
            : [folder];
        // The live copy: the caller's may be from a list the cache has since replaced.
        var live = accountFolders.FirstOrDefault(f => string.Equals(f.FullName, folder.FullName, StringComparison.Ordinal));
        if (live is null)
            return FolderRenameResult.Refused($"{folder.DisplayName} is no longer in the folder list. It may have been renamed or deleted elsewhere.");

        var (name, invalid) = FolderRenameReferences.ValidateName(live, proposedName, accountFolders, account.BackendKind);
        if (invalid is not null)
            return FolderRenameResult.Refused(invalid);

        var oldFullName    = live.FullName;
        var oldDisplayName = live.DisplayName;
        // Read before the rename, while the folder list still describes the account as it was.
        char? separator = account.BackendKind == BackendKind.ImapSmtp
            ? FolderRenameReferences.ImapSeparator(live, accountFolders)
            : null;

        _folderRenameInProgress = true;
        IsBusy = true;
        SetStatus($"Renaming folder {oldDisplayName}…", AnnouncementCategory.Status);
        try
        {
            string newFullName;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                newFullName = await _imap.RenameFolderAsync(account.Id, oldFullName, name!, null, cts.Token);
            }
            catch (Exception ex)
            {
                LogService.Log("RenameFolder", ex);
                // A timeout's own message is "A task was canceled", which tells a listener nothing.
                var reason = ex is OperationCanceledException ? "The mail server did not answer in time." : ex.Message;
                var failed = $"Could not rename {oldDisplayName}. {reason}";
                SetStatus(failed, AnnouncementCategory.Silent);   // the caller says it, in its own window
                return new FolderRenameResult(false, failed, failed);
            }
            if (string.IsNullOrEmpty(newFullName)) newFullName = oldFullName;

            var rename = new FolderRename(
                account.Id, oldFullName, newFullName,
                string.Equals(oldFullName, newFullName, StringComparison.Ordinal) ? null : separator,
                oldDisplayName, name!);

            // From here the folder has its new name on the server, so nothing below may report the
            // rename as failed: what could not be carried across is said, by name, alongside it.
            var problems = new List<string>();
            var report = UpdateFolderReferences(rename, account, problems);

            if (rename.NamesChange && rename.Separator is { } sep)
            {
                try { await _localStore.RenameFolderAsync(account.Id, oldFullName, newFullName, sep); }
                catch (Exception ex)
                {
                    LogService.Log("RenameFolder: local store", ex);
                    problems.Add("the messages saved on this computer for it will download again");
                }
            }

            if (rename.NamesChange)
            {
                try { _folderViewState?.Rename(account.Id, rename.Map); }
                catch (Exception ex) { LogService.Log("RenameFolder: folder views", ex); }
            }

            RemapLoadedState(rename, accountFolders);

            IReadOnlyList<MailFolderModel> fresh;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var list = await _imap.GetFoldersAsync(account.Id, cts.Token);
                SetCachedFolders(account.Id, list);
                ApplyAccountStatus(account, list, "folder-renamed");
                fresh = list;
            }
            catch (Exception ex)
            {
                // The list in hand has already been renamed in place (RemapLoadedState), so it is
                // right; keep it, and it is what the next launch draws from.
                LogService.Log("RenameFolder: refresh after rename", ex);
                SetCachedFolders(account.Id, accountFolders);
                fresh = accountFolders;
            }

            if (insideModal)
            {
                _folderTreeRebuildPending = true;
                _pendingFolderRenames.Add(rename);
            }
            else
            {
                RebuildFolderListFromCache();
                FolderRenamed?.Invoke(rename);
            }

            var announcement = report.Summary();
            if (problems.Count > 0)
                announcement = (announcement.Length > 0 ? announcement + " " : string.Empty)
                               + "But " + FolderRenameReferences.JoinWithAnd(problems) + ".";
            var message = $"Renamed {oldDisplayName} to {name}." + (announcement.Length > 0 ? " " + announcement : string.Empty);
            // Silent: focus landing on the renamed folder says its new name, and the caller says the
            // rest (the announcement) in its own window, which may not be this one.
            SetStatus(message, AnnouncementCategory.Silent);
            return new FolderRenameResult(true, message, announcement, rename, fresh);
        }
        finally
        {
            IsBusy = false;
            _folderRenameInProgress = false;
        }
    }

    /// <summary>
    /// Rewrites and saves the stored references: rules, saved views, config.ini and the account.
    /// Each store is saved on its own, so one that cannot be written does not stop the others, and
    /// is added to <paramref name="problems"/> by name.
    /// </summary>
    private FolderRenameReferences.Report UpdateFolderReferences(FolderRename rename, AccountModel account, List<string> problems)
    {
        List<MailRule>? rules;
        try { rules = _ruleService.LoadRules(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not read as none: saving an empty list would replace every rule (#700).
            LogService.Log("RenameFolder: rules could not be read", ex);
            rules = null;
            if (rename.NamesChange)
                problems.Add("your rules could not be read, so a rule that files mail into it must have its folder set again");
        }

        var views = _viewService.Load();
        var cfg = _configService.Load();
        var report = FolderRenameReferences.Apply(rename, rules, views, cfg, account);

        if (rules is not null && report.UpdatedRules.Count > 0)
        {
            try { _ruleService.SaveRules(rules); }
            catch (Exception ex)
            {
                LogService.Log("RenameFolder: rules could not be saved", ex);
                problems.Add(report.UpdatedRules.Count == 1
                    ? $"the rule {report.UpdatedRules[0]} could not be saved, so set its folder again"
                    : $"the rules {FolderRenameReferences.JoinWithAnd(report.UpdatedRules)} could not be saved, so set their folder again");
                report.UpdatedRules.Clear();
            }
        }

        if (report.ViewsChanged)
        {
            try { _viewService.Save(views); }
            catch (Exception ex)
            {
                LogService.Log("RenameFolder: views could not be saved", ex);
                problems.Add("your saved views could not be saved");
                report.UpdatedViews.Clear();
            }
            // The views in memory are what the tree and the view commands use until the next load.
            FolderRenameReferences.Apply(rename, null, [.. SavedViews], new ConfigModel(), null);
        }

        if (report.ConfigChanged)
        {
            try { _configService.Save(cfg); }
            catch (Exception ex)
            {
                LogService.Log("RenameFolder: settings could not be saved", ex);
                problems.Add("the startup folder and remembered destinations could not be saved");
                report.StartupFolderUpdated = false;
            }
        }

        if (report.AccountChanged)
        {
            try { _accountService.SaveAccounts([.. Accounts]); }
            catch (Exception ex)
            {
                LogService.Log("RenameFolder: accounts could not be saved", ex);
                problems.Add("its hidden, favorite or Archive setting could not be saved");
            }
        }

        return report;
    }

    /// <summary>
    /// Brings what is in memory up to date without waiting for a reload: the account's folder models
    /// (which the tree, the selection, any open picker and anything else holding one share), and the
    /// folder of every message held anywhere — the list, open tabs, the message clipboard. Updating
    /// the shared objects is what keeps a Move or Delete on a message already in hand from going to
    /// the old name. Nothing here raises a change notification, so it is safe inside a modal picker.
    /// </summary>
    private void RemapLoadedState(FolderRename rename, IReadOnlyList<MailFolderModel> accountFolders)
    {
        void RemapFolder(MailFolderModel f)
        {
            if (f.AccountId != rename.AccountId) return;
            var isTheFolder = rename.IsTheFolder(f.FullName);
            if (rename.Map(f.FullName) is { } mapped) f.FullName = mapped;
            if (isTheFolder) f.DisplayName = rename.NewDisplayName;
        }

        foreach (var f in accountFolders) RemapFolder(f);
        if (SelectedFolder is { } open && !accountFolders.Contains(open)) RemapFolder(open);

        if (!rename.NamesChange) return;

        var held = _rawMessages
            .Concat(Messages)
            .Concat(OpenTabs.OfType<MessageTabViewModel>().Select(t => t.Summary))
            .Concat(MessageClipboard.Messages);
        if (SelectedMessage is { } selected) held = held.Append(selected);

        foreach (var m in held.Distinct(ReferenceEqualityComparer.Instance).Cast<MailMessageSummary>())
            if (m.AccountId == rename.AccountId && rename.Map(m.FolderName) is { } mapped)
                m.FolderName = mapped;
    }

    /// <summary>
    /// Raises <see cref="FolderRenamed"/> for renames made inside a picker that has now closed.
    /// Called from <see cref="CommitPendingFolderTreeRebuild"/>, after the tree has caught up.
    /// </summary>
    private void FlushPendingFolderRenames()
    {
        if (_pendingFolderRenames.Count == 0) return;
        var renames = _pendingFolderRenames.ToList();
        _pendingFolderRenames.Clear();
        foreach (var rename in renames) FolderRenamed?.Invoke(rename);
    }
}
