using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.ViewModels;

/// <summary>
/// What the one-time sender-address backfill (#803) shows the user. It fetches from the server for IMAP mail
/// cached before the address was kept, which on a large cache is traffic nobody asked for, so it is never
/// invisible: its progress has its own region of the status bar while it runs. Nothing is spoken — it is a
/// one-time background job the user did not start, and one that finishes at startup is not heard anyway — and the
/// region is cleared the moment the pass ends, however it ends; the result goes to the log (review of #806).
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// The status bar's word on the backfill while it runs. Its own region rather than the main status text, which
    /// it would otherwise overwrite (the message count) and could leave stuck there if the pass stopped early.
    /// Empty, and so hidden, at any other time.
    /// </summary>
    [ObservableProperty]
    private string _senderAddressStatusText = string.Empty;

    internal static string DescribeSenderEmailBackfill(int done, int total)
        => string.Create(CultureInfo.CurrentCulture, $"Updating sender addresses: {done:N0} of {total:N0}");

    private void OnSenderEmailBackfillProgress(int done, int total)
        => SenderAddressStatusText = DescribeSenderEmailBackfill(done, total);

    private void OnSenderEmailBackfillEnded(int filled, int planned)
        => SenderAddressStatusText = string.Empty;
}
