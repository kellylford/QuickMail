using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// The Outbox virtual folder (#637): every draft waiting to upload and message waiting to send,
/// across all accounts, read from the local queue. Enter reopens a row in the compose window;
/// Delete removes it from the queue; Send Outbox Now drains it on demand.
/// </summary>
public partial class MainViewModel
{
    private readonly IOutboxService? _outbox;

    /// <summary>
    /// Mail written while the server could not be reached (#637): drafts waiting to upload and
    /// messages waiting to send, across all accounts, read from the local store only. Deliberately
    /// absent from <see cref="AllVirtualFolders"/>: it is never a startup folder and never the
    /// subject of a saved view. Not shown in --online mode, which has no store to queue into.
    /// </summary>
    // "\0" rather than " ": a fixed one-character escape, so it cannot swallow a following hex
    // digit the way "\x00" can (see the sentinel comment in MainViewModel.cs). Same NUL either way.
    public static readonly MailFolderModel OutboxFolder = new()
    {
        FullName    = "\0Outbox",
        DisplayName = "Outbox",
        Kind        = SpecialFolderKind.Outbox,
    };

    /// <summary>What every message command other than Enter and Delete says on an Outbox row.</summary>
    internal const string OutboxRowHint = "This message is waiting in the Outbox. Press Enter to open it in the compose window.";

    /// <summary>True when the Outbox exists at all: a queue is wired and there is a store behind it.</summary>
    private bool ShowOutboxFolder => !OnlineMode && _outbox is { IsAvailable: true };

    /// <summary>True when the Outbox folder is selected; the window routes Enter to <see cref="OpenOutboxItemCommand"/>.</summary>
    public bool IsSelectedFolderOutbox =>
        SelectedFolder != null &&
        string.Equals(SelectedFolder.FullName, OutboxFolder.FullName, StringComparison.Ordinal);

    private static bool IsOutboxRow(MailMessageSummary? summary)
        => summary != null && string.Equals(summary.FolderName, OutboxFolder.FullName, StringComparison.Ordinal);

    // True while a Send Outbox Now is in flight, so its outcome is announced as the result of the
    // user's action; automatic drains are background progress.
    private bool _manualOutboxDrain;

    // True while Delete is removing rows itself, so the queue's Changed event does not relist the
    // Outbox out from under the removal (the real service raises it inline on the UI thread).
    private bool _suppressOutboxRelist;

    /// <summary>
    /// The queue as message rows. The state leads the subject ("Waiting to send: Lunch Friday") so
    /// it is shown and spoken under every row layout — the preview column can be turned off, the
    /// subject cannot. From is the account the message leaves from, which for outgoing mail is the
    /// "who" that matters.
    /// </summary>
    private async Task<List<MailMessageSummary>> BuildOutboxSummariesAsync()
    {
        if (_outbox == null) return [];
        var items = await _outbox.ListAsync();
        var labels = Accounts.ToDictionary(a => a.Id, a => a.AccountLabel);
        return items.Select(i => new MailMessageSummary
        {
            MessageId         = i.Id,
            AccountId         = i.AccountId,
            FolderName        = OutboxFolder.FullName,
            FolderDisplayName = OutboxFolder.DisplayName,
            From              = labels.GetValueOrDefault(i.AccountId, "Unknown account"),
            To                = i.To,
            Subject           = $"{i.StateDisplay}: {i.Subject}",
            Date              = i.CreatedUtc,
            IsRead            = true,
            HasAttachments    = i.HasAttachments,
            Preview           = i.StateDisplay,
        }).ToList();
    }

    private async Task FetchOutboxAsync()
    {
        var loadVersion = Interlocked.Increment(ref _folderLoadVersion);
        var expectedFolder = SelectedFolder;
        Messages.Clear();
        StatusText = "Loading Outbox…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        try
        {
            var rows = await BuildOutboxSummariesAsync();
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            SetMessages(rows);
            StatusText = OutboxCountText(Messages.Count);
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = "Outbox load cancelled.";
        }
        catch (Exception ex)
        {
            LogService.Log("FetchOutbox failed", ex);
            StatusText = "Could not load the Outbox.";
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
    }

    private static string OutboxCountText(int n)
        => n == 0 ? "Outbox is empty." : $"{n} {(n == 1 ? "item" : "items")} in Outbox.";

    /// <summary>
    /// Refreshes the Outbox listing in place after the queue changed, keeping the user's selection
    /// (by row id, else by position) instead of clearing the list out from under their focus — a
    /// drain flips each row through "Sending…" and the listing follows.
    /// </summary>
    private async Task RelistOutboxPreservingSelectionAsync()
    {
        if (!IsSelectedFolderOutbox) return;
        var selectedId = SelectedMessage?.MessageId;
        var selectedIndex = SelectedMessage != null ? Messages.IndexOf(SelectedMessage) : -1;

        List<MailMessageSummary> rows;
        try { rows = await BuildOutboxSummariesAsync(); }
        catch (Exception ex) { LogService.Log("Outbox relist", ex); return; }
        if (!IsSelectedFolderOutbox) return;

        SetMessages(rows);
        StatusText = OutboxCountText(Messages.Count);
        if (Messages.Count == 0) { SelectedMessage = null; return; }

        var byId = selectedId == null ? null : Messages.FirstOrDefault(m => m.MessageId == selectedId);
        SelectedMessage = byId ?? Messages[Math.Clamp(selectedIndex, 0, Messages.Count - 1)];
    }

    /// <summary>
    /// Puts the queue length on the Outbox tree node. A local read, so it runs before any connect
    /// and again on every change. The node words it "waiting", never "unread".
    /// </summary>
    private async Task RefreshOutboxCountAsync()
    {
        if (!ShowOutboxFolder) return;
        int count;
        try { count = await _outbox!.CountAsync(); }
        catch (Exception ex) { LogService.Log("Outbox: count", ex); return; }

        OutboxFolder.UnreadCount = count;
        var node = FolderTree == null ? null
            : FlattenAllNodes(FolderTree).FirstOrDefault(n => ReferenceEquals(n.Folder, OutboxFolder));
        node?.NotifyUnreadChanged();
    }

    /// <summary>Enter on an Outbox row: reopen it in the compose window, with everything it was saved with.</summary>
    [RelayCommand]
    private async Task OpenOutboxItemAsync()
    {
        var id = SelectedMessage?.MessageId;
        if (id == null || _outbox == null) return;

        ComposeModel? compose;
        try { compose = await _outbox.LoadComposeAsync(id); }
        catch (Exception ex)
        {
            LogService.Log("Outbox: open item", ex);
            SetStatus("Could not open that Outbox item.", AnnouncementCategory.Result);
            return;
        }

        if (compose == null)
        {
            // Sent or removed since the list was drawn.
            SetStatus("That Outbox item is no longer there.", AnnouncementCategory.Result);
            await RelistOutboxPreservingSelectionAsync();
            return;
        }
        // Held from here until the compose window closes, so no drain sends it from under the edit.
        _outbox.Hold(id);
        ComposeRequested?.Invoke(compose);
    }

    /// <summary>
    /// Delete on Outbox rows. Unlike an ordinary delete this asks first: there is no Trash to get
    /// the message back from, and it has never been anywhere but this computer.
    /// </summary>
    private async Task RemoveOutboxItemsAsync(IReadOnlyList<MailMessageSummary> rows)
    {
        if (_outbox == null || rows.Count == 0) return;

        var n = rows.Count;
        var prompt = n == 1
            ? "Remove this message from the Outbox? It has not been sent and will be discarded."
            : $"Remove these {n} messages from the Outbox? They have not been sent and will be discarded.";
        if (ConfirmationRequested != null && !ConfirmationRequested(prompt, "Remove from Outbox"))
            return;

        var minIdx = rows.Min(m => Messages.IndexOf(m));
        _suppressOutboxRelist = true;
        try
        {
            foreach (var row in rows)
            {
                Messages.Remove(row);
                try { await _outbox.RemoveAsync(row.MessageId); }
                catch (Exception ex) { LogService.Log($"Outbox: remove {row.MessageId}", ex); }
            }
        }
        finally
        {
            _suppressOutboxRelist = false;
        }
        var removedIds = new HashSet<string>(rows.Select(r => r.MessageId), StringComparer.Ordinal);
        _rawMessages.RemoveAll(m => IsOutboxRow(m) && removedIds.Contains(m.MessageId));
        RebuildActiveGroupView();

        if (ViewMode == ViewMode.Messages && Messages.Count > 0)
        {
            SelectedMessage = Messages[Math.Max(0, Math.Min(minIdx, Messages.Count - 1))];
            MessageListFocusRequested?.Invoke();
        }
        else
        {
            SelectedMessage = null;
        }

        await RefreshOutboxCountAsync();
        SetStatus(n == 1 ? "1 Outbox item removed." : $"{n} Outbox items removed.", AnnouncementCategory.MessageAction);
    }

    /// <summary>Send Outbox Now: a drain that ignores backoff, retries failed rows, and does not wait for a connectivity signal.</summary>
    [RelayCommand]
    private async Task SendOutboxNowAsync()
    {
        if (!ShowOutboxFolder) return;
        SetStatus("Sending Outbox…", AnnouncementCategory.Status);

        OutboxFlushResult result;
        _manualOutboxDrain = true;
        try { result = await _outbox!.FlushAsync(force: true); }
        catch (Exception ex)
        {
            LogService.Log("Outbox: Send Outbox Now", ex);
            SetStatus($"Outbox: {ex.Message}", AnnouncementCategory.Result);
            return;
        }
        finally { _manualOutboxDrain = false; }

        // Anything that reached an outcome is announced by the FlushCompleted handler, once.
        if (result.Any) return;
        SetStatus(
            result.Deferred > 0 ? "Could not reach the server. The Outbox will try again when you're online."
            : result.Skipped > 0 ? "Outbox is busy: a drain is already running, or every item is open in a compose window."
            : "Outbox is empty.",
            AnnouncementCategory.Result);
    }

    /// <summary>The fallback sync tick's drain: never lets a queue problem stop the sweep.</summary>
    private async Task DrainOutboxQuietlyAsync(CancellationToken ct)
    {
        if (!ShowOutboxFolder) return;
        try { await _outbox!.FlushAsync(ct: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Log("Outbox: fallback-tick drain", ex); }
    }

    private void OnOutboxChanged() => _ui.Post(() =>
    {
        if (_suppressOutboxRelist) return;
        RefreshOutboxCountAsync().LogFaults("Outbox count");
        RelistOutboxPreservingSelectionAsync().LogFaults("Outbox relist");
    });

    private void OnOutboxFlushCompleted(OutboxFlushResult result)
    {
        // Read on the raising thread: the manual command's own await is what clears it, and that
        // continuation cannot run before this handler returns.
        var category = _manualOutboxDrain ? AnnouncementCategory.Result : AnnouncementCategory.Status;
        _ui.Post(() =>
        {
            SetStatus(SummariseFlush(result), category);
            RefreshOutboxCountAsync().LogFaults("Outbox count");
        });
    }

    /// <summary>One sentence per drain, never one per item: "Outbox: 2 messages sent, 1 draft uploaded."</summary>
    internal static string SummariseFlush(OutboxFlushResult r)
    {
        var parts = new List<string>(2);
        if (r.Sent > 0)           parts.Add($"{r.Sent} {(r.Sent == 1 ? "message" : "messages")} sent");
        if (r.DraftsUploaded > 0) parts.Add($"{r.DraftsUploaded} {(r.DraftsUploaded == 1 ? "draft" : "drafts")} uploaded");
        var text = parts.Count > 0 ? $"Outbox: {string.Join(", ", parts)}." : "Outbox:";
        if (r.Failed > 0)
            text += $" {r.Failed} failed. See the Outbox folder.";
        return text;
    }
}
