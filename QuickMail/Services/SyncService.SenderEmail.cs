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
/// address is already on disk; what is left is IMAP rows holding a display name alone. For exactly those this
/// fetches the From header — nothing else — and writes only from_email: no row is added or removed, read state
/// and flags are untouched, and rules do not run, since nothing arrives. It is paced, and it reports its
/// progress, because on a large cache it is real traffic the user did not ask for.
/// </summary>
public partial class SyncService
{
    /// <summary>Ids per server request.</summary>
    internal const int SenderEmailFetchBatch = 500;

    /// <summary>
    /// The pause between requests: this is background catching-up, and a burst of back-to-back fetches is what
    /// makes a server throttle a client. Settable so tests do not pay it.
    /// </summary>
    internal TimeSpan SenderEmailPacing { get; set; } = TimeSpan.FromSeconds(1);

    private Action? _senderEmailBackfillDone;

    public event Action<int, int>? SenderEmailBackfillProgressChanged;
    public event Action<int, int>? SenderEmailBackfillCompleted;

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

        // Plan first, so the progress total is the whole pass rather than one account at a time.
        var complete = true;
        var work = new List<(AccountModel Account, string Folder, List<string> Ids)>();
        foreach (var account in accounts.Where(a => a.BackendKind == BackendKind.ImapSmtp && !a.IsShared))
        {
            if (_connectivity != null && !_connectivity.IsAccountOnline(account.Id)) { complete = false; continue; }
            foreach (var (folder, ids) in await _store.GetMessageIdsMissingSenderEmailAsync(account.Id))
                work.Add((account, folder, ids));
        }

        var total = work.Sum(w => w.Ids.Count);
        var done = 0;
        var filled = 0;
        var lost = new HashSet<Guid>();
        var firstRequest = true;
        foreach (var (account, folder, ids) in work)
        {
            if (lost.Contains(account.Id)) continue;
            for (var start = 0; start < ids.Count; start += SenderEmailFetchBatch)
            {
                ct.ThrowIfCancellationRequested();
                if (!firstRequest && SenderEmailPacing > TimeSpan.Zero) await Task.Delay(SenderEmailPacing, ct);
                firstRequest = false;

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
                    lost.Add(account.Id);
                    complete = false;
                    break;
                }
                catch (Exception ex)
                {
                    // A folder deleted on the server, say. Retrying it every launch would never succeed, so it is
                    // logged and skipped; its rows keep matching on the display name, as they always did.
                    LogService.Log($"Sender-address backfill {account.AccountLabel}/{folder}: skipped", ex);
                    break;
                }
                finally
                {
                    done += batch.Count;
                }

                if (done < total)
                {
                    var (d, t) = (done, total);
                    _ui.Post(() => SenderEmailBackfillProgressChanged?.Invoke(d, t));
                }
            }
        }

        if (total > 0)
        {
            LogService.Log($"Sender-address backfill: {filled} of {total} filled from the server");
            _ui.Post(() => SenderEmailBackfillCompleted?.Invoke(filled, total));
        }
        if (complete) onComplete();
        else LogService.Log("Sender-address backfill: not every IMAP account was reachable; it continues next launch.");
    }
}
