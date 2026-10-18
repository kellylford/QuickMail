namespace QuickMail.Services;

/// <summary>
/// The contract between the fixture generator (tools/QuickMail.Fixtures) and the
/// probe driver (#180): the driver selects fixture content by these identifiers,
/// so they live in one place both sides reference. Changing a value here changes
/// what the generator writes AND what the driver looks for — they cannot drift.
/// </summary>
public static class UiProbeFixture
{
    /// <summary>The single fixture account.</summary>
    public static readonly System.Guid AccountId = new("11111111-1111-1111-1111-111111111111");

    public const string InboxFolder = "INBOX";
    public const string SentFolder = "Sent";

    /// <summary>The HTML-bodied message the reading-pane probe renders.</summary>
    public const string HtmlMessageId = "1002";
    public const string HtmlMessageSubject = "Weekly digest: headings, lists, links";

    /// <summary>
    /// The message with an attachment, which the reading-pane-attachment probe renders so the
    /// attachment row (file name plus its visible size, issue #825) is captured.
    /// </summary>
    public const string AttachmentMessageId = "1004";

    /// <summary>
    /// A file name long enough to overflow an attachment row, so the probe shows it trimming
    /// with an ellipsis while the size stays visible beside it (issue #825).
    /// </summary>
    public const string LongAttachmentName =
        "Quarterly-financial-report-Q4-final-revised-with-appendices-and-board-comments-v7.pdf";

    /// <summary>Fixed clock: every fixture timestamp derives from this instant.</summary>
    public static readonly System.DateTimeOffset T0 = new(2026, 1, 15, 9, 0, 0, System.TimeSpan.Zero);
}
