using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// The one-time attachment-flag pass (#931). IMAP summaries now carry BODYSTRUCTURE, so mail synced from then on knows
/// whether it has attachments; mail cached before reads as having none until it is checked. This asks the server for
/// the BODYSTRUCTURE alone of each unchecked row, a batch at a time, after the startup sync, and records the answer.
/// What it does not reach — an account that is not connected, a server that refuses — stays unchecked for the next
/// launch; once every row is checked there is nothing left to ask and the pass costs one query per account.
/// </summary>
public partial class SyncService
{
    internal const int AttachmentFlagFetchBatch = 500;

    /// <summary>Pause between requests, so a large cache is worked through gently. Zero in tests.</summary>
    internal TimeSpan AttachmentFlagPacing { get; set; } = TimeSpan.FromSeconds(1);

    public event Action<Guid, string, IReadOnlyCollection<string>>? AttachmentFlagsFound;

    private static readonly IReadOnlyDictionary<string, string> NoneFound = new Dictionary<string, string>();

    // Internal so tests can run the pass without a whole startup sync around it.
    internal async Task BackfillAttachmentFlagsAsync(List<AccountModel> accounts, CancellationToken ct)
    {
        if (_probeMode) return;
        var firstRequest = true;
        var checkedCount = 0;
        var flaggedCount = 0;

        foreach (var account in accounts.Where(a => a.BackendKind == BackendKind.ImapSmtp))
        {
            if (_connectivity != null && !_connectivity.IsAccountOnline(account.Id)) continue;

            foreach (var (folder, ids) in await _store.GetMessageIdsWithUncheckedAttachmentsAsync(account.Id))
            {
                // An id that is not an IMAP UID can never be fetched, and one in a request fails the whole request:
                // recorded as checked, with nothing found, so it is not asked about again.
                var notUids = ids.Where(id => !IsImapUid(id)).ToList();
                if (notUids.Count > 0)
                    await _store.UpdateAttachmentFlagsBatchAsync(account.Id, folder, notUids, NoneFound);
                var uids = ids.Where(IsImapUid).ToList();

                var stopAccount = false;
                for (var start = 0; start < uids.Count; start += AttachmentFlagFetchBatch)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!firstRequest && AttachmentFlagPacing > TimeSpan.Zero) await Task.Delay(AttachmentFlagPacing, ct);
                    firstRequest = false;

                    var batch = uids.GetRange(start, Math.Min(AttachmentFlagFetchBatch, uids.Count - start));
                    try
                    {
                        var flags = await _imap.FetchAttachmentFlagsAsync(account.Id, folder, batch, ct);
                        var found = flags.Where(f => f.Value.HasAttachments)
                                         .ToDictionary(f => f.Key, f => f.Value.InternetMessageId, StringComparer.Ordinal);
                        // Every id asked about is checked, including any the server no longer has: the sync removes
                        // those, and asking again next launch would get the same answer.
                        var flagged = await _store.UpdateAttachmentFlagsBatchAsync(account.Id, folder, batch, found);
                        flaggedCount += flagged.Count;
                        checkedCount += batch.Count;
                        if (flagged.Count > 0)
                        {
                            var (acct, fn, list) = (account.Id, folder, (IReadOnlyCollection<string>)flagged);
                            _ui.Post(() => AttachmentFlagsFound?.Invoke(acct, fn, list));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ConnectionFailure.IsConnectionFailure(ex, ct))
                    {
                        _connectivity?.NoteAccountUnreachable(account.Id, "attachment-flag pass");
                        LogService.Log($"Attachment-flag pass {account.AccountLabel}: server unreachable, stopping", ex);
                        stopAccount = true;
                        break;
                    }
                    catch (Exception ex) when (IsPermanentlyUnfetchable(ex))
                    {
                        // The folder is gone from the server: its rows can never be checked, so they are recorded as
                        // checked rather than asked about every launch.
                        LogService.Log($"Attachment-flag pass {account.AccountLabel}/{folder}: folder gone, skipped for good", ex);
                        await _store.UpdateAttachmentFlagsBatchAsync(account.Id, folder, uids, NoneFound);
                        break;
                    }
                    catch (MailKit.Net.Imap.ImapCommandException ex)
                    {
                        // This folder refused (one that cannot be selected, say): skipped for this launch only, so
                        // the account's other folders are still checked rather than blocked behind it every launch.
                        LogService.Log($"Attachment-flag pass {account.AccountLabel}/{folder}: refused, skipped this launch", ex);
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Refused (throttling, a sign-in that needs renewing): left for the next launch, and the
                        // account is not pressed further now.
                        LogService.Log($"Attachment-flag pass {account.AccountLabel}/{folder}: will try again next launch", ex);
                        stopAccount = true;
                        break;
                    }
                }
                if (stopAccount) break;
            }
        }

        if (checkedCount > 0)
            LogService.Log($"Attachment-flag pass: {checkedCount} cached message(s) checked, {flaggedCount} with attachments.");
    }
}
