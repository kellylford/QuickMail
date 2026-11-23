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

    // Concurrent executions allowed so the menu item stays enabled while one runs (the app's convention:
    // dimmed, never disabled), and a second press is answered rather than ignored.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendAndReceiveAsync()
    {
        // EvenIfUnchanged: pressing F9 again must be answered again, even with the same words.
        if (_sendReceiveRunning)
        {
            SetStatusEvenIfUnchanged("Send and Receive is already running.", AnnouncementCategory.Result);
            return;
        }
        _sendReceiveRunning = true;
        try
        {
            if (Accounts.Count == 0)
            {
                SetStatusEvenIfUnchanged("There are no accounts to send or receive mail for.", AnnouncementCategory.Result);
                return;
            }

            SetStatus("Sending and receiving…", AnnouncementCategory.Status);
            var sent = await SendWaitingMailAsync();

            // Receive. Known offline, the server would only time out; say so instead, as F5 does.
            var received = IsKnownOffline
                ? "You're offline, so QuickMail could not check for new mail."
                : await CheckInboxesForNewMailAsync();

            // One closing sentence with both halves: said separately, the send outcome was replaced by the
            // next status line before it could be spoken.
            SetStatusEvenIfUnchanged(sent is null ? received : $"{sent} {received}", AnnouncementCategory.Result);
        }
        finally
        {
            _sendReceiveRunning = false;
        }
    }

    /// <summary>
    /// Drains the Outbox when anything is waiting and returns what to say about it, or null when nothing
    /// was waiting: an empty Outbox is the usual case and not news.
    /// </summary>
    private async Task<string?> SendWaitingMailAsync()
    {
        if (!ShowOutboxFolder) return null;
        int waiting;
        try { waiting = await _outbox!.CountAsync(); }
        catch (Exception ex) { LogService.Log("Send and Receive: Outbox count", ex); return null; }
        if (waiting == 0) return null;

        _sendReceiveDrainSummary = null;
        _sendReceiveDrain = true;
        string? noOutcome;
        try { noOutcome = await ForceDrainAsync(sayWhenEmpty: false); }
        finally { _sendReceiveDrain = false; }
        return noOutcome ?? _sendReceiveDrainSummary;
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
                // What the Inbox already held, so only mail that wasn't is counted as new. What the sync
                // returns is not that: Graph and POP3 return the whole sync window once anything changed,
                // and IMAP's "next UID onward" fetch returns the newest message even when nothing is new.
                var known = OnlineMode ? null : await KnownMessageIdsAsync(job.Account.Id, job.Inbox.FullName);

                // Off the UI thread, as the IDLE and sweep paths run it.
                var incoming = await Task.Run(() => OnlineMode
                    ? _syncService.SyncOneFolderOnlineAsync(job.Account, job.Inbox, cts.Token)
                    : _syncService.SyncFolderFullAsync(job.Account, job.Inbox, cts.Token));
                var arrived = known is null ? incoming : incoming.Where(m => !known.Contains(m.MessageId)).ToList();
                return (job.Account, Incoming: incoming, Arrived: arrived, Reached: true);
            }
            catch (Exception ex)
            {
                LogService.Log($"Send and Receive: {job.Account.AccountLabel} Inbox", ex);
                IReadOnlyList<MailMessageSummary> none = [];
                return (job.Account, Incoming: none, Arrived: none, Reached: false);
            }
        }));

        var newCount = 0;
        foreach (var (account, incoming, arrived, reached) in results)
        {
            if (!reached) { unreachable.Add(account.AccountLabel); continue; }
            newCount += arrived.Count;
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

    /// <summary>
    /// The ids the local store holds for the folder. A store that can't be read gives an empty set, so
    /// everything fetched counts as new: an overcount is the safer error than no answer at all.
    /// </summary>
    private async Task<HashSet<string>> KnownMessageIdsAsync(Guid accountId, string folderName)
    {
        try { return await _localStore.GetAllMessageIdsAsync(accountId, folderName); }
        catch (Exception ex)
        {
            LogService.Log("Send and Receive: reading the cached Inbox", ex);
            return [];
        }
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
