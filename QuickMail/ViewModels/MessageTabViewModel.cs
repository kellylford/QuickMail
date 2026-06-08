using System;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Tab view model for an open message.
/// Tracks the message summary and (once loaded) the detail.
/// The reading pane in MainWindow re-renders whenever this tab is activated.
/// </summary>
public sealed partial class MessageTabViewModel : TabSessionViewModel
{
    private const int MaxTitleLength = 60;

    public MailMessageSummary Summary { get; }

    [ObservableProperty]
    private MailMessageDetail? _detail;

    [ObservableProperty]
    private bool _isLoaded;

    public DateTimeOffset OpenedAt { get; } = DateTimeOffset.Now;
    public string? SourceFolderName { get; init; }
    public Guid AccountId { get; init; }

    public MessageTabViewModel(MailMessageSummary summary)
        : base(new TabSessionModel
        {
            Kind       = TabKind.Message,
            ContentKey = summary.MessageId,
            Title      = TruncateTitle(summary.Subject),
            Tooltip    = summary.Subject ?? string.Empty,
        })
    {
        Summary = summary;
        Title   = Model.Title;
    }

    partial void OnDetailChanged(MailMessageDetail? value)
    {
        if (value != null)
        {
            IsLoaded = true;
            Title    = TruncateTitle(value.Subject ?? Summary.Subject);
            Model.Tooltip = value.Subject ?? Summary.Subject ?? string.Empty;
        }
    }

    private static string TruncateTitle(string? raw)
    {
        var s = string.IsNullOrWhiteSpace(raw) ? "Untitled" : raw.Trim();
        return s.Length > MaxTitleLength ? s[..MaxTitleLength] + "…" : s;
    }
}
