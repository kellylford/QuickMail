using System.Globalization;

namespace QuickMail.ViewModels;

/// <summary>
/// What the one-time sender-address backfill (#803) tells the user. It fetches from the server for IMAP mail
/// cached before the address was kept, which on a large cache is traffic nobody asked for, so it is never
/// invisible: progress and the result go to the status bar. Nothing is spoken — it is a one-time background job
/// the user did not start, and one that finishes at startup is not heard anyway (review of #806).
/// </summary>
public partial class MainViewModel
{
    internal static string DescribeSenderEmailBackfill(int done, int total)
        => string.Create(CultureInfo.CurrentCulture, $"Updating sender addresses: {done:N0} of {total:N0}");

    internal static string? SenderEmailBackfillFinished(int filled)
        => filled switch
        {
            <= 0 => null,
            1    => "Updated the sender address of 1 message.",
            _    => string.Create(CultureInfo.CurrentCulture, $"Updated sender addresses for {filled:N0} messages."),
        };

    private void OnSenderEmailBackfillProgress(int done, int total)
        => SetStatusSilently(DescribeSenderEmailBackfill(done, total));

    /// <summary>
    /// The result, shown and not spoken. A pass that filled nothing (the server went away first) shows nothing;
    /// the offline status covers that.
    /// </summary>
    private void OnSenderEmailBackfillCompleted(int filled, int planned)
    {
        if (SenderEmailBackfillFinished(filled) is not { } text) return;
        SetStatusSilently(text);
    }
}
