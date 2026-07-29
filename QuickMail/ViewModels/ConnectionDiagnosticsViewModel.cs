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

/// <summary>
/// One row in the accounts list: what the app currently shows for an account, and what the last
/// independent probe actually found.
/// </summary>
public sealed class ConnectionAccountRow : ObservableObject
{
    private string _statusText = string.Empty;
    private string _verdictText = string.Empty;

    public required Guid Id { get; init; }
    public required string Label { get; init; }
    public required string Host { get; init; }

    /// <summary>What the account list shows, e.g. "connected" or "disconnected since 2:14 PM".</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>The last probe verdict in plain words, or a note that it has not been probed.</summary>
    public string VerdictText
    {
        get => _verdictText;
        set => SetProperty(ref _verdictText, value);
    }

    /// <summary>
    /// A screen reader reads a list item's accessible name from ToString(), not from the item
    /// template, so the whole row has to be here (see the accessibility checklist in CLAUDE.md).
    /// </summary>
    public override string ToString() => $"{Label}, {StatusText}. {VerdictText}";
}

/// <summary>
/// Backs the Connection Diagnostics window: current per-account status, the live connection
/// journal, an on-demand reachability test, and report export.
/// </summary>
public sealed partial class ConnectionDiagnosticsViewModel : ObservableObject
{
    private readonly ConnectionTruthProbe? _probe;
    private readonly Func<IReadOnlyList<AccountModel>> _accountsSource;

    [NotifyCanExecuteChangedFor(nameof(TestAccountCommand))]
    [ObservableProperty]
    private ConnectionAccountRow? _selectedAccount;

    [ObservableProperty]
    private string _eventFilter = "All accounts";

    [NotifyCanExecuteChangedFor(nameof(TestAccountCommand))]
    [ObservableProperty]
    private bool _isTesting;

    public ObservableCollection<ConnectionAccountRow> Accounts { get; } = new();
    public ObservableCollection<ConnectionEvent> Events { get; } = new();

    /// <summary>Account labels plus "All accounts", for the journal filter.</summary>
    public ObservableCollection<string> FilterOptions { get; } = new();

    /// <summary>
    /// Raised when the VM has text for the clipboard. The View owns the clipboard —
    /// a ViewModel must not touch <c>System.Windows</c> types (CLAUDE.md, MVVM rules).
    /// </summary>
    public event Action<string>? CopyRequested;

    /// <summary>Raised when the user asks to save the report; the View shows the file dialog.</summary>
    public event Action<string>? SaveRequested;

    /// <summary>Raised with text the View should announce, and the category it belongs to.</summary>
    public event Action<string, AnnouncementCategory>? AnnounceRequested;

    public ConnectionDiagnosticsViewModel(
        ConnectionTruthProbe? probe, Func<IReadOnlyList<AccountModel>> accountsSource)
    {
        _probe          = probe;
        _accountsSource = accountsSource;

        Refresh();
    }

    /// <summary>Rebuilds the account rows and the event list from current state.</summary>
    public void Refresh()
    {
        var accounts = _accountsSource();

        var previousSelection = SelectedAccount?.Id;
        Accounts.Clear();
        FilterOptions.Clear();
        FilterOptions.Add("All accounts");

        foreach (var account in accounts)
        {
            var row = new ConnectionAccountRow
            {
                Id    = account.Id,
                Label = account.AccountLabel,
                Host  = account.ImapHost ?? "-",
            };
            UpdateRow(row, account);
            Accounts.Add(row);
            FilterOptions.Add(account.AccountLabel);
        }

        SelectedAccount = Accounts.FirstOrDefault(r => r.Id == previousSelection) ?? Accounts.FirstOrDefault();
        ReloadEvents();
    }

    private void UpdateRow(ConnectionAccountRow row, AccountModel account)
    {
        row.StatusText = account.IsConnected ? "connected" : "disconnected";

        var verdict = _probe?.LastResultFor(account.Id);
        var at      = _probe?.LastResultAtFor(account.Id);

        if (verdict == null)
        {
            row.VerdictText = "Not yet tested.";
            return;
        }

        // An inconclusive probe says so and stops. It must never be phrased as a fault: reporting
        // "the server did not answer" for an account we simply could not test is what made the
        // first live run flag a healthy Microsoft account as broken.
        if (verdict.Inconclusive)
        {
            row.VerdictText = $"Cannot be tested ({verdict.Detail}).";
            return;
        }

        // State the disagreement in plain words. This is the finding, not a footnote — if the
        // account list says disconnected and the server answers fine, that is the whole bug.
        row.VerdictText = (account.IsConnected, verdict.Reachable) switch
        {
            (false, true)  => $"Server answered normally at {at:h:mm:ss tt}. The disconnected status is wrong.",
            (false, false) => $"Confirmed unreachable at {at:h:mm:ss tt}: {verdict.Detail}",
            (true,  true)  => $"Confirmed reachable at {at:h:mm:ss tt}.",
            (true,  false) => $"Shown as connected but the server did not answer at {at:h:mm:ss tt}: {verdict.Detail}",
        };
    }

    /// <summary>Refreshes only the status/verdict text, leaving selection and scroll position alone.</summary>
    public void RefreshStatusOnly()
    {
        var byId = _accountsSource().ToDictionary(a => a.Id);
        foreach (var row in Accounts)
        {
            if (byId.TryGetValue(row.Id, out var account))
                UpdateRow(row, account);
        }
    }

    /// <summary>Reloads the journal list, applying the current account filter, newest first.</summary>
    public void ReloadEvents()
    {
        var all = ConnectionJournal.Snapshot();
        var filtered = EventFilter == "All accounts"
            ? all
            : all.Where(e => e.Account == EventFilter).ToList();

        Events.Clear();
        foreach (var e in filtered.Reverse()) Events.Add(e);
    }

    partial void OnEventFilterChanged(string value) => ReloadEvents();

    /// <summary>Appends one live event if it passes the current filter.</summary>
    public void AppendEvent(ConnectionEvent evt)
    {
        if (EventFilter != "All accounts" && evt.Account != EventFilter) return;

        Events.Insert(0, evt);
        // Keep the visible list bounded; the file and the report hold the full history.
        while (Events.Count > ConnectionJournal.Capacity) Events.RemoveAt(Events.Count - 1);
    }

    /// <summary>A test needs a selected account, a probe, and no test already running.</summary>
    private bool CanTest() => SelectedAccount != null && _probe != null && !IsTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAccountAsync()
    {
        var row = SelectedAccount;
        if (row == null || _probe == null || IsTesting) return;

        IsTesting = true;
        AnnounceRequested?.Invoke($"Testing {row.Label}.", AnnouncementCategory.Status);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await _probe.RunProbeAsync(row.Id, "manual test", cts.Token);

            RefreshStatusOnly();

            var shownDisconnected = !_accountsSource().FirstOrDefault(a => a.Id == row.Id)?.IsConnected ?? false;
            var spoken = result.Inconclusive
                ? $"{row.Label} cannot be tested. {result.Detail}"
                : (shownDisconnected, result.Reachable) switch
                {
                    (true, true)   => $"{row.Label} is reachable. The disconnected status is incorrect.",
                    (true, false)  => $"{row.Label} is not reachable. {result.Detail}",
                    (false, true)  => $"{row.Label} is reachable.",
                    (false, false) => $"{row.Label} did not answer. {result.Detail}",
                };
            AnnounceRequested?.Invoke(spoken, AnnouncementCategory.Result);
        }
        catch (OperationCanceledException)
        {
            AnnounceRequested?.Invoke(
                $"The test of {row.Label} timed out after 45 seconds.", AnnouncementCategory.Result);
        }
        catch (Exception ex)
        {
            ConnectionJournal.RecordError(
                ConnectionEventKind.Probe, row.Label, row.Host, "manual-test-failed", ex);
            AnnounceRequested?.Invoke(
                $"The test of {row.Label} could not run. {ex.Message}", AnnouncementCategory.Result);
        }
        finally
        {
            IsTesting = false;
            ReloadEvents();
        }
    }

    [RelayCommand]
    private void CopyReport()
    {
        var report = BuildReport();
        CopyRequested?.Invoke(report);
        AnnounceRequested?.Invoke(
            $"Report copied. {Events.Count} events.", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void SaveReport() => SaveRequested?.Invoke(BuildReport());

    [RelayCommand]
    private void RefreshNow()
    {
        Refresh();
        AnnounceRequested?.Invoke("Refreshed.", AnnouncementCategory.Result);
    }

    private string BuildReport()
    {
        var accounts = _accountsSource();
        var summaries = new List<string>();

        if (_probe != null)
        {
            summaries.AddRange(_probe.SummaryLines(
                accounts.Select(a => (a.Id, a.AccountLabel, a.IsConnected))));
        }
        else
        {
            summaries.AddRange(accounts.Select(a =>
                $"{a.AccountLabel} — shown as {(a.IsConnected ? "connected" : "DISCONNECTED")}; probe unavailable"));
        }

        // Which hosts share an address is the fact most likely to be missed by a reader who does not
        // already know to look for it, so it goes in the report rather than only in the event lines.
        summaries.Add(string.Empty);
        summaries.Add("Hosts and live socket counts:");
        summaries.AddRange(HostConnectionCensus.SnapshotLines().Select(l => "  " + l));

        return ConnectionJournal.BuildReport(summaries);
    }
}
