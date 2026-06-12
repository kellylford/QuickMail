using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public class ImapMailService : IMailService
{
    private const int DefaultMaxConnectionsPerAccount = 6;
    private const int AbsoluteMaxConnectionsPerAccount = 15;
    private const int ForegroundReservedConnectionCount = 2;

    private readonly IOAuthService _oauth;
    private readonly IConfigService? _config;
    private readonly ConcurrentDictionary<Guid, AccountConnectionPool> _pools = new();
    private readonly ConcurrentDictionary<Guid, AccountModel> _accounts = new();
    private readonly ConcurrentDictionary<Guid, string>  _passwords     = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _poolLocks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _idleCts = new();
    private readonly ConcurrentDictionary<Guid, int> _idleRetryCount = new();
    private bool _disposed;

    public event Action<Guid, bool>? AccountReachabilityChanged;
    public event Action<Guid>? InboxNewMailDetected;

    public ImapMailService(IOAuthService oauth, IConfigService? config = null)
    {
        _oauth  = oauth;
        _config = config;
    }

    private enum ImapLeasePriority
    {
        Foreground,
        Background
    }

    // ── Connect / disconnect ─────────────────────────────────────────────────────

    public async Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (account.AuthType == AuthType.Password)
        {
            if (password is null && !_passwords.TryGetValue(account.Id, out password))
                throw new InvalidOperationException($"No password is available for account {account.Username}.");
        }

        var maxConnections = GetMaxConnectionsPerAccount();
        var poolLock = _poolLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await poolLock.WaitAsync(ct);
        try
        {
            if (_pools.TryGetValue(account.Id, out var existing) &&
                existing.Matches(account, maxConnections))
            {
                existing.UpdateAccount(account, password);
                _accounts[account.Id] = CloneAccount(account);
                if (password is not null)
                    _passwords[account.Id] = password;

                using var existingWarmLease = await existing.RentAsync(ImapLeasePriority.Foreground, ct);
                return;
            }

            if (_pools.TryRemove(account.Id, out var stalePool))
                await stalePool.DisconnectAsync(ct);

            var pool = new AccountConnectionPool(this, account, password, maxConnections);
            _pools[account.Id]   = pool;
            _accounts[account.Id] = CloneAccount(account);
            if (password is not null)
                _passwords[account.Id] = password;

            using var newWarmLease = await pool.RentAsync(ImapLeasePriority.Foreground, ct);
        }
        finally
        {
            poolLock.Release();
        }
    }

    public async Task DisconnectAsync(Guid accountId, CancellationToken ct = default)
    {
        if (_idleCts.TryRemove(accountId, out var watcherCts))
        {
            try { watcherCts.Cancel(); watcherCts.Dispose(); } catch { }
        }

        if (_pools.TryRemove(accountId, out var pool))
            await pool.DisconnectAsync(ct);

        _accounts.TryRemove(accountId, out _);
        _passwords.TryRemove(accountId, out _);
    }

    // ── Folder list ──────────────────────────────────────────────────────────────

    public async Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var result = new List<MailFolderModel>();

        // Use IMAP STATUS for all folders — faster than EXAMINE (no select/deselect
        // round-trips) and gives accurate UNSEEN counts that EXAMINE often omits.

        // Always put INBOX first — many servers don't return it via GetFoldersAsync.
        try
        {
            var inbox = client.Inbox!;
            await inbox.StatusAsync(StatusItems.Count | StatusItems.Unread, ct);
            LogService.Log($"INBOX: FullName={inbox.FullName} Count={inbox.Count} Unread={inbox.Unread}");
            result.Add(new MailFolderModel
            {
                FullName     = inbox.FullName,
                DisplayName  = "Inbox",
                UnreadCount  = inbox.Unread,
                MessageCount = inbox.Count,
                AccountId    = accountId,
                Kind         = SpecialFolderKind.Inbox
            });
        }
        catch (Exception ex) { LogService.Log("GetFolders/Inbox", ex); }

        var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: ct);
        LogService.Log($"GetFoldersAsync returned {folders.Count} folders");

        foreach (var folder in folders)
        {
            if ((folder.Attributes & FolderAttributes.NonExistent) != 0) continue;
            if ((folder.Attributes & FolderAttributes.NoSelect)    != 0) continue;
            if (folder.FullName == client.Inbox!.FullName)              continue;

            try
            {
                await folder.StatusAsync(StatusItems.Count | StatusItems.Unread, ct);
                LogService.Log($"  Folder: {folder.FullName} Count={folder.Count} Unread={folder.Unread}");
                result.Add(new MailFolderModel
                {
                    FullName           = folder.FullName,
                    DisplayName        = folder.Name,
                    UnreadCount        = folder.Unread,
                    MessageCount       = folder.Count,
                    AccountId          = accountId,
                    ExcludeFromAllMail = IsExcludedFromAllMail(folder.Attributes, folder.FullName),
                    Kind               = GetSpecialFolderKind(folder.Attributes, folder.FullName)
                });
            }
            catch (Exception ex)
            {
                LogService.Log($"  Cannot get status for folder {folder.FullName}: {ex.Message}");
                result.Add(new MailFolderModel
                {
                    FullName           = folder.FullName,
                    DisplayName        = folder.Name,
                    UnreadCount        = 0,
                    AccountId          = accountId,
                    ExcludeFromAllMail = IsExcludedFromAllMail(folder.Attributes, folder.FullName),
                    Kind               = GetSpecialFolderKind(folder.Attributes, folder.FullName)
                });
            }
        }

        LogService.Log($"GetFoldersAsync: returning {result.Count} folders");
        return result;
    }

    public async Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Background);
        var inbox = lease.Client.Inbox!;
        await inbox.StatusAsync(StatusItems.Count | StatusItems.Unread, ct);
        LogService.Debug($"GetInboxStatus: account={accountId} Total={inbox.Count} Unread={inbox.Unread}");
        return (inbox.Count, inbox.Unread);
    }

    // ── Message lists ────────────────────────────────────────────────────────────

    public async Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
    {
        LogService.Log($"GetMessageSummaries: folder={folderName} maxMessages={maxMessages}");
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        LogService.Log($"  Opened. Count={folder.Count} Unread={folder.Unread}");
        try
        {
            if (folder.Count == 0) return new List<MailMessageSummary>();

            var startIndex = Math.Max(0, folder.Count - maxMessages);
            var summaries  = await folder.FetchAsync(
                startIndex, -1,
                MessageSummaryItems.UniqueId
                | MessageSummaryItems.Envelope
                | MessageSummaryItems.Flags
                | MessageSummaryItems.PreviewText,   // free if server supports PREVIEW
                _mailingListHeaders,
                ct);

            var result = summaries
                .OrderByDescending(s => s.Envelope?.Date ?? DateTimeOffset.MinValue)
                .Select(s => SummaryToModel(s, accountId, folderName))
                .ToList();

            LogService.Log($"  Returning {result.Count} summaries (preview={result.Count(r => !string.IsNullOrEmpty(r.Preview))} prefilled)");
            return result;
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(
        Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
    {
        LogService.Log($"GetMessagesSinceDate: folder={folderName} since={since:yyyy-MM-dd}");
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Background);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            var query   = SearchQuery.DeliveredAfter(since.Date);
            var uids    = await folder.SearchAsync(query, ct);
            if (uids.Count == 0) return new List<MailMessageSummary>();

            var items = MessageSummaryItems.UniqueId
                      | MessageSummaryItems.Envelope
                      | MessageSummaryItems.Flags
                      | MessageSummaryItems.PreviewText;

            var summaries = await folder.FetchAsync(uids, items, _mailingListHeaders, ct);
            var result    = summaries
                .OrderByDescending(s => s.Envelope?.Date ?? DateTimeOffset.MinValue)
                .Select(s => SummaryToModel(s, accountId, folderName))
                .ToList();

            LogService.Log($"  Returning {result.Count} summaries since {since:yyyy-MM-dd}");
            return result;
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task<List<MailMessageSummary>> GetMessagesSinceAsync(
        Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Background);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            var items = MessageSummaryItems.UniqueId
                      | MessageSummaryItems.Envelope
                      | MessageSummaryItems.Flags
                      | MessageSummaryItems.PreviewText;   // free if server supports PREVIEW

            IList<IMessageSummary> summaries;
            if (sinceMessageId == "0")
            {
                if (folder.Count == 0) return new List<MailMessageSummary>();
                var count = initialCount > 0 ? initialCount : folder.Count;
                var startIndex = Math.Max(0, folder.Count - count);
                summaries = await folder.FetchAsync(startIndex, -1, items, _mailingListHeaders, ct);
            }
            else
            {
                var sinceUid = uint.Parse(sinceMessageId, CultureInfo.InvariantCulture);
                var range = new UniqueIdRange(new UniqueId(sinceUid + 1), UniqueId.MaxValue);
                summaries = await folder.FetchAsync((IList<UniqueId>)range, items, _mailingListHeaders, ct);
            }

            return summaries.Select(s => SummaryToModel(s, accountId, folderName)).ToList();
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Message detail ───────────────────────────────────────────────────────────

    public Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default) =>
        GetMessageDetailCoreAsync(accountId, folderName, messageId, markRead: true, ImapLeasePriority.Foreground, ct);

    public Task<MailMessageDetail> PrefetchMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default) =>
        GetMessageDetailCoreAsync(accountId, folderName, messageId, markRead: false, ImapLeasePriority.Background, ct);

    private async Task<MailMessageDetail> GetMessageDetailCoreAsync(
        Guid accountId, string folderName, string messageId,
        bool markRead, ImapLeasePriority priority, CancellationToken ct)
    {
        using var lease = await RentClientAsync(accountId, ct, priority);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        var access = markRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly;
        await folder.OpenAsync(access, ct);

        try
        {
            var mailKitUid = ToUid(messageId);
            var summaries  = await folder.FetchAsync(
                new[] { mailKitUid },
                MessageSummaryItems.UniqueId
                | MessageSummaryItems.Envelope
                | MessageSummaryItems.Flags
                | MessageSummaryItems.BodyStructure,
                new[] { "X-QuickMail-Compose-Mode" },
                ct);

            var s = summaries.FirstOrDefault()
                ?? throw new InvalidOperationException($"Message UID {messageId} not found.");

            string plainText = string.Empty;
            string htmlText  = string.Empty;

            if (s.HtmlBody != null)
            {
                try
                {
                    var bodyPart = await folder.GetBodyPartAsync(mailKitUid, s.HtmlBody, ct);
                    if (bodyPart is TextPart tp) htmlText = tp.Text ?? string.Empty;
                }
                catch (Exception ex)
                {
                    LogService.Log($"ImapMailService: failed to fetch HTML body for UID {messageId} in {folderName}: {ex.Message}");
                }
            }

            if (s.TextBody != null)
            {
                try
                {
                    var bodyPart = await folder.GetBodyPartAsync(mailKitUid, s.TextBody, ct);
                    if (bodyPart is TextPart tp) plainText = tp.Text ?? string.Empty;
                }
                catch (Exception ex)
                {
                    LogService.Log($"ImapMailService: failed to fetch plain-text body for UID {messageId} in {folderName}: {ex.Message}");
                }
            }

            if (markRead)
                await folder.AddFlagsAsync(mailKitUid, MessageFlags.Seen, true, ct);

            var attachments = ExtractAttachments(s.Body);

            // Detect and parse text/calendar MIME parts (ICS calendar invites).
            IcsModel? calendarInvite = null;
            var calendarPart = FindCalendarPart(s.Body);
            if (calendarPart != null)
            {
                try
                {
                    var decoded = await folder.GetBodyPartAsync(mailKitUid, calendarPart, ct);
                    if (decoded is TextPart tp && !string.IsNullOrWhiteSpace(tp.Text))
                    {
                        calendarInvite = IcsModel.Parse(tp.Text);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Log($"ImapMailService: failed to parse calendar part for UID {messageId}: {ex.Message}");
                }
            }

            return new MailMessageDetail
            {
                MessageId     = messageId,
                AccountId     = accountId,
                FolderName    = folderName,
                From          = FormatAddressList(s.Envelope?.From),
                To            = FormatAddressList(s.Envelope?.To),
                Cc            = FormatAddressList(s.Envelope?.Cc),
                ReplyTo       = FormatAddressList(s.Envelope?.ReplyTo),
                Subject       = s.Envelope?.Subject ?? "(no subject)",
                Date          = s.Envelope?.Date ?? DateTimeOffset.MinValue,
                IsRead        = markRead || (s.Flags & MessageFlags.Seen) != 0,
                InternetMessageId = s.Envelope?.MessageId ?? string.Empty,
                PlainTextBody = plainText,
                HtmlBody      = htmlText,
                Attachments   = attachments,
                CalendarInvite = calendarInvite,
                DraftComposeMode = ParseComposeMode(s.Headers?["X-QuickMail-Compose-Mode"]),
            };
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Mutations ────────────────────────────────────────────────────────────────

    public async Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try   { await folder.AddFlagsAsync(ToUid(messageId), MessageFlags.Seen, true, ct); }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return;
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try   { await folder.AddFlagsAsync(messageIds.Select(ToUid).ToList(), MessageFlags.Seen, true, ct); }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var uidList = messageIds.Select(ToUid).ToList();
            var trash   = await FindSpecialFolderAsync(client, ct, SpecialFolder.Trash, SpecialFolder.Junk);
            if (trash != null) await folder.MoveToAsync(uidList, trash, ct);
            else               await folder.AddFlagsAsync(uidList, MessageFlags.Deleted, true, ct);
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var trash = await FindSpecialFolderAsync(client, ct, SpecialFolder.Trash, SpecialFolder.Junk);
            var mailKitUid = ToUid(messageId);
            if (trash != null) await folder.MoveToAsync(mailKitUid, trash, ct);
            else               await folder.AddFlagsAsync(mailKitUid, MessageFlags.Deleted, true, ct);
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Drafts ───────────────────────────────────────────────────────────────────

    public async Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var drafts = await FindSpecialFolderAsync(client, ct, SpecialFolder.Drafts);
        return drafts?.FullName;
    }

    public async Task<string> AppendDraftAsync(
        Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client  = lease.Client;
        var account = _accounts[accountId];

        var draftsFolder = await FindSpecialFolderAsync(client, ct, SpecialFolder.Drafts)
            ?? throw new InvalidOperationException("No Drafts folder found for this account.");

        var msg = MimeMessageBuilder.Build(draft, account);

        if (!string.IsNullOrEmpty(replaceMessageId))
        {
            await draftsFolder.OpenAsync(FolderAccess.ReadWrite, ct);
            try
            {
                await draftsFolder.AddFlagsAsync(ToUid(replaceMessageId), MessageFlags.Deleted, true, ct);
                await draftsFolder.ExpungeAsync(ct);
            }
            finally { await draftsFolder.CloseAsync(false, ct); }
        }

        await draftsFolder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var newUid = await draftsFolder.AppendAsync(msg, MessageFlags.Draft, ct);
            LogService.Log($"AppendDraft: saved draft to {draftsFolder.FullName} UID={newUid?.Id}");
            // Empty (not "0") signals "server didn't echo a UID" so the next save appends a new
            // draft instead of issuing a delete against the invalid UID 0.
            return newUid?.Id is uint id ? id.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }
        finally { await draftsFolder.CloseAsync(false, ct); }
    }

    public async Task AppendToSentAsync(
        Guid accountId, ComposeModel sent, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client  = lease.Client;
        var account = _accounts[accountId];

        var sentFolder = await FindSpecialFolderAsync(client, ct, SpecialFolder.Sent);
        if (sentFolder == null)
        {
            LogService.Log($"AppendToSent: no Sent folder found for account {accountId}");
            return;
        }

        var msg = MimeMessageBuilder.Build(sent, account);

        await sentFolder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var uid = await sentFolder.AppendAsync(msg, MessageFlags.Seen, ct);
            LogService.Log($"AppendToSent: appended to {sentFolder.FullName} UID={uid?.Id}");
        }
        finally { await sentFolder.CloseAsync(false, ct); }
    }

    // ── Copy / Move messages ─────────────────────────────────────────────────────

    public async Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        var dest   = await client.GetFolderAsync(destinationFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try   { await folder.CopyToAsync(messageIds.Select(ToUid).ToList(), dest, ct); }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        var dest   = await client.GetFolderAsync(destinationFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try   { await folder.MoveToAsync(messageIds.Select(ToUid).ToList(), dest, ct); }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Folder CRUD ──────────────────────────────────────────────────────────────

    public async Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        IMailFolder parent = string.IsNullOrEmpty(parentFolderName)
            ? client.GetFolder(client.PersonalNamespaces[0])
            : await client.GetFolderAsync(parentFolderName, ct);
        await parent.CreateAsync(name, true, ct);
    }

    public async Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);

        // Move all messages to Trash first so no mail is hard-deleted
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var uids = await folder.SearchAsync(SearchQuery.All, ct);
            if (uids.Count > 0)
            {
                var trash = await FindSpecialFolderAsync(client, ct, SpecialFolder.Trash);
                if (trash != null) await folder.MoveToAsync(uids, trash, ct);
                else               await folder.AddFlagsAsync(uids, MessageFlags.Deleted, true, ct);
            }
        }
        finally { await folder.CloseAsync(false, ct); }

        await folder.DeleteAsync(ct);
    }

    public async Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        IMailFolder parent = string.IsNullOrEmpty(newParentFolderName)
            ? client.GetFolder(client.PersonalNamespaces[0])
            : await client.GetFolderAsync(newParentFolderName, ct);
        await folder.RenameAsync(parent, newName, ct);
    }

    public async Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        await CopyFolderCoreAsync(lease.Client, folderName, destinationParentName, ct);
    }

    private async Task CopyFolderCoreAsync(ImapClient client, string folderName, string? destinationParentName, CancellationToken ct)
    {
        var srcFolder = await client.GetFolderAsync(folderName, ct);

        IMailFolder destParent = string.IsNullOrEmpty(destinationParentName)
            ? client.GetFolder(client.PersonalNamespaces[0])
            : await client.GetFolderAsync(destinationParentName, ct);

        var newFolder = await destParent.CreateAsync(srcFolder.Name, true, ct)
            ?? throw new InvalidOperationException($"Failed to create destination folder '{srcFolder.Name}'.");

        await srcFolder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            if (srcFolder.Count > 0)
            {
                var uids = await srcFolder.SearchAsync(SearchQuery.All, ct);
                if (uids.Count > 0)
                    await srcFolder.CopyToAsync(uids, newFolder, ct);
            }
        }
        finally { await srcFolder.CloseAsync(false, ct); }

        var subfolders = await srcFolder.GetSubfoldersAsync(false, ct);
        foreach (var sub in subfolders)
            await CopyFolderCoreAsync(client, sub.FullName, newFolder.FullName, ct);
    }

    public async Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var trash  = await FindSpecialFolderAsync(client, ct, SpecialFolder.Trash, SpecialFolder.Junk);

        if (trash == null)
        {
            LogService.Log($"EmptyTrash: no Trash folder found for account {accountId}");
            return 0;
        }

        await trash.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var uids = await trash.SearchAsync(SearchQuery.All, ct);
            if (uids.Count == 0) return 0;
            LogService.Log($"EmptyTrash: expunging {uids.Count} messages from {trash.FullName}");
            await trash.AddFlagsAsync(uids, MessageFlags.Deleted, true, ct);
            await trash.ExpungeAsync(ct);
            return uids.Count;
        }
        finally { await trash.CloseAsync(false, ct); }
    }

    // ── UID queries ──────────────────────────────────────────────────────────────

    public async Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Background);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            if (folder.Count == 0) return (IList<string>)Array.Empty<string>();
            var uids = await folder.SearchAsync(SearchQuery.All, ct);
            return (IList<string>)uids.Select(u => u.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Body-download preview fallback (used when server lacks IMAP PREVIEW) ────

    public async Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<string> messageIds,
        int maxLines, CancellationToken ct = default)
    {
        if (messageIds.Count == 0 || maxLines <= 0)
            return new Dictionary<string, string>();

        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Background);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            var result = new Dictionary<string, string>();
            var mailKitUids = messageIds.Select(ToUid).ToList();
            var summaries   = await folder.FetchAsync(
                mailKitUids,
                MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure,
                ct);

            foreach (var s in summaries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string text = string.Empty;
                    if (s.TextBody != null)
                    {
                        var part = await folder.GetBodyPartAsync(s.UniqueId, s.TextBody, ct);
                        if (part is TextPart tp) text = tp.Text ?? string.Empty;
                    }
                    else if (s.HtmlBody != null)
                    {
                        var part = await folder.GetBodyPartAsync(s.UniqueId, s.HtmlBody, ct);
                        if (part is TextPart tp) text = StripHtml(tp.Text ?? string.Empty);
                    }

                    var preview = ExtractPreviewLines(text, maxLines);
                    if (!string.IsNullOrEmpty(preview)) result[s.UniqueId.Id.ToString(CultureInfo.InvariantCulture)] = preview;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { LogService.Log($"FetchPreview/{s.UniqueId}", ex); }
            }
            return (IReadOnlyDictionary<string, string>)result;
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── IDLE watchers ────────────────────────────────────────────────────────────

    public void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default)
    {
        foreach (var account in accounts)
        {
            // Cancel any existing watcher for this account.
            if (_idleCts.TryRemove(account.Id, out var old))
            {
                try { old.Cancel(); old.Dispose(); } catch { }
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _idleCts[account.Id] = linked;

            // Copy for capture — account object in _accounts may be updated later.
            var accountId = account.Id;
            _ = Task.Run(() => RunIdleWatcherAsync(accountId, linked.Token), linked.Token);
        }
    }

    public void StopIdleWatchers()
    {
        foreach (var kvp in _idleCts)
        {
            if (_idleCts.TryRemove(kvp.Key, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
        }
    }

    private async Task RunIdleWatcherAsync(Guid accountId, CancellationToken ct)
    {
        // Retry loop — reconnect on transient failures with exponential backoff.
        while (!ct.IsCancellationRequested)
        {
            ImapClient? client = null;
            IMailFolder? inbox  = null;
            try
            {
                if (!_accounts.TryGetValue(accountId, out var account))
                    return; // account removed — exit permanently

                _passwords.TryGetValue(accountId, out var password);
                client = await CreateAuthenticatedClientAsync(account, password, ct);

                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    LogService.Log($"IDLE watcher [{account.Username}]: server does not support IDLE — watcher disabled.");
                    DisposeClient(client);
                    return;
                }

                inbox = client.Inbox ?? throw new InvalidOperationException("Server has no INBOX.");
                await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

                LogService.Log($"IDLE watcher [{account.Username}]: watching INBOX (Count={inbox.Count}).");

                // Successfully connected — reset retry count and fire reachability event.
                if (_idleRetryCount.TryGetValue(accountId, out var retryCount) && retryCount > 0)
                {
                    _idleRetryCount[accountId] = 0;
                    AccountReachabilityChanged?.Invoke(accountId, true);
                }

                inbox.CountChanged += OnCountChanged;
                try
                {
                    // Re-IDLE every 25 minutes — most servers time out at 30 minutes.
                    while (!ct.IsCancellationRequested)
                    {
                        using var idleTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
                        using var combinedCts    = CancellationTokenSource.CreateLinkedTokenSource(ct, idleTimeoutCts.Token);
                        try
                        {
                            await client.IdleAsync(combinedCts.Token, ct);
                        }
                        catch (OperationCanceledException) when (idleTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            // 25-minute timeout fired — send NOOP to keep the connection alive then re-enter IDLE.
                            await client.NoOpAsync(ct);
                        }
                    }
                }
                finally
                {
                    inbox.CountChanged -= OnCountChanged;
                }
            }
            catch (OperationCanceledException)
            {
                break; // ct cancelled — clean exit
            }
            catch (Exception ex)
            {
                // Exponential backoff: 30s, 60s, 120s cap
                var retryCount = _idleRetryCount.AddOrUpdate(accountId, 1, (_, rc) => rc + 1);
                var delaySeconds = retryCount == 1 ? 30 : retryCount == 2 ? 60 : 120;

                LogService.Log($"IDLE watcher [{accountId}] error (attempt {retryCount}) — will retry in {delaySeconds}s: {ex.Message}");

                // Mark account as unreachable on first retry failure
                if (retryCount == 1)
                    AccountReachabilityChanged?.Invoke(accountId, false);

                if (client != null) DisposeClient(client);
                client = null;

                try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (inbox != null)
                    try { await inbox.CloseAsync(false, CancellationToken.None); } catch { }
                if (client != null)
                    await DisconnectAndDisposeAsync(client, CancellationToken.None);
            }
        }

        void OnCountChanged(object? sender, EventArgs e)
        {
            LogService.Log($"IDLE watcher [{accountId}]: CountChanged — new mail detected.");
            InboxNewMailDetected?.Invoke(accountId);
        }
    }

    public async Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Background);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try   { await folder.CheckAsync(ct); return folder.Unread; }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Attachment download ───────────────────────────────────────────────────────

    public async Task<byte[]> DownloadAttachmentAsync(
        Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            var mailKitUid = ToUid(messageId);
            var summaries  = await folder.FetchAsync(
                new[] { mailKitUid },
                MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure,
                ct);

            var s        = summaries.FirstOrDefault()
                ?? throw new InvalidOperationException($"Message UID {messageId} not found.");
            var bodyPart = FindBodyPartBySpecifier(s.Body, partSpecifier)
                ?? throw new InvalidOperationException($"Body part '{partSpecifier}' not found.");

            var decoded = await folder.GetBodyPartAsync(mailKitUid, bodyPart, ct);
            using var stream = new MemoryStream();
            if (decoded is MimePart mp)
                mp.Content!.DecodeTo(stream);
            else if (decoded is MessagePart msgPart)
                await msgPart.Message!.WriteToAsync(stream, ct);
            return stream.ToArray();
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Leases one authenticated client for a single IMAP operation. MailKit clients
    /// are single-command objects, so every concurrent operation gets its own client.
    /// </summary>
    private async Task<ImapClientLease> RentClientAsync(
        Guid accountId, CancellationToken ct, ImapLeasePriority priority)
    {
        ThrowIfDisposed();

        if (!_pools.TryGetValue(accountId, out var pool))
        {
            if (!_accounts.TryGetValue(accountId, out var account))
                throw new InvalidOperationException($"Account {accountId} is not connected.");

            _passwords.TryGetValue(accountId, out var password);
            await ConnectAsync(account, password, ct);
            if (!_pools.TryGetValue(accountId, out pool))
                throw new InvalidOperationException($"Account {accountId} is not connected.");
        }

        return await pool.RentAsync(priority, ct);
    }

    private async Task<ImapClient> CreateAuthenticatedClientAsync(
        AccountModel account, string? password, CancellationToken ct)
    {
        var client = new ImapClient();
        try
        {
            if (account.ImapAcceptInvalidCert)
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;

            var ssl = account.ImapUseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;

            LogService.Log($"Connecting to {account.ImapHost}:{account.ImapPort} ssl={account.ImapUseSsl} auth={account.AuthType}");
            LogService.Debug($"  user={account.Username}");
            await client.ConnectAsync(account.ImapHost, account.ImapPort, ssl, ct);

            if (account.AuthType == AuthType.OAuth2Microsoft)
            {
                var token = await _oauth.GetAccessTokenAsync(account, ct);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
            }
            else
            {
                if (password is null)
                    throw new InvalidOperationException($"No password is available for account {account.Username}.");
                await client.AuthenticateAsync(account.Username, password, ct);
            }

            LogService.Log($"Connected. Capabilities: {client.Capabilities}");
            return client;
        }
        catch
        {
            DisposeClient(client);
            throw;
        }
    }

    private int GetMaxConnectionsPerAccount()
    {
        var configured = _config?.Load().MaxImapConnectionsPerAccount
                         ?? DefaultMaxConnectionsPerAccount;
        return Math.Clamp(configured, 1, AbsoluteMaxConnectionsPerAccount);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ImapMailService));
    }

    private static bool SameConnectionSettings(AccountModel left, AccountModel right) =>
        left.Id == right.Id &&
        left.Username == right.Username &&
        left.AuthType == right.AuthType &&
        left.ImapHost == right.ImapHost &&
        left.ImapPort == right.ImapPort &&
        left.ImapUseSsl == right.ImapUseSsl &&
        left.ImapAcceptInvalidCert == right.ImapAcceptInvalidCert;

    private static AccountModel CloneAccount(AccountModel account) =>
        new()
        {
            Id                    = account.Id,
            AccountName           = account.AccountName,
            DisplayName           = account.DisplayName,
            Username              = account.Username,
            AuthType              = account.AuthType,
            ImapHost              = account.ImapHost,
            ImapPort              = account.ImapPort,
            ImapUseSsl            = account.ImapUseSsl,
            ImapAcceptInvalidCert = account.ImapAcceptInvalidCert,
            SmtpHost              = account.SmtpHost,
            SmtpPort              = account.SmtpPort,
            SmtpUseSsl            = account.SmtpUseSsl,
            SmtpAcceptInvalidCert = account.SmtpAcceptInvalidCert,
            IsDefault             = account.IsDefault,
        };

    private static bool IsClientUsable(ImapClient client)
    {
        try { return client.IsConnected && client.IsAuthenticated; }
        catch { return false; }
    }

    private static void DisposeClient(ImapClient client)
    {
        try { client.Dispose(); } catch { /* best effort */ }
    }

    private static async Task DisconnectAndDisposeAsync(ImapClient client, CancellationToken ct)
    {
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) { LogService.Log("IMAP disconnect", ex); }
        finally { DisposeClient(client); }
    }

    private sealed class ImapClientLease : IDisposable
    {
        private AccountConnectionPool? _pool;

        public ImapClientLease(
            AccountConnectionPool pool, ImapClient client, bool releaseBackgroundSlot)
        {
            _pool = pool;
            Client = client;
            ReleaseBackgroundSlot = releaseBackgroundSlot;
        }

        public ImapClient Client { get; }
        private bool ReleaseBackgroundSlot { get; }

        public void Dispose()
        {
            var pool = Interlocked.Exchange(ref _pool, null);
            pool?.Return(Client, ReleaseBackgroundSlot);
        }
    }

    private sealed class AccountConnectionPool : IDisposable
    {
        private readonly ImapMailService _owner;
        private readonly SemaphoreSlim _slots;
        private readonly SemaphoreSlim _backgroundSlots;
        private readonly object _gate = new();
        private readonly Stack<ImapClient> _idle = new();
        private readonly HashSet<ImapClient> _all = new();
        private bool _disposed;

        public AccountConnectionPool(
            ImapMailService owner, AccountModel account, string? password, int maxConnections)
        {
            _owner = owner;
            Account = CloneAccount(account);
            Password = password;
            MaxConnections = maxConnections;
            _slots = new SemaphoreSlim(maxConnections, maxConnections);
            var reservedForegroundSlots = maxConnections >= 3
                ? Math.Min(ForegroundReservedConnectionCount, maxConnections - 1)
                : 0;
            var backgroundCapacity = Math.Max(1, maxConnections - reservedForegroundSlots);
            _backgroundSlots = new SemaphoreSlim(backgroundCapacity, backgroundCapacity);
        }

        public AccountModel Account { get; private set; }
        public string? Password { get; private set; }
        public int MaxConnections { get; }

        public bool Matches(AccountModel account, int maxConnections) =>
            MaxConnections == maxConnections && SameConnectionSettings(Account, account);

        public void UpdateAccount(AccountModel account, string? password)
        {
            lock (_gate)
            {
                if (_disposed) return;
                Account = CloneAccount(account);
                if (password is not null)
                    Password = password;
            }
        }

        public async Task<ImapClientLease> RentAsync(ImapLeasePriority priority, CancellationToken ct)
        {
            var hasBackgroundSlot = false;
            if (priority == ImapLeasePriority.Background)
            {
                await _backgroundSlots.WaitAsync(ct);
                hasBackgroundSlot = true;
            }

            try
            {
                await _slots.WaitAsync(ct);
            }
            catch
            {
                if (hasBackgroundSlot)
                    _backgroundSlots.Release();
                throw;
            }

            ImapClient? client = null;

            try
            {
                while (client == null)
                {
                    AccountModel account;
                    string? password;

                    lock (_gate)
                    {
                        if (_disposed)
                            throw new ObjectDisposedException(nameof(AccountConnectionPool));

                        while (_idle.Count > 0)
                        {
                            var candidate = _idle.Pop();
                            if (IsClientUsable(candidate))
                            {
                                client = candidate;
                                break;
                            }

                            _all.Remove(candidate);
                            DisposeClient(candidate);
                        }

                        if (client != null)
                            break;

                        account = CloneAccount(Account);
                        password = Password;
                    }

                    client = await _owner.CreateAuthenticatedClientAsync(account, password, ct);

                    lock (_gate)
                    {
                        if (_disposed)
                            throw new ObjectDisposedException(nameof(AccountConnectionPool));
                        _all.Add(client);
                    }
                }

                return new ImapClientLease(this, client, hasBackgroundSlot);
            }
            catch
            {
                if (client != null)
                {
                    lock (_gate) { _all.Remove(client); }
                    DisposeClient(client);
                }
                _slots.Release();
                if (hasBackgroundSlot)
                    _backgroundSlots.Release();
                throw;
            }
        }

        public void Return(ImapClient client, bool releaseBackgroundSlot)
        {
            var keep = false;

            lock (_gate)
            {
                if (!_disposed && IsClientUsable(client))
                {
                    _idle.Push(client);
                    keep = true;
                }
                else
                {
                    _all.Remove(client);
                }
            }

            if (!keep)
                DisposeClient(client);

            _slots.Release();
            if (releaseBackgroundSlot)
                _backgroundSlots.Release();
        }

        public async Task DisconnectAsync(CancellationToken ct)
        {
            List<ImapClient> clients;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                clients = _all.ToList();
                _idle.Clear();
                _all.Clear();
            }

            foreach (var client in clients)
                await DisconnectAndDisposeAsync(client, ct);
        }

        public void Dispose()
        {
            List<ImapClient> clients;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                clients = _all.ToList();
                _idle.Clear();
                _all.Clear();
            }

            foreach (var client in clients)
                DisposeClient(client);
        }
    }

    // IMAP message keys are decimal UID strings (MailMessageSummary.MessageId). Parse one back to
    // a MailKit UniqueId at the service boundary.
    private static UniqueId ToUid(string messageId)
    {
        Debug.Assert(!string.IsNullOrEmpty(messageId), "IMAP MessageId must be a non-empty decimal UID string");
        return new UniqueId(uint.Parse(messageId, CultureInfo.InvariantCulture));
    }

    private static MailMessageSummary SummaryToModel(IMessageSummary s, Guid accountId, string folderName) =>
        new()
        {
            MessageId   = s.UniqueId.Id.ToString(CultureInfo.InvariantCulture),
            AccountId   = accountId,
            FolderName  = folderName,
            From        = FormatAddressListDisplay(s.Envelope?.From),
            To          = FormatAddressList(s.Envelope?.To),
            Subject     = s.Envelope?.Subject ?? "(no subject)",
            Date        = s.Envelope?.Date ?? DateTimeOffset.MinValue,
            IsRead      = (s.Flags & MessageFlags.Seen)     != 0,
            IsReplied   = (s.Flags & MessageFlags.Answered) != 0,
            IsForwarded = s.Keywords?.Any(k =>
                              k.Equals("$Forwarded", StringComparison.OrdinalIgnoreCase) ||
                              k.Equals("Forwarded",  StringComparison.OrdinalIgnoreCase)) == true,
            Preview       = s.PreviewText ?? string.Empty,   // populated when server supports IMAP PREVIEW
            IsMailingList = !string.IsNullOrEmpty(s.Headers?["List-Id"]),
        };

    private static async Task<IMailFolder?> FindSpecialFolderAsync(
        ImapClient client, CancellationToken ct, params SpecialFolder[] candidates)
    {
        // Attribute-based lookup (fast, no network round-trip)
        foreach (var sf in candidates)
        {
            try { return client.GetFolder(sf); }
            catch { }
        }

        // Name-based fallback for servers that don't advertise special-use attributes (e.g. Courier IMAP)
        var names = candidates
            .SelectMany<SpecialFolder, string>(sf => sf switch
            {
                SpecialFolder.Trash  => ["Trash"],
                SpecialFolder.Junk   => ["Junk", "Spam"],
                SpecialFolder.Sent   => ["Sent"],
                SpecialFolder.Drafts => ["Drafts"],
                _                   => []
            })
            .Distinct()
            .ToArray();

        if (names.Length == 0) return null;

        foreach (var ns in client.PersonalNamespaces)
        {
            IMailFolder root;
            try { root = client.GetFolder(ns); }
            catch { continue; }

            foreach (var name in names)
            {
                try { return await root.GetSubfolderAsync(name, ct); }
                catch { }
            }
        }
        return null;
    }

    private static List<AttachmentModel> ExtractAttachments(BodyPart? body)
    {
        var result = new List<AttachmentModel>();
        if (body != null) CollectAttachments(body, result);
        return result;
    }

    private static void CollectAttachments(BodyPart part, List<AttachmentModel> result)
    {
        if (part is BodyPartMultipart multi)
        {
            // Skip alternative (body variants) and related (inline images) — recurse into everything else
            var subtype = multi.ContentType.MediaSubtype;
            if (subtype.Equals("alternative", StringComparison.OrdinalIgnoreCase) ||
                subtype.Equals("related",     StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var child in multi.BodyParts)
                CollectAttachments(child, result);
        }
        else if (part is BodyPartBasic basic)
        {
            var disposition = basic.ContentDisposition?.Disposition ?? string.Empty;
            var fileName    = basic.ContentDisposition?.FileName ?? basic.ContentType.Name;

            bool isExplicit = disposition.Equals("attachment", StringComparison.OrdinalIgnoreCase);
            bool isTextBody = basic is BodyPartText bt &&
                              (bt.ContentType.MediaSubtype.Equals("plain", StringComparison.OrdinalIgnoreCase) ||
                               bt.ContentType.MediaSubtype.Equals("html",  StringComparison.OrdinalIgnoreCase));

            if (isExplicit || (!isTextBody && !string.IsNullOrEmpty(fileName)))
            {
                result.Add(new AttachmentModel
                {
                    FileName      = fileName ?? $"attachment.{basic.ContentType.MediaSubtype}",
                    ContentType   = basic.ContentType.MimeType,
                    FileSize      = (long)basic.Octets,
                    PartSpecifier = basic.PartSpecifier,
                });
            }
        }
    }

    private static BodyPart? FindBodyPartBySpecifier(BodyPart? part, string specifier)
    {
        if (part == null) return null;
        if (part.PartSpecifier == specifier) return part;
        if (part is BodyPartMultipart multi)
            foreach (var child in multi.BodyParts)
            {
                var found = FindBodyPartBySpecifier(child, specifier);
                if (found != null) return found;
            }
        return null;
    }

    /// <summary>
    /// Finds the first text/calendar body part in the MIME tree, if any.
    /// Returns null if no calendar part is present.
    /// </summary>
    private static BodyPart? FindCalendarPart(BodyPart? part)
    {
        if (part == null) return null;
        if (part is BodyPartBasic basic &&
            basic.ContentType.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase) &&
            basic.ContentType.MediaSubtype.Equals("calendar", StringComparison.OrdinalIgnoreCase))
            return part;
        if (part is BodyPartMultipart multi)
            foreach (var child in multi.BodyParts)
            {
                var found = FindCalendarPart(child);
                if (found != null) return found;
            }
        return null;
    }

    private static string ExtractPreviewLines(string text, int maxLines) =>
        string.Join(" ", text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(maxLines));

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")
            .Replace("&nbsp;", " ").Replace("&amp;", "&")
            .Replace("&lt;", "<").Replace("&gt;", ">")
            .Trim();
    }

    private static bool IsExcludedFromAllMail(FolderAttributes attrs, string fullName)
    {
        var kind = GetSpecialFolderKind(attrs, fullName);
        return kind is SpecialFolderKind.Trash or SpecialFolderKind.Junk
                    or SpecialFolderKind.Sent  or SpecialFolderKind.Drafts;
    }

    private static SpecialFolderKind GetSpecialFolderKind(FolderAttributes attrs, string fullName)
    {
        if ((attrs & FolderAttributes.Trash)  != 0) return SpecialFolderKind.Trash;
        if ((attrs & FolderAttributes.Junk)   != 0) return SpecialFolderKind.Junk;
        if ((attrs & FolderAttributes.Sent)   != 0) return SpecialFolderKind.Sent;
        if ((attrs & FolderAttributes.Drafts) != 0) return SpecialFolderKind.Drafts;
        // Name-based fallback for servers that don't advertise special-use attributes (e.g. Courier IMAP)
        var leaf = fullName.Split('/', '.').LastOrDefault() ?? fullName;
        if (leaf.Equals("Trash",  StringComparison.OrdinalIgnoreCase)) return SpecialFolderKind.Trash;
        if (leaf.Equals("Junk",   StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("Spam",   StringComparison.OrdinalIgnoreCase)) return SpecialFolderKind.Junk;
        if (leaf.Equals("Sent",   StringComparison.OrdinalIgnoreCase)) return SpecialFolderKind.Sent;
        if (leaf.Equals("Drafts", StringComparison.OrdinalIgnoreCase)) return SpecialFolderKind.Drafts;
        return SpecialFolderKind.None;
    }

    private static readonly string[] _mailingListHeaders = { "List-Id" };

    private static ComposeMode ParseComposeMode(string? header) => header?.ToLowerInvariant() switch
    {
        "markdown" => ComposeMode.Markdown,
        "html"     => ComposeMode.Html,
        _          => ComposeMode.PlainText,
    };

    private static string FormatAddressList(InternetAddressList? list) =>
        list == null || list.Count == 0
            ? string.Empty
            : string.Join(", ", list.Select(a => a.ToString()));

    private static string FormatAddressListDisplay(InternetAddressList? list) =>
        list == null || list.Count == 0
            ? string.Empty
            : string.Join(", ", list.Select(a =>
                a is MailboxAddress mb && !string.IsNullOrWhiteSpace(mb.Name)
                    ? mb.Name
                    : a.ToString()));

    public async Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Foreground);
        var client = lease.Client;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var uidList = messageIds.Select(ToUid).ToList();
            await folder.AddFlagsAsync(uidList, MessageFlags.Deleted, true, ct);
            await folder.ExpungeAsync(ct);
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task NoOpAsync(Guid accountId, CancellationToken ct = default)
    {
        if (!_pools.ContainsKey(accountId)) return;
        try
        {
            using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Background);
            await lease.Client.NoOpAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Pool will discard the dead client on Return via IsClientUsable; just log.
            LogService.Log($"NoOp failed for {accountId}: {ex.GetType().Name}");
        }
    }

    public async Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default)
    {
        if (!_pools.ContainsKey(accountId)) return 0;
        try
        {
            using var lease = await RentClientAsync(accountId, ct, ImapLeasePriority.Background);
            var client = lease.Client;
            var trashFolder = await FindSpecialFolderAsync(client, ct, SpecialFolder.Trash, SpecialFolder.Junk);
            if (trashFolder == null) return 0;

            await trashFolder.OpenAsync(FolderAccess.ReadOnly, ct);
            try
            {
                var uids = await trashFolder.SearchAsync(SearchQuery.All, ct);
                return uids.Count;
            }
            finally
            {
                await trashFolder.CloseAsync(false, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Log($"CountTrashMessages failed for {accountId}: {ex.GetType().Name}");
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopIdleWatchers();
        foreach (var pool in _pools.Values)
            pool.Dispose();
        _pools.Clear();
    }
}
