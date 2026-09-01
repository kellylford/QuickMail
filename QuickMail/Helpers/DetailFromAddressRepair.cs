using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Fills the sender's address back into a cache-served message detail that has only a display name.
/// <para>
/// Detail rows written before the <c>from_addr</c> column existed (issue #636) had no From of their
/// own: <c>LocalStoreService.LoadDetailAsync</c> took it from <c>MessageSummary.from_disp</c>, which
/// holds the display name alone because that is what the message list's From column wants. So the
/// same message showed "Kelly Ford &lt;kelly@example.com&gt;" when opened straight from the server
/// and "Kelly Ford" when served from cache — and a reply started from the cached copy addressed a
/// bare name the address box could not turn into a chip.
/// </para>
/// <para>
/// The address is stored nowhere else in the database, so such a row can only be repaired by
/// re-fetching it. Lives here rather than on a view model because both cache-first load paths need
/// it: the reading pane and tabs (MainViewModel) and standalone windows (MessageWindow).
/// </para>
/// </summary>
public static class DetailFromAddressRepair
{
    /// <summary>
    /// Returns <paramref name="detail"/> unchanged when its From already carries an address, and a
    /// freshly fetched, re-cached copy when it does not.
    /// <para>
    /// An empty From is left alone: the message had no From header to recover, and a detail whose
    /// summary row is gone — a deleted message whose cached body still backs a calendar event —
    /// reads as empty here, so re-fetching it would fail on every open.
    /// </para>
    /// <para>
    /// The cached detail is returned when the fetch fails, so a message deleted from the server, or
    /// one on a POP3 account where the cache is the only copy, still opens. A name-only From is
    /// worse than a full one and far better than an empty message.
    /// </para>
    /// </summary>
    /// <param name="background">Use the background IMAP lease, for prefetch. Foreground fetches also
    /// mark the message read, which is correct when the user is opening it and wrong when they are
    /// not.</param>
    public static async Task<MailMessageDetail> RepairAsync(
        MailMessageDetail detail, ILocalStoreService store, IMailService mail,
        bool background, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(detail.From) || detail.From.Contains('@', StringComparison.Ordinal))
            return detail;

        try
        {
            var fresh = background
                ? await mail.PrefetchMessageDetailAsync(
                      detail.AccountId, detail.FolderName, detail.MessageId, ct)
                : await mail.GetMessageDetailAsync(
                      detail.AccountId, detail.FolderName, detail.MessageId, ct);
            await store.UpsertDetailAsync(fresh);
            return fresh;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Log($"DetailFromAddressRepair {detail.FolderName}/{detail.MessageId}", ex);
            return detail;
        }
    }
}
