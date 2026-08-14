using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Pop3;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// <see cref="IMailService"/> implementation for POP3/SMTP accounts (#128). Send goes through the
/// ordinary <see cref="SmtpService"/> path — only receive is POP3.
///
/// <para>How it differs from <see cref="ImapMailService"/>, and why the difference is structural
/// rather than incidental:</para>
/// <list type="bullet">
/// <item>POP3 has one mailbox drop, no folders, no server-side per-message state, and no way to
/// fetch part of a message. So every message is downloaded whole at sync time and everything the
/// user does to it afterwards — read/unread, flags, move, delete-to-Trash — is local.</item>
/// <item><b>The local store is the only copy.</b> Once a message is collected (and, with
/// leave-on-server off, dropped from the server) nothing can fetch it again. That single fact drives
/// the sync contract below: no code path may delete cached mail because a server listing did not
/// mention it.</item>
/// <item>Four synthetic folders are exposed — Inbox, Sent, Drafts, Trash — and only the Inbox is
/// backed by the server. Folder CRUD throws <see cref="NotSupportedException"/>; callers gate on
/// <see cref="BackendKind.Pop3Smtp"/> rather than catching.</item>
/// </list>
///
/// <para><b>The sync contract (#462).</b> <c>SyncService</c> routes any folder whose max message key
/// is "0" — which is every POP3 folder, since UIDLs are not numeric — through its id-diff sweep. That
/// sweep asks a backend three things, and each answer here is chosen so the sweep behaves correctly
/// against a mailbox the server keeps deleting from:</para>
/// <list type="number">
/// <item><see cref="GetFolderMessageIdDatesAsync"/> returns the UNION of the server's UIDLs and the
/// ids already cached. The union is what makes the sweep's deletion reconcile a no-op: cached ids
/// are never "missing from the server", so collected mail is never purged. Cached rows report their
/// cached read state (so the read reconcile is a no-op too — POP3 has no server-side read state to
/// reconcile against), and UIDLs we have never seen are reported as arriving now, which is what
/// tells the sweep there is something to fetch.</item>
/// <item><see cref="GetMessagesSinceDateAsync"/> is therefore the download trigger, not a store
/// read: the sweep calls it when the listing showed something new, and the messages it returns are
/// what reaches rules, the message list and the new-mail toast.</item>
/// <item><see cref="GetFolderMessageIdsAsync"/> answers from the store alone, so the older
/// <c>ReconcileFolderAsync</c> path (taken when a server happens to issue numeric UIDLs, giving the
/// folder a real high-water mark) also diffs the cache against itself and deletes nothing.</item>
/// </list>
/// </summary>
public class Pop3MailService : IMailService
{
    internal const string InboxFolder  = "Inbox";
    internal const string SentFolder   = "Sent";
    internal const string DraftsFolder = "Drafts";
    internal const string TrashFolder  = "Trash";

    /// <summary>Prefix for the synthetic ids of locally-authored messages (sent mail, drafts).
    /// A real UIDL never collides with it, and <see cref="PermanentlyDeleteBatchAsync"/> uses it to
    /// know an id was never on the server.</summary>
    internal const string LocalIdPrefix = "local-";

    private readonly ILocalStoreService _store;
    private readonly bool _onlineMode;

    private readonly ConcurrentDictionary<Guid, AccountModel> _accounts  = new();
    private readonly ConcurrentDictionary<Guid, string>       _passwords = new();

    /// <summary>
    /// One POP3 session per account at a time. Not an optimization: RFC 1939 gives the server an
    /// exclusive-access lock on the maildrop for the life of a session, so a second concurrent
    /// connection is refused outright by most servers. The sweep can call the listing and the
    /// download back to back, so this is load-bearing.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    private bool _disposed;

    public Pop3MailService(ILocalStoreService store, bool onlineMode = false)
    {
        _store      = store;
        _onlineMode = onlineMode;
    }

    // ── Connect / Disconnect ──────────────────────────────────────────────────

    public async Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                $"POP3 account {account.AuthUsername} has no password. POP3 accounts authenticate with a password; " +
                "OAuth is not offered for this backend.");

        // Probe first, register second, so a failed sign-in does not leave the account looking
        // connected to IsConnected (and so the router never dispatches work at a bad credential).
        using (var client = await OpenConnectionAsync(account, password, ct))
            await client.DisconnectAsync(quit: false, ct);

        _accounts[account.Id]  = account;
        _passwords[account.Id] = password;
        _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
    }

    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default)
    {
        _accounts.TryRemove(accountId, out _);
        _passwords.TryRemove(accountId, out _);
        if (_locks.TryRemove(accountId, out var sem))
            sem.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// True once <see cref="ConnectAsync"/> has authenticated successfully. POP3 holds no persistent
    /// connection — a session is opened per operation — so registration, not a live socket, is the
    /// analogue of IMAP's connection pool.
    /// </summary>
    public bool IsConnected(Guid accountId) => _accounts.ContainsKey(accountId);

    // ── Folders ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The four synthetic folders, with counts read from the local store so the folder tree shows
    /// real unread numbers (there is no server-side STATUS to ask).
    /// </summary>
    public async Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        var folders = new List<MailFolderModel>
        {
            new() { AccountId = accountId, FullName = InboxFolder,  DisplayName = "Inbox",  Kind = SpecialFolderKind.Inbox },
            new() { AccountId = accountId, FullName = SentFolder,   DisplayName = "Sent",   Kind = SpecialFolderKind.Sent,   ExcludeFromAllMail = true },
            new() { AccountId = accountId, FullName = DraftsFolder, DisplayName = "Drafts", Kind = SpecialFolderKind.Drafts, ExcludeFromAllMail = true },
            new() { AccountId = accountId, FullName = TrashFolder,  DisplayName = "Trash",  Kind = SpecialFolderKind.Trash,  ExcludeFromAllMail = true },
        };

        if (_onlineMode) return folders;

        foreach (var f in folders)
        {
            try
            {
                var readStates = await _store.LoadFolderReadStatesAsync(accountId, f.FullName);
                f.MessageCount = readStates.Count;
                f.UnreadCount  = readStates.Count(kv => !kv.Value);
            }
            catch (Exception ex)
            {
                // Counts are cosmetic; a store hiccup must not cost the user their folder list.
                LogService.Log($"POP3: failed to count {f.FullName} for account {accountId}", ex);
            }
        }
        return folders;
    }

    // ── Message fetch ─────────────────────────────────────────────────────────

    public async Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
        => await LoadFromStoreAsync(accountId, folderName, maxMessages);

    /// <summary>
    /// The sweep's fetch, and therefore the download trigger — see the sync contract on the class.
    /// Collects anything new from the server first, then returns the folder's messages inside the
    /// window so the sweep surfaces them through the ordinary arrivals path (rules, list, toast).
    /// <para>Mail older than the window is still downloaded and stored; it is simply not announced
    /// as an arrival. That matters on a first sync of a mailbox holding a long backlog: the user
    /// gets all of it in the Inbox, without a toast per message.</para>
    /// </summary>
    public async Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(
        Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
    {
        if (IsInbox(folderName))
            await DownloadNewMessagesAsync(accountId, ct);

        var all = await LoadFromStoreAsync(accountId, folderName);
        var sinceUtc = since.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(since, DateTimeKind.Utc)
            : since.ToUniversalTime();
        return all.Where(m => m.Date.UtcDateTime >= sinceUtc).ToList();
    }

    /// <summary>
    /// Incremental fetch. <paramref name="sinceMessageId"/> is ignored — POP3 has no high-water mark
    /// and UIDLs carry no order, so the local store's id set is what decides which messages are new.
    /// Returns only the messages actually downloaded on this call.
    /// </summary>
    public async Task<List<MailMessageSummary>> GetMessagesSinceAsync(
        Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default)
        => IsInbox(folderName)
            ? await DownloadNewMessagesAsync(accountId, ct)
            : [];

    public async Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        if (_onlineMode)
            throw new InvalidOperationException(OnlineModeExplanation);

        var detail = await _store.LoadDetailAsync(accountId, folderName, messageId);
        if (detail is null)
            throw new InvalidOperationException(
                $"POP3 message {messageId} is not in the local store. POP3 messages are only readable " +
                "after they have been downloaded by a sync.");
        return detail;
    }

    public Task<MailMessageDetail> PrefetchMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => GetMessageDetailAsync(accountId, folderName, messageId, ct);

    // ── State mutations (local-only) ──────────────────────────────────────────

    public async Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        if (_onlineMode) return;
        await _store.UpdateIsReadAsync(accountId, folderName, messageId, isRead: true);
    }

    public async Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        if (_onlineMode) return;
        await _store.UpdateIsReadBatchAsync(messageIds.Select(id => (accountId, folderName, id)), isRead: true);
    }

    public async Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default)
    {
        if (_onlineMode) return;
        await _store.UpdateFlagIdAsync(accountId, folderName, messageId,
            flagged ? FlagDefinition.BuiltInFlagId.ToString() : null);
    }

    // ── Delete (local Trash, then the server) ─────────────────────────────────

    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => MoveLocalAsync(accountId, folderName, TrashFolder, [messageId], copy: false);

    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        => MoveLocalAsync(accountId, folderName, TrashFolder, messageIds, copy: false);

    /// <summary>
    /// Removes messages from the local store, and — only when the account is set to remove collected
    /// mail from the server — issues the matching DELEs.
    /// <para>The DELE is addressed by index, and indexes are only meaningful within one session, so
    /// every id is re-resolved against a fresh UIDL listing first. An id no longer listed (another
    /// client collected it) is skipped rather than deleted by position: deleting the wrong message
    /// off a POP3 server destroys the only copy.</para>
    /// </summary>
    public async Task PermanentlyDeleteBatchAsync(
        Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        if (_onlineMode) return;   // nothing was ever collected; see LoadFromStoreAsync

        await _store.DeleteSummariesAsync(accountId, folderName, messageIds);

        if (!_accounts.TryGetValue(accountId, out var account) || account.Pop3LeaveMailOnServer)
            return;

        // Locally-authored mail (sent, drafts) was never on the server; nothing to delete there.
        var serverIds = messageIds.Where(id => !id.StartsWith(LocalIdPrefix, StringComparison.Ordinal)).ToList();
        if (serverIds.Count == 0) return;

        var wanted = new HashSet<string>(serverIds, StringComparer.Ordinal);
        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            using var client = await OpenConnectionAsync(accountId, ct);
            var uidls = await client.GetMessageUidsAsync(ct);

            bool anyDeleted = false;
            for (int i = 0; i < uidls.Count; i++)
            {
                if (!wanted.Contains(uidls[i])) continue;
                await client.DeleteMessageAsync(i, ct);
                anyDeleted = true;
            }

            // QUIT is what commits the DELEs; a plain disconnect abandons them.
            await client.DisconnectAsync(quit: anyDeleted, ct);
            LogService.Log($"POP3 [{account.AccountLabel}]: permanently deleted {serverIds.Count} message(s) locally, {(anyDeleted ? "and on the server" : "none still on the server")}.");
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
    {
        if (_onlineMode) return 0;

        var trashIds = await _store.GetAllMessageIdsAsync(accountId, TrashFolder);
        if (trashIds.Count == 0) return 0;
        await PermanentlyDeleteBatchAsync(accountId, TrashFolder, trashIds.ToList(), ct);
        return trashIds.Count;
    }

    public async Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default)
        => _onlineMode ? 0 : (await _store.GetAllMessageIdsAsync(accountId, TrashFolder)).Count;

    // ── Sent / Drafts (local-only) ────────────────────────────────────────────

    public async Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default)
    {
        // The message has already been sent over SMTP by the time this is called; with no store there
        // is simply nowhere to file the copy, which is the same bargain --online makes for IMAP.
        if (_onlineMode) return;

        _accounts.TryGetValue(accountId, out var account);
        var msg = MimeMessageBuilder.Build(sent, account ?? new AccountModel { Id = accountId }, null);
        var id  = NewLocalId();
        await StoreMessageAsync(accountId, id, msg, SentFolder, isRead: true, ct);
    }

    public async Task<string> AppendDraftAsync(
        Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
    {
        if (_onlineMode)
            throw new InvalidOperationException(OnlineModeExplanation);

        if (replaceMessageId is not null)
            await _store.DeleteSummariesAsync(accountId, DraftsFolder, [replaceMessageId]);

        _accounts.TryGetValue(accountId, out var account);
        var msg = MimeMessageBuilder.Build(draft, account ?? new AccountModel { Id = accountId }, null);
        var id  = NewLocalId();
        await StoreMessageAsync(accountId, id, msg, DraftsFolder, isRead: true, ct);
        return id;
    }

    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult<string?>(DraftsFolder);

    // ── Attachments (served from the stored message bytes) ────────────────────

    public async Task<byte[]> DownloadAttachmentAsync(
        Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
    {
        if (_onlineMode)
            throw new InvalidOperationException(OnlineModeExplanation);

        var mimeBytes = await _store.LoadMimeBytesAsync(accountId, folderName, messageId)
            ?? throw new InvalidOperationException(
                $"No stored message bytes for POP3 message {messageId} in {folderName}, so its attachment " +
                "cannot be extracted. POP3 cannot re-fetch a single part from the server.");

        using var ms = new MemoryStream(mimeBytes);
        var message  = await MimeMessage.LoadAsync(ms, ct);
        var parts    = message.Attachments.ToList();

        if (!int.TryParse(partSpecifier, out var idx) || idx < 0 || idx >= parts.Count)
            throw new InvalidOperationException(
                $"Attachment '{partSpecifier}' is out of range for POP3 message {messageId} ({parts.Count} attachment(s)).");

        if (parts[idx] is not MimePart part || part.Content is null)
            throw new InvalidOperationException(
                $"Attachment {idx} of POP3 message {messageId} is not a decodable MIME part.");

        using var buf = new MemoryStream();
        await part.Content.DecodeToAsync(buf, ct);
        return buf.ToArray();
    }

    // ── Poll / keepalive / status ─────────────────────────────────────────────

    public async Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => (await DownloadNewMessagesAsync(accountId, ct)).Count;

    /// <summary>No-op: POP3 keeps no connection open between operations, so there is nothing to keep alive.</summary>
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        if (_onlineMode) return (0, 0);

        var readStates = await _store.LoadFolderReadStatesAsync(accountId, InboxFolder);
        return (readStates.Count, readStates.Count(kv => !kv.Value));
    }

    // ── Id listings (the sweep's input — see the sync contract on the class) ──

    /// <summary>
    /// Store-backed by design. The deletion reconcile diffs the cache against this, so answering
    /// from the server would delete every message the server has already dropped — which, with
    /// leave-on-server off, is all of them.
    /// </summary>
    public async Task<IList<string>> GetFolderMessageIdsAsync(
        Guid accountId, string folderName, CancellationToken ct = default)
        => _onlineMode ? [] : (await _store.GetAllMessageIdsAsync(accountId, folderName)).ToList();

    /// <summary>
    /// The union of what the server lists and what the cache holds — see the sync contract on the
    /// class for why each half is there.
    /// <para>Cached rows carry their cached date and read state. Server UIDLs we have not seen are
    /// reported as received now: their real date is inside the message, which POP3 cannot show us
    /// without downloading it, and "now" is the answer that puts them inside the sweep's window so
    /// the fetch actually happens.</para>
    /// <para>Only the Inbox contacts the server; the synthetic folders have no server side, and in
    /// <c>--online</c> mode there is no store, so both answer from what they have.</para>
    /// </summary>
    public async Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(
        Guid accountId, string folderName, CancellationToken ct = default)
    {
        var cached = await LoadFromStoreAsync(accountId, folderName);

        if (!IsInbox(folderName) || _onlineMode || !_accounts.ContainsKey(accountId))
            return MergeListing(cached, [], DateTimeOffset.UtcNow);

        IList<string> serverUidls;
        try
        {
            serverUidls = await ListServerUidlsAsync(accountId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // An unreachable server means "nothing new to report", never "the cache is stale".
            // Reporting the cached half alone keeps the sweep's reconciles no-ops.
            LogService.Log($"POP3: UIDL listing failed for account {accountId}", ex);
            serverUidls = [];
        }

        return MergeListing(cached, serverUidls, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The sync contract's arithmetic, in one place so it can be tested directly: every cached
    /// message, plus every server UIDL not already cached.
    ///
    /// <para>Cached rows keep their own date and read state — the sweep's read reconcile compares
    /// against them, and POP3 has no server-side read state that could disagree. Unseen UIDLs are
    /// stamped <paramref name="now"/> and unread, which is what puts them inside the sweep's window
    /// so it fetches them.</para>
    ///
    /// <para>Cached ids are never dropped, whatever the server says. That is the whole point: the
    /// sweep deletes cached ids missing from this list, and a POP3 server legitimately stops listing
    /// a message the moment it is collected.</para>
    /// </summary>
    internal static IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)> MergeListing(
        IReadOnlyList<MailMessageSummary> cached, IEnumerable<string> serverUidls, DateTimeOffset now)
    {
        var result = cached
            .Select(m => (Id: m.MessageId, ReceivedUtc: m.Date, m.IsRead))
            .ToList();

        var known = new HashSet<string>(result.Select(r => r.Id), StringComparer.Ordinal);
        foreach (var uidl in serverUidls)
            if (known.Add(uidl))
                result.Add((uidl, now, false));

        return result;
    }

    /// <summary>
    /// Previews are built at download time and live in the store, so this needs no server round trip.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<string> messageIds,
        int maxLines, CancellationToken ct = default)
    {
        if (_onlineMode) return new Dictionary<string, string>();

        var wanted = new HashSet<string>(messageIds, StringComparer.Ordinal);
        var summaries = await _store.LoadFolderSummariesAsync(accountId, folderName);
        return summaries
            .Where(s => wanted.Contains(s.MessageId) && !string.IsNullOrEmpty(s.Preview))
            .ToDictionary(s => s.MessageId, s => s.Preview, StringComparer.Ordinal);
    }

    // ── Copy / Move (between the synthetic folders) ───────────────────────────

    public Task CopyMessagesAsync(
        Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => MoveLocalAsync(accountId, folderName, destinationFolder, messageIds, copy: true);

    public Task MoveMessagesAsync(
        Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        => MoveLocalAsync(accountId, folderName, destinationFolder, messageIds, copy: false);

    // ── Folder CRUD — not supported ───────────────────────────────────────────
    // POP3 has no folder namespace. Callers gate on BackendKind (see MainViewModel's folder
    // commands) so the user never reaches these; they throw rather than silently doing nothing.

    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default)
        => throw new NotSupportedException("POP3 accounts have no server folders, so folders cannot be created.");

    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => throw new NotSupportedException("POP3 accounts have no server folders, so folders cannot be deleted.");

    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default)
        => throw new NotSupportedException("POP3 accounts have no server folders, so folders cannot be renamed or moved.");

    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default)
        => throw new NotSupportedException("POP3 accounts have no server folders, so folders cannot be copied.");

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Collects every UIDL the server holds that the store does not, storing each message whole.
    /// Returns the messages downloaded on this call (empty when there is nothing new).
    /// <para>Deduplication is by UIDL against the store, which is what makes this safe to call from
    /// several sync paths — the sweep, a poll, an account's first load — without re-downloading.</para>
    /// </summary>
    private async Task<List<MailMessageSummary>> DownloadNewMessagesAsync(Guid accountId, CancellationToken ct)
    {
        var downloaded = new List<MailMessageSummary>();

        if (_onlineMode)
        {
            // POP3 keeps mail nowhere but the store, so with no store there is nothing to sync into.
            LogService.Log($"POP3: skipping sync for account {accountId} — --online mode has no local store.");
            return downloaded;
        }

        if (!_accounts.TryGetValue(accountId, out var account))
            return downloaded;

        var seen = await _store.GetAllMessageIdsAsync(accountId, InboxFolder);

        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            using var client = await OpenConnectionAsync(accountId, ct);
            if (client.Count == 0)
            {
                await client.DisconnectAsync(quit: false, ct);
                return downloaded;
            }

            // Index into this list is the session-scoped message number the RETR/DELE commands take.
            var uidls = await client.GetMessageUidsAsync(ct);
            bool anyDeleted = false;

            for (int i = 0; i < uidls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var uidl = uidls[i];
                if (seen.Contains(uidl)) continue;

                MimeMessage msg;
                try
                {
                    msg = await client.GetMessageAsync(i, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // One unreadable message must not stop the rest of the mailbox from arriving.
                    LogService.Log($"POP3 [{account.AccountLabel}]: failed to download UIDL {uidl}", ex);
                    continue;
                }

                var summary = await StoreMessageAsync(accountId, uidl, msg, InboxFolder, isRead: false, ct);
                downloaded.Add(summary);

                if (!account.Pop3LeaveMailOnServer)
                {
                    // Only after the message is committed to the store. DELEs take effect at QUIT.
                    await client.DeleteMessageAsync(i, ct);
                    anyDeleted = true;
                }
            }

            await client.DisconnectAsync(quit: anyDeleted, ct);

            if (downloaded.Count > 0)
                LogService.Log($"POP3 [{account.AccountLabel}]: downloaded {downloaded.Count} new message(s)" +
                               (anyDeleted ? ", removed from the server" : ", left on the server") + ".");
            return downloaded;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Lists the server's UIDLs without retrieving anything. One short session.</summary>
    private async Task<IList<string>> ListServerUidlsAsync(Guid accountId, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            using var client = await OpenConnectionAsync(accountId, ct);
            IList<string> uidls = client.Count == 0
                ? new List<string>()
                : await client.GetMessageUidsAsync(ct);
            await client.DisconnectAsync(quit: false, ct);
            return uidls;
        }
        finally
        {
            sem.Release();
        }
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes one downloaded (or locally authored) message to the store as summary + detail, keeping
    /// the raw bytes when it has attachments so they can be extracted later.
    /// </summary>
    private async Task<MailMessageSummary> StoreMessageAsync(
        Guid accountId, string messageId, MimeMessage msg, string folderName, bool isRead, CancellationToken ct)
    {
        var summary = BuildSummary(accountId, messageId, msg, folderName, isRead);
        var detail  = BuildDetail(accountId, messageId, msg, folderName, isRead);

        await _store.UpsertSummariesAsync([summary]);
        await _store.UpsertDetailAsync(detail);

        if (detail.Attachments.Count > 0)
        {
            using var ms = new MemoryStream();
            await msg.WriteToAsync(ms, ct);
            await _store.StoreMimeBytesAsync(accountId, folderName, messageId, ms.ToArray());
        }

        return summary;
    }

    /// <summary>
    /// Loads a folder from the store for a caller on a sync path.
    /// <para>The stamp matters: <c>SyncService</c> upserts whatever a fetch returns, and
    /// <c>UpsertSummariesAsync</c> derives the stored flag from <see cref="MailMessageSummary.IsServerFlagged"/>
    /// — an IMAP-shaped rule where the server is the authority on flags. POP3 has no server flag
    /// state, so the local flag IS the state of record; without restating it, every sweep would
    /// re-upsert these rows as unflagged and silently clear the user's flags.</para>
    /// </summary>
    private async Task<List<MailMessageSummary>> LoadFromStoreAsync(Guid accountId, string folderName, int? limit = null)
    {
        // --online skips LocalStoreService.Initialize(), so the tables do not exist. Reading anyway
        // raises "no such table: MessageSummary" from every aggregate load — an error in the log for
        // a supported configuration. A POP3 account in --online mode simply has no mail.
        if (_onlineMode) return [];

        var summaries = await _store.LoadFolderSummariesAsync(accountId, folderName, limit);
        foreach (var s in summaries)
            s.IsServerFlagged = s.IsFlagged;
        return summaries;
    }

    /// <summary>
    /// Copies or moves messages between the synthetic folders, carrying everything that makes the
    /// message what it is: the stored row itself (so no field is lost when the model gains one), its
    /// flag, its body and its raw bytes.
    /// </summary>
    private async Task MoveLocalAsync(
        Guid accountId, string fromFolder, string toFolder, IList<string> messageIds, bool copy)
    {
        if (_onlineMode) return;   // no store, so there is nothing filed anywhere to move
        if (string.Equals(fromFolder, toFolder, StringComparison.OrdinalIgnoreCase)) return;

        var byId = (await _store.LoadFolderSummariesAsync(accountId, fromFolder))
            .ToDictionary(s => s.MessageId, StringComparer.Ordinal);

        foreach (var id in messageIds)
        {
            if (!byId.TryGetValue(id, out var summary)) continue;

            var detail    = await _store.LoadDetailAsync(accountId, fromFolder, id);
            var mimeBytes = await _store.LoadMimeBytesAsync(accountId, fromFolder, id);
            var flagId    = summary.FlagId;

            // Re-file the loaded rows rather than projecting them field by field — these instances
            // came from the store, not from the UI, so retargeting them is safe and cannot drop a
            // column that the model gains later.
            summary.FolderName      = toFolder;
            summary.IsServerFlagged = flagId is not null;   // see LoadFromStoreAsync
            await _store.UpsertSummariesAsync([summary]);

            // The upsert writes the built-in flag id at most; a named flag has to be restated.
            if (flagId is not null)
                await _store.UpdateFlagIdAsync(accountId, toFolder, id, flagId);

            if (detail is not null)
            {
                detail.FolderName = toFolder;
                await _store.UpsertDetailAsync(detail);
                if (mimeBytes is not null)
                    await _store.StoreMimeBytesAsync(accountId, toFolder, id, mimeBytes);
            }

            if (!copy)
                await _store.DeleteSummariesAsync(accountId, fromFolder, [id]);
        }
    }

    // ── Model building ────────────────────────────────────────────────────────

    // BuildSummary/BuildDetail are internal rather than private so the mapping from a downloaded
    // MimeMessage to QuickMail's models can be tested directly, without standing up a POP3 server.
    internal static MailMessageSummary BuildSummary(
        Guid accountId, string messageId, MimeMessage msg, string folderName, bool isRead) =>
        new()
        {
            MessageId         = messageId,
            AccountId         = accountId,
            FolderName        = folderName,
            InternetMessageId = msg.MessageId ?? string.Empty,
            // Display form in the summary, full addresses in the detail — matching ImapMailService,
            // so the message list reads the same whichever backend fetched the message.
            From              = FormatAddressesDisplay(msg.From),
            To                = FormatAddresses(msg.To),
            Subject           = string.IsNullOrEmpty(msg.Subject) ? "(no subject)" : msg.Subject,
            Date              = NormalizeDate(msg.Date),
            IsRead            = isRead,
            Preview           = BuildPreview(msg),
            HasAttachments    = msg.Attachments.Any(),
            IsMailingList     = !string.IsNullOrEmpty(msg.Headers["List-Id"]),
        };

    internal static MailMessageDetail BuildDetail(
        Guid accountId, string messageId, MimeMessage msg, string folderName, bool isRead)
    {
        var attachments = new List<AttachmentModel>();
        int idx = 0;
        foreach (var part in msg.Attachments)
        {
            attachments.Add(new AttachmentModel
            {
                FileName    = part.ContentDisposition?.FileName
                              ?? part.ContentType?.Name
                              ?? $"attachment{idx}",
                ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                FileSize    = DecodedSize(part),
                // Index into MimeMessage.Attachments — the same enumeration DownloadAttachmentAsync
                // walks when it re-parses the stored bytes, so the two always agree.
                PartSpecifier = idx.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            idx++;
        }

        var detail = new MailMessageDetail
        {
            MessageId         = messageId,
            AccountId         = accountId,
            FolderName        = folderName,
            InternetMessageId = msg.MessageId ?? string.Empty,
            From              = FormatAddresses(msg.From),
            To                = FormatAddresses(msg.To),
            Cc                = FormatAddresses(msg.Cc),
            ReplyTo           = FormatAddresses(msg.ReplyTo),
            Subject           = string.IsNullOrEmpty(msg.Subject) ? "(no subject)" : msg.Subject,
            Date              = NormalizeDate(msg.Date),
            IsRead            = isRead,
            PlainTextBody     = msg.TextBody ?? string.Empty,
            HtmlBody          = msg.HtmlBody ?? string.Empty,
            Attachments       = attachments,
            DraftComposeMode  = ImapMailService.ParseComposeMode(msg.Headers["X-QuickMail-Compose-Mode"]),
        };

        ImapMailService.PopulateCalendar(detail, FindCalendarText(msg));
        return detail;
    }

    /// <summary>
    /// Decoded byte count for an attachment, measured rather than guessed: the encoded length on the
    /// wire is what the stream reports, and for base64 that overstates the file by a third.
    /// </summary>
    private static long DecodedSize(MimeEntity entity)
    {
        if (entity is not MimePart part || part.Content is null) return 0;
        try
        {
            using var measuring = new MimeKit.IO.MeasuringStream();
            part.Content.DecodeTo(measuring);
            return measuring.Length;
        }
        catch (Exception ex)
        {
            LogService.Log($"POP3: could not measure attachment '{part.FileName}'", ex);
            return 0;
        }
    }

    /// <summary>Raw text of the first text/calendar part, or null when the message carries no invite.</summary>
    private static string? FindCalendarText(MimeMessage msg) =>
        msg.BodyParts
            .OfType<TextPart>()
            .FirstOrDefault(p => p.ContentType.IsMimeType("text", "calendar"))?.Text;

    private static string FormatAddresses(InternetAddressList list) =>
        list.Count == 0 ? string.Empty : string.Join(", ", list.Select(a => a.ToString()));

    private static string FormatAddressesDisplay(InternetAddressList list) =>
        list.Count == 0
            ? string.Empty
            : string.Join(", ", list.Select(a =>
                a is MailboxAddress mb && !string.IsNullOrWhiteSpace(mb.Name) ? mb.Name : a.ToString()));

    /// <summary>
    /// A message with no (or an unparseable) Date header sorts to the top of the list rather than to
    /// 1/1/0001 at the bottom, and — because the sweep's window test reads this date — is treated as
    /// having just arrived, which is what it is from this mailbox's point of view.
    /// </summary>
    private static DateTimeOffset NormalizeDate(DateTimeOffset date) =>
        date == default ? DateTimeOffset.UtcNow : date;

    private static string BuildPreview(MimeMessage msg)
    {
        var text = msg.TextBody;
        if (string.IsNullOrWhiteSpace(text))
            text = Helpers.HtmlStripper.ToPlainText(msg.HtmlBody);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var joined = string.Join(" ", text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(3));
        return joined.Length > 200 ? joined[..200] : joined;
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private Task<Pop3Client> OpenConnectionAsync(Guid accountId, CancellationToken ct)
    {
        if (!_accounts.TryGetValue(accountId, out var account))
            throw new InvalidOperationException($"Account {accountId} is not registered with the POP3 backend.");
        if (!_passwords.TryGetValue(accountId, out var password) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException($"No password is available for POP3 account {account.AuthUsername}.");
        return OpenConnectionAsync(account, password, ct);
    }

    private static async Task<Pop3Client> OpenConnectionAsync(AccountModel account, string password, CancellationToken ct)
    {
        var client = new Pop3Client();
        try
        {
            if (account.Pop3AcceptInvalidCert)
            {
#pragma warning disable CA5359 // Accepting an invalid certificate is this account's explicit setting.
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            }

            await client.ConnectAsync(account.Pop3Host, account.Pop3Port, MailSecurity.ForPop3(account), ct);
            await client.AuthenticateAsync(account.AuthUsername, password, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Why an operation that needs the local store cannot run. POP3 keeps mail nowhere else, so
    /// <c>--online</c> — which is defined as "no local store" — leaves a POP3 account with nothing to
    /// read, file or attach. Said plainly rather than surfaced as a SQLite "no such table" error.
    /// </summary>
    private const string OnlineModeExplanation =
        "POP3 accounts store their mail locally, and --online mode runs without the local store. " +
        "Restart QuickMail without --online to use this account.";

    private static bool IsInbox(string folderName) =>
        string.Equals(folderName, InboxFolder, StringComparison.OrdinalIgnoreCase);

    private static string NewLocalId() => LocalIdPrefix + Guid.NewGuid().ToString("N");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        _disposed = true;
        foreach (var sem in _locks.Values)
            sem.Dispose();
        _locks.Clear();
        _accounts.Clear();
        _passwords.Clear();
        GC.SuppressFinalize(this);
    }
}
