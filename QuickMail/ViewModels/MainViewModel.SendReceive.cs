using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Send and Receive (#874): one command that sends whatever is waiting in the Outbox and then checks
/// every account's Inbox for new mail. Refresh (F5) reloads the folder being viewed and nothing else,
/// which is why it was hard to tell what it did; this is the command for "is there anything new".
/// </summary>
public partial class MainViewModel
{
    private bool _sendReceiveRunning;

    /// <summary>How long one Inbox check may take before it is reported as not reached.</summary>
    private static readonly TimeSpan SendReceiveInboxTimeout = TimeSpan.FromSeconds(60);

    [RelayCommand]
    private async Task SendAndReceiveAsync()
    {
        if (_sendReceiveRunning)
        {
            SetStatus("Send and Receive is already running.", AnnouncementCategory.Result);
            return;
        }
        _sendReceiveRunning = true;
        try
        {
            if (Accounts.Count == 0)
            {
                SetStatus("There are no accounts to send or receive mail for.", AnnouncementCategory.Result);
                return;
            }

            // Send. Only when something is waiting: an empty Outbox is the usual case and not news.
            // Its outcome is announced as a result by the drain's own completion handler.
            if (ShowOutboxFolder)
            {
                int waiting;
                try { waiting = await _outbox!.CountAsync(); }
                catch (Exception ex) { LogService.Log("Send and Receive: Outbox count", ex); waiting = 0; }
                if (waiting > 0)
                    await DrainOutboxNowAsync(sayWhenEmpty: false);
            }

            // Receive. Known offline, the server would only time out; say so instead, as F5 does.
            if (IsKnownOffline)
            {
                SetStatus("You're offline. QuickMail will check for new mail when you're back online.", AnnouncementCategory.Result);
                return;
            }

            SetStatus("Checking for new mail…", AnnouncementCategory.Status);
            SetStatus(await CheckInboxesForNewMailAsync(), AnnouncementCategory.Result);
        }
        finally
        {
            _sendReceiveRunning = false;
        }
    }

    /// <summary>
    /// Syncs every connected account's Inbox and returns what to tell the user. New mail is merged into
    /// the list by the sync's own FolderSynced event and gets the sounds and notifications it would get
    /// from the background sweep.
    /// </summary>
    private async Task<string> CheckInboxesForNewMailAsync()
    {
        // The same folder-list pass F5 makes: it is what brings back an account that dropped and has
        // since come back, so it is in the connected set below.
        await RefreshAllFolderListsAsync();

        // Accounts and the folder cache are UI-thread-owned; read them before leaving it.
        var jobs = new List<(AccountModel Account, MailFolderModel Inbox)>();
        var unreachable = new List<string>();
        foreach (var account in Accounts)
        {
            var inbox = _connectedAccountIds.Contains(account.Id) && _cachedFolders.TryGetValue(account.Id, out var folders)
                ? folders.FirstOrDefault(IsInboxFolder)
                : null;
            if (inbox != null) jobs.Add((account, inbox));
            else unreachable.Add(account.AccountLabel);
        }

        var results = await Task.WhenAll(jobs.Select(async job =>
        {
            using var cts = new CancellationTokenSource(SendReceiveInboxTimeout);
            try
            {
                // Off the UI thread, as the IDLE and sweep paths run it.
                var incoming = await Task.Run(() => OnlineMode
                    ? _syncService.SyncOneFolderOnlineAsync(job.Account, job.Inbox, cts.Token)
                    : _syncService.SyncFolderFullAsync(job.Account, job.Inbox, cts.Token));
                return (job.Account, Incoming: incoming, Reached: true);
            }
            catch (Exception ex)
            {
                LogService.Log($"Send and Receive: {job.Account.AccountLabel} Inbox", ex);
                return (job.Account, Incoming: (IReadOnlyList<MailMessageSummary>)[], Reached: false);
            }
        }));

        var newCount = 0;
        foreach (var (account, incoming, reached) in results)
        {
            if (!reached) { unreachable.Add(account.AccountLabel); continue; }
            newCount += incoming.Count;
            if (incoming.Count == 0) continue;
            // As the sweep does, and in its order: watched first, since the two share a dedup set.
            MaybePlayMailSounds(account, incoming, isInbox: true);
            MaybeNotifyWatchedMail(account, incoming);
            MaybeNotifyNewMail(account, incoming);
            ScheduleFolderCountRefresh(account.Id);
        }

        return SendReceiveSummary(
            checkedCount: results.Count(r => r.Reached),
            newCount: OnlineMode ? null : newCount,
            unreachable: unreachable);
    }

    private static bool IsInboxFolder(MailFolderModel folder) =>
        folder.Kind == SpecialFolderKind.Inbox
        || string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What Send and Receive says when it is done. <paramref name="newCount"/> is null in online mode,
    /// where the fetch returns the latest messages rather than only the new ones, so there is no count.
    /// </summary>
    internal static string SendReceiveSummary(int checkedCount, int? newCount, IReadOnlyList<string> unreachable)
    {
        if (checkedCount == 0)
            return unreachable.Count == 1
                ? $"Could not check {unreachable[0]} for new mail."
                : "Could not check any account for new mail.";

        var text = newCount switch
        {
            null => "Checked for new mail.",
            0    => "No new messages.",
            1    => "1 new message.",
            _    => $"{newCount} new messages.",
        };
        if (unreachable.Count > 0)
            text += $" Could not check {string.Join(", ", unreachable)}.";
        return text;
    }
}
