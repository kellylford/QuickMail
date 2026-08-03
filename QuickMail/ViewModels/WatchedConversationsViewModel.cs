using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>One watched conversation as shown in the manager list.</summary>
public sealed partial class WatchRow : ObservableObject
{
    public Guid   Id                { get; }
    /// <summary>The matching key. Shown nowhere and deliberately not editable — see the class
    /// comment on <see cref="WatchedConversationsViewModel"/>.</summary>
    public string NormalizedSubject { get; }
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>Bound as the type-ahead path, so typing jumps by the label the user reads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _label;

    /// <summary>How many cached messages this watch currently matches. Null when unknown — in
    /// <c>--online</c> mode there is no local cache to count, and 0 there would be a lie.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private int? _messageCount;

    public WatchRow(WatchedConversation watch)
    {
        Id                = watch.Id;
        NormalizedSubject = watch.NormalizedSubject;
        CreatedUtc        = watch.CreatedUtc;
        _label            = string.IsNullOrWhiteSpace(watch.Label) ? watch.NormalizedSubject : watch.Label;
    }

    public string CountText => MessageCount switch
    {
        null => "count unavailable",
        1    => "1 message",
        _    => $"{MessageCount} messages",
    };

    public string WatchedSince => CreatedUtc.ToLocalTime().ToString("d MMMM yyyy");

    /// <summary>
    /// The row's spoken and displayed text. Screen readers read a Selector item's accessible name
    /// from <see cref="ToString"/>, not from the template or DisplayMemberPath, so this is the
    /// single source for both — see SelectorItemAccessibilityTests.
    /// </summary>
    public string DisplayText => $"{Label}, {CountText}, watched {WatchedSince}";

    public override string ToString() => DisplayText;
}

/// <summary>
/// Drives the Watched Conversations manager: review what you are watching, rename a watch, stop
/// watching, and jump to a conversation.
/// <para><b>Rename is label-only.</b> A watch matches on its normalized subject; that key is not
/// editable here, because editing it would silently change which messages the watch collects while
/// presenting as a rename. The window says so once, as a hint.</para>
/// </summary>
public partial class WatchedConversationsViewModel : ObservableObject
{
    private readonly IWatchService      _watchService;
    private readonly ILocalStoreService _localStore;
    private readonly bool               _onlineMode;

    /// <summary>Raised when the user asks to open a conversation; carries its normalized subject.</summary>
    public event Action<string>? GoToRequested;

    /// <summary>
    /// Raised after this window changes the watch list, carrying the affected conversation's key.
    /// The main window re-stamps rows and prunes the watched folder in response — this window is
    /// modeless, so that folder may be visible behind it while the user prunes.
    /// </summary>
    public event Action<string>? WatchesChanged;

    // Which watch the open rename is about. Not SelectedWatch — see BeginRename.
    private Guid _renamingId;

    /// <summary>Raised for screen reader announcements; the View routes these to AccessibilityHelper.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    public ObservableCollection<WatchRow> Watches { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedLabel))]
    [NotifyCanExecuteChangedFor(nameof(StopWatchingCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginRenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToCommand))]
    private WatchRow? _selectedWatch;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editLabel = string.Empty;

    [ObservableProperty]
    private string _renameError = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    public bool HasSelection  => SelectedWatch != null;
    public bool IsEmpty       => Watches.Count == 0;
    public string SelectedLabel => SelectedWatch?.Label ?? string.Empty;

    public WatchedConversationsViewModel(
        IWatchService watchService, ILocalStoreService localStore, bool onlineMode = false)
    {
        _watchService = watchService;
        _localStore   = localStore;
        _onlineMode   = onlineMode;

        Reload();
    }

    private void Reload()
    {
        Watches.Clear();
        foreach (var w in _watchService.GetAll())
            Watches.Add(new WatchRow(w));
        SelectedWatch = Watches.FirstOrDefault();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        OnPropertyChanged(nameof(IsEmpty));
        Status = Watches.Count == 0
            ? "No watched conversations. Press Ctrl+Shift+W on a message to watch its conversation."
            : $"{Watches.Count} watched {(Watches.Count == 1 ? "conversation" : "conversations")}.";
    }

    /// <summary>
    /// Fills in each row's message count from the local cache. Counts are computed, never stored:
    /// a stored count would drift from the cache on every sync, and a wrong count is worse than a
    /// late one. Safe to fail — a row simply keeps its unknown count.
    /// </summary>
    public async Task LoadCountsAsync(CancellationToken ct)
    {
        // No local store in --online mode, so there is nothing to count. Leaving MessageCount null
        // shows "count unavailable" rather than a confident and wrong zero.
        if (_onlineMode) return;

        Dictionary<string, int> counts;
        try
        {
            // Off the UI thread deliberately. Microsoft.Data.Sqlite's *Async methods complete
            // synchronously, so both the whole-table read and the GroupBy over every cached
            // message would otherwise run on the dispatcher and freeze the window while it opens.
            counts = await Task.Run(async () =>
            {
                var all = await _localStore.LoadAllSummariesAsync().ConfigureAwait(false);
                return all
                    .GroupBy(m => ConversationBuilder.NormalizeSubject(m.Subject),
                             StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            LogService.Log("Watched manager: counting cached messages failed", ex);
            return;
        }

        // Back on the UI thread (ConfigureAwait(true)) before touching observable rows.
        if (ct.IsCancellationRequested) return;

        foreach (var row in Watches)
            row.MessageCount = counts.TryGetValue(row.NormalizedSubject, out var n) ? n : 0;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void StopWatching()
    {
        var row = SelectedWatch;
        if (row == null) return;

        var index = Watches.IndexOf(row);
        if (!_watchService.Unwatch(row.Id))
        {
            // The list is a snapshot and this window is modeless, so Ctrl+Shift+W behind it can
            // unwatch a row that is still on screen. Say so and re-sync rather than doing nothing
            // visible, which reads as a dead key.
            Announce("That conversation is no longer watched.", AnnouncementCategory.Result);
            Reload();
            return;
        }

        Watches.Remove(row);
        // Land on the row that took its place, or the new last row if it was at the end — never
        // back at the top, which would lose the user's place in a long list.
        SelectedWatch = Watches.Count == 0
            ? null
            : Watches[Math.Min(index, Watches.Count - 1)];

        UpdateStatus();
        Announce($"Stopped watching: {row.Label}", AnnouncementCategory.Result);
        // The main window owns the watch-derived UI state (row stamps, and the watched folder's
        // membership if it is open behind this modeless window). Tell it, exactly as the message
        // window's toggle does, so there is still one place that reacts to a watch changing.
        WatchesChanged?.Invoke(row.NormalizedSubject);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BeginRename()
    {
        if (SelectedWatch == null) return;
        // Capture WHICH watch is being renamed, not just its text. Selection stays reachable while
        // the rename panel is open (F6, then arrows), so re-reading SelectedWatch on save renamed
        // whichever row happened to be selected by then — silently, and to the wrong watch.
        _renamingId = SelectedWatch.Id;
        EditLabel   = SelectedWatch.Label;
        RenameError = string.Empty;
        IsRenaming  = true;
    }

    [RelayCommand]
    private void SaveRename()
    {
        var row = Watches.FirstOrDefault(w => w.Id == _renamingId);
        if (row == null)
        {
            // The watch went away while the edit was open.
            IsRenaming  = false;
            RenameError = string.Empty;
            Announce("That conversation is no longer watched.", AnnouncementCategory.Result);
            Reload();
            return;
        }

        var trimmed = (EditLabel ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            RenameError = "A label cannot be empty.";
            Announce("A label cannot be empty.", AnnouncementCategory.Result);
            return;
        }

        if (!_watchService.Rename(row.Id, trimmed)) return;

        SelectedWatch = row;   // saving is about the renamed row, wherever selection wandered to
        row.Label   = trimmed;
        IsRenaming  = false;
        RenameError = string.Empty;
        Announce($"Renamed to {trimmed}", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming  = false;
        RenameError = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void GoTo()
    {
        var row = SelectedWatch;
        if (row == null) return;

        // A watch with nothing cached has nowhere to go. Say so rather than navigating to an empty
        // list and leaving the user to work out why.
        if (row.MessageCount == 0)
        {
            Announce("That conversation has no cached messages.", AnnouncementCategory.Result);
            return;
        }

        GoToRequested?.Invoke(row.NormalizedSubject);
    }

    private void Announce(string text, AnnouncementCategory category) =>
        AnnouncementRequested?.Invoke(text, category);
}
