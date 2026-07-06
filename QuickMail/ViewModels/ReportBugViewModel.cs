using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Backs the "Report a Bug" window. Holds only what the user typed plus non-sensitive
/// auto-collected metadata — never reads the log file (see
/// docs/planning/bug-reporting-pm-dev-spec.md §4.2, an explicit product decision, not an
/// oversight to "improve" later without a new design pass).
/// </summary>
public partial class ReportBugViewModel : ObservableObject, IDisposable
{
    private readonly IBugReportService _bugReportService;
    private CancellationTokenSource? _sendCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _whatHappened = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _whatExpected = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _stepsToReproduce = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private bool _isSending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelButtonLabel))]
    private bool _isSent;

    [ObservableProperty]
    private string? _issueUrl;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool CanSend => !IsSending;

    /// <summary>"Cancel" before a send succeeds, "Close" afterward — one button, state-reflecting label.</summary>
    public string CancelButtonLabel => IsSent ? "Close" : "Cancel";

    /// <summary>Exactly what will be submitted — shown read-only before the user sends.</summary>
    public string PreviewText => _bugReportService.BuildReportText(BuildModel());

    public event Action<string, AnnouncementCategory>? AnnouncementRequested;
    public event EventHandler? CloseRequested;

    /// <summary>Fired after a successful send — the View moves focus to the issue-link control.</summary>
    public event EventHandler? SendSucceeded;

    /// <summary>Fired after a failed send — the View moves focus to the fallback button.</summary>
    public event EventHandler? SendFailed;

    public ReportBugViewModel(IBugReportService bugReportService)
    {
        _bugReportService = bugReportService;
    }

    private BugReportModel BuildModel() => new()
    {
        Summary          = Summary,
        WhatHappened     = WhatHappened,
        WhatExpected     = WhatExpected,
        StepsToReproduce = StepsToReproduce,
    };

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Summary))
        {
            AnnouncementRequested?.Invoke("Enter a summary before sending.", AnnouncementCategory.Result);
            return;
        }

        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();

        IsSending     = true;
        StatusMessage = "Sending report…";
        AnnouncementRequested?.Invoke(StatusMessage, AnnouncementCategory.Status);

        var result = await _bugReportService.SubmitAsync(BuildModel(), _sendCts.Token);

        IsSending = false;

        if (result.Success)
        {
            IsSent        = true;
            IssueUrl      = result.IssueUrl;
            StatusMessage = $"Report sent. Issue created: {result.IssueUrl}.";
            AnnouncementRequested?.Invoke(StatusMessage, AnnouncementCategory.Result);
            SendSucceeded?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            StatusMessage = "Could not send the report automatically. Your report is ready to copy.";
            AnnouncementRequested?.Invoke(StatusMessage, AnnouncementCategory.Result);
            SendFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void CopyAndOpen()
    {
        if (string.IsNullOrWhiteSpace(Summary))
        {
            AnnouncementRequested?.Invoke("Enter a summary before sending.", AnnouncementCategory.Result);
            return;
        }

        var model = BuildModel();
        Clipboard.SetText($"{Summary}\n\n{_bugReportService.BuildReportText(model)}");
        StatusMessage = "Report copied. Opening GitHub in your browser.";
        AnnouncementRequested?.Invoke(StatusMessage, AnnouncementCategory.Result);
        ExternalUriPolicy.TryOpenExternal(_bugReportService.BuildFallbackUrl(model));
    }

    [RelayCommand]
    private void OpenIssue()
    {
        if (!string.IsNullOrEmpty(IssueUrl))
            ExternalUriPolicy.TryOpenExternal(IssueUrl);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Cancels any in-flight Send so it doesn't outlive the window. Called from OnClosed.</summary>
    public void Dispose()
    {
        _sendCts?.Cancel();
        _sendCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
