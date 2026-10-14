using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// The server half of the one-time sender-address backfill (#803). The local half
/// (<see cref="ILocalStoreService.BackfillSenderEmailsLocallyAsync"/>, run at startup) fills every row whose
/// address is already on disk; what is left is IMAP rows holding a display name alone, and those need their
/// ENVELOPE once more. This fetches it for exactly those rows and writes only from_email: no row is added or
/// removed, read state and flags are untouched, and rules do not run, since nothing arrives.
/// </summary>
public partial class SyncService
{
    /// <summary>Ids per server request: one ENVELOPE fetch, the size of a sync's.</summary>
    internal const int SenderEmailFetchBatch = 500;

    private Action? _senderEmailBackfillDone;

    /// <summary>
    /// Asks the next post-sync trickle to fetch the addresses of IMAP rows still lacking one, calling
    /// <paramref name="onComplete"/> once every IMAP account has been worked through. When an account cannot be
    /// reached it is not called, and the caller asks again at the next launch.
    /// </summary>
    public void RequestSenderEmailBackfill(Action onComplete) => _senderEmailBackfillDone = onComplete;

    // Internal so tests can run the pass without a whole startup sync around it.
    internal async Task BackfillSenderEmailsFromServerAsync(List<AccountModel> accounts, CancellationToken ct)
    {
        if (_probeMode) return;
        // Taken, not read: one pass per request, so the periodic sweep does not repeat it.
        var onComplete = Interlocked.Exchange(ref _senderEmailBackfillDone, null);
        if (onComplete is null) return;

        var complete = true;
        foreach (var account in accounts.Where(a => a.BackendKind == BackendKind.ImapSmtp && !a.IsShared))
        {
            if (_connectivity != null && !_connectivity.IsAccountOnline(account.Id)) { complete = false; continue; }
            if (!await BackfillAccountSenderEmailsAsync(account, ct)) complete = false;
        }

        if (complete) onComplete();
        else LogService.Log("Sender-address backfill: not every IMAP account was reachable; it continues next launch.");
    }

    /// <returns>False when the account's server went away part-way, so the pass has to run again.</returns>
    private async Task<bool> BackfillAccountSenderEmailsAsync(AccountModel account, CancellationToken ct)
    {
        var missing = await _store.GetMessageIdsMissingSenderEmailAsync(account.Id);
        if (missing.Count == 0) return true;

        var filled = 0;
        foreach (var (folder, ids) in missing)
        {
            for (var start = 0; start < ids.Count; start += SenderEmailFetchBatch)
            {
                ct.ThrowIfCancellationRequested();
                var batch = ids.GetRange(start, Math.Min(SenderEmailFetchBatch, ids.Count - start));
                try
                {
                    var emails = await _imap.FetchSenderEmailsAsync(account.Id, folder, batch, ct);
                    await _store.UpdateSenderEmailsBatchAsync(account.Id, folder, emails.Select(e => (e.Key, e.Value)));
                    filled += emails.Count;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ConnectionFailure.IsConnectionFailure(ex, ct))
                {
                    _connectivity?.NoteAccountUnreachable(account.Id, "sender-address backfill");
                    LogService.Log($"Sender-address backfill {account.AccountLabel}: server unreachable, stopping", ex);
                    return false;
                }
                catch (Exception ex)
                {
                    // A folder deleted on the server, say. Retrying it every launch would never succeed, so it is
                    // logged and skipped; its rows keep matching on the display name, as they always did.
                    LogService.Log($"Sender-address backfill {account.AccountLabel}/{folder}: skipped", ex);
                    break;
                }
            }
        }
        LogService.Log($"Sender-address backfill {account.AccountLabel}: {filled} of {missing.Sum(m => m.Value.Count)} filled from the server");
        return true;
    }
}
