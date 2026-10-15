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
/// address is already on disk; what is left is IMAP rows with no cached body, including any whose display text
/// looks like an address, since a sender can make their name look like one. For exactly those this
/// fetches the From and Message-ID headers — nothing else — and writes only from_email, and only where the
/// Message-ID shows the stored id still names the cached message: no row is added or removed, read state
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

    /// <summary>
    /// The IMAP accounts the requested pass still has to work through. Fixed when the pass is requested, from every
    /// IMAP account the profile has — not from the accounts the sync is handed, which are the connected ones only, so
    /// an account that could not connect at startup is still owed its pass (review of #806).
    /// </summary>
    private HashSet<Guid> _senderEmailPending = [];

    public event Action<int, int>? SenderEmailBackfillProgressChanged;
    public event Action<int, int>? SenderEmailBackfillCompleted;

    /// <summary>
    /// Asks the post-sync trickle to fetch the addresses of IMAP rows still lacking one, calling
    /// <paramref name="onComplete"/> once every account in <paramref name="imapAccountIds"/> has been worked through.
    /// An account the pass did not reach — not connected at startup, offline, or dropped part-way — leaves it
    /// uncalled, and the caller asks again at the next launch: the startup sync is the trickle's only caller.
    /// </summary>
    public void RequestSenderEmailBackfill(IReadOnlyCollection<Guid> imapAccountIds, Action onComplete)
    {
        _senderEmailPending = [.. imapAccountIds];
        _senderEmailBackfillDone = onComplete;
    }

    // Internal so tests can run the pass without a whole startup sync around it.
    internal async Task BackfillSenderEmailsFromServerAsync(List<AccountModel> accounts, CancellationToken ct)
    {
        if (_probeMode) return;
        // Taken, not read: the request is for one pass. The startup sync is the only caller, so whatever this pass
        // leaves undone waits for the request the next launch makes.
        var onComplete = Interlocked.Exchange(ref _senderEmailBackfillDone, null);
        if (onComplete is null) return;
        var pending = _senderEmailPending;

        // Plan first, so the progress total is the whole pass rather than one account at a time. Only the accounts
        // the request named, and only those the sync was handed (connected) and still believes online; every other
        // one stays pending.
        var reached = new List<AccountModel>();
        var work = new List<(AccountModel Account, string Folder, List<string> Ids)>();
        foreach (var account in accounts.Where(a => pending.Contains(a.Id)))
        {
            if (_connectivity != null && !_connectivity.IsAccountOnline(account.Id)) continue;
            reached.Add(account);
            foreach (var (folder, ids) in await _store.GetMessageIdsMissingSenderEmailAsync(account.Id))
                work.Add((account, folder, ids));
        }

        var total = work.Sum(w => w.Ids.Count);
        var done = 0;
        var filled = 0;
        // Accounts this pass stopped on before it was through: the rest of their folders are left for the next launch.
        var unfinished = new HashSet<Guid>();
        var firstRequest = true;
        foreach (var (account, folder, ids) in work)
        {
            if (unfinished.Contains(account.Id)) continue;
            for (var start = 0; start < ids.Count; start += SenderEmailFetchBatch)
            {
                ct.ThrowIfCancellationRequested();
                if (!firstRequest && SenderEmailPacing > TimeSpan.Zero) await Task.Delay(SenderEmailPacing, ct);
                firstRequest = false;

                var batch = ids.GetRange(start, Math.Min(SenderEmailFetchBatch, ids.Count - start));
                try
                {
                    var fetched = await _imap.FetchSenderEmailsAsync(account.Id, folder, batch, ct);
                    var written = await _store.UpdateSenderEmailsBatchAsync(account.Id, folder,
                        fetched.Select(f => (f.Key, f.Value.FromEmail, f.Value.InternetMessageId)));
                    filled += written;
                    // Not written, almost always: the server's message under that id has another Message-ID, because
                    // the folder was renumbered after it was cached. Asking again next launch would get the same
                    // answer, so these rows keep matching on the display name, as they always did.
                    if (written < fetched.Count)
                        LogService.Log($"Sender-address backfill {account.AccountLabel}/{folder}: {fetched.Count - written} not written; their ids may now name other messages");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ConnectionFailure.IsConnectionFailure(ex, ct))
                {
                    _connectivity?.NoteAccountUnreachable(account.Id, "sender-address backfill");
                    LogService.Log($"Sender-address backfill {account.AccountLabel}: server unreachable, stopping", ex);
                    unfinished.Add(account.Id);
                    break;
                }
                catch (Exception ex) when (IsPermanentlyUnfetchable(ex))
                {
                    // The folder is gone from the server, or holds an id no IMAP fetch can name. Retrying it every
                    // launch would never succeed, so it is skipped for good and the account can still finish; its
                    // rows keep matching on the display name, as they always did.
                    LogService.Log($"Sender-address backfill {account.AccountLabel}/{folder}: skipped for good", ex);
                    break;
                }
                catch (Exception ex)
                {
                    // The server answered but refused: Gmail throttling (NO [UNAVAILABLE]), a sign-in that needs
                    // renewing, and the like. These pass, so the account is left for the next launch rather than
                    // its folder dropped for good — and the pass stops asking it now rather than pressing on into a
                    // server that is pushing back (review of #806).
                    LogService.Log($"Sender-address backfill {account.AccountLabel}/{folder}: will try again next launch", ex);
                    unfinished.Add(account.Id);
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

        // An account is done once the pass reached it and went through all its folders: each one fetched, or skipped
        // for good because it can never be fetched.
        foreach (var account in reached)
            if (!unfinished.Contains(account.Id)) pending.Remove(account.Id);

        if (pending.Count == 0) onComplete();
        else LogService.Log($"Sender-address backfill: {pending.Count} IMAP account(s) not worked through; it continues next launch.");
    }

    /// <summary>
    /// The failures that will happen again however often the fetch is retried: a folder deleted on the server, or a
    /// stored id that is not an IMAP UID (non-numeric, or out of range). Everything else — a refusal, throttling, an
    /// expired sign-in — can pass, so it leaves the account for the next launch instead (review of #806).
    /// </summary>
    internal static bool IsPermanentlyUnfetchable(Exception ex) =>
        ex is MailKit.FolderNotFoundException or FormatException or OverflowException;
}
