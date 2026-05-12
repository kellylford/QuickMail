using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public class ImapService : IImapService
{
    private readonly IOAuthService _oauth;
    private readonly ConcurrentDictionary<Guid, ImapClient> _clients   = new();
    private readonly ConcurrentDictionary<Guid, AccountModel> _accounts = new();
    private readonly ConcurrentDictionary<Guid, string>  _passwords     = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks   = new();
    private bool _disposed;

    public ImapService(IOAuthService oauth) => _oauth = oauth;

    // ── Connect / disconnect ─────────────────────────────────────────────────────

    public async Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
    {
        if (_clients.TryGetValue(account.Id, out var existing))
        {
            if (existing.IsAuthenticated) return;
            existing.Dispose();
            _clients.TryRemove(account.Id, out _);
        }

        var client = new ImapClient();

        if (account.ImapAcceptInvalidCert)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var ssl = account.ImapUseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        LogService.Log($"Connecting to {account.ImapHost}:{account.ImapPort} ssl={account.ImapUseSsl} user={account.Username} auth={account.AuthType}");
        await client.ConnectAsync(account.ImapHost, account.ImapPort, ssl, ct);

        if (account.AuthType == AuthType.OAuth2Microsoft)
        {
            var token = await _oauth.GetAccessTokenAsync(account, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
        }
        else
        {
            await client.AuthenticateAsync(account.Username, password!, ct);
        }

        LogService.Log($"Connected. Capabilities: {client.Capabilities}");

        _clients[account.Id]  = client;
        _accounts[account.Id] = account;
        if (password is not null)
            _passwords[account.Id] = password;
    }

    public async Task DisconnectAsync(Guid accountId, CancellationToken ct = default)
    {
        if (_clients.TryGetValue(accountId, out var client))
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true, ct);
            client.Dispose();
            _clients.TryRemove(accountId, out _);
            _accounts.TryRemove(accountId, out _);
            _passwords.TryRemove(accountId, out _);
        }
    }

    // ── Folder list ──────────────────────────────────────────────────────────────

    public async Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        var client = await GetOrReconnectAsync(accountId, ct);
        var result = new List<MailFolderModel>();

        // Always put INBOX first — many servers don't return it via GetFoldersAsync
        try
        {
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            LogService.Log($"INBOX: FullName={inbox.FullName} Count={inbox.Count} Unread={inbox.Unread}");
            result.Add(new MailFolderModel
            {
                FullName    = inbox.FullName,
                DisplayName = "Inbox",
                UnreadCount = inbox.Unread,
                AccountId   = accountId
            });
            await inbox.CloseAsync(false, ct);
        }
        catch (Exception ex) { LogService.Log("GetFolders/Inbox", ex); }

        var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: ct);
        LogService.Log($"GetFoldersAsync returned {folders.Count} folders");

        foreach (var folder in folders)
        {
            if ((folder.Attributes & FolderAttributes.NonExistent) != 0) continue;
            if ((folder.Attributes & FolderAttributes.NoSelect)    != 0) continue;
            if (folder.FullName == client.Inbox.FullName)               continue;

            try
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, ct);
                LogService.Log($"  Folder: {folder.FullName} Count={folder.Count} Unread={folder.Unread}");
                var unread   = folder.Unread;
                var excluded = IsExcludedFromAllMail(folder.Attributes);
                await folder.CloseAsync(false, ct);
                result.Add(new MailFolderModel
                {
                    FullName           = folder.FullName,
                    DisplayName        = folder.Name,
                    UnreadCount        = unread,
                    AccountId          = accountId,
                    ExcludeFromAllMail = excluded
                });
            }
            catch (Exception ex)
            {
                LogService.Log($"  Cannot open folder {folder.FullName}: {ex.Message}");
                result.Add(new MailFolderModel
                {
                    FullName           = folder.FullName,
                    DisplayName        = folder.Name,
                    UnreadCount        = 0,
                    AccountId          = accountId,
                    ExcludeFromAllMail = IsExcludedFromAllMail(folder.Attributes)
                });
            }
        }

        LogService.Log($"GetFoldersAsync: returning {result.Count} folders");
        return result;
    }

    // ── Message lists ────────────────────────────────────────────────────────────

    public async Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
    {
        LogService.Log($"GetMessageSummaries: folder={folderName} maxMessages={maxMessages}");
        var client = await GetOrReconnectAsync(accountId, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        LogService.Log($"  Opened. Count={folder.Count} Unread={folder.Unread}");

        try
        {
            if (folder.Count == 0) return [];

            var startIndex = Math.Max(0, folder.Count - maxMessages);
            var summaries  = await folder.FetchAsync(
                startIndex, -1,
                MessageSummaryItems.UniqueId
                | MessageSummaryItems.Envelope
                | MessageSummaryItems.Flags
                | MessageSummaryItems.PreviewText,   // free if server supports PREVIEW
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

    public async Task<List<MailMessageSummary>> GetMessagesSinceAsync(
        Guid accountId, string folderName, uint sinceUid, CancellationToken ct = default)
    {
        var client = await GetOrReconnectAsync(accountId, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            var items = MessageSummaryItems.UniqueId
                      | MessageSummaryItems.Envelope
                      | MessageSummaryItems.Flags
                      | MessageSummaryItems.PreviewText;   // free if server supports PREVIEW

            IList<IMessageSummary> summaries;
            if (sinceUid == 0)
            {
                if (folder.Count == 0) return [];
                var startIndex = Math.Max(0, folder.Count - 500);
                summaries = await folder.FetchAsync(startIndex, -1, items, ct);
            }
            else
            {
                var range = new UniqueIdRange(new UniqueId(sinceUid + 1), UniqueId.MaxValue);
                summaries = await folder.FetchAsync((IList<UniqueId>)range, items, ct);
            }

            return summaries.Select(s => SummaryToModel(s, accountId, folderName)).ToList();
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Message detail ───────────────────────────────────────────────────────────

    public async Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, uint uid, CancellationToken ct = default)
    {
        var client    = await GetOrReconnectAsync(accountId, ct);
        var folder    = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);

        try
        {
            var mailKitUid = new UniqueId(uid);
            var summaries  = await folder.FetchAsync(
                new[] { mailKitUid },
                MessageSummaryItems.UniqueId
                | MessageSummaryItems.Envelope
                | MessageSummaryItems.Flags
                | MessageSummaryItems.BodyStructure,
                ct);

            var s = summaries.FirstOrDefault()
                ?? throw new InvalidOperationException($"Message UID {uid} not found.");

            string plainText = string.Empty;
            string htmlText  = string.Empty;

            if (s.HtmlBody != null)
            {
                var bodyPart = await folder.GetBodyPartAsync(mailKitUid, s.HtmlBody, ct);
                if (bodyPart is TextPart tp) htmlText = tp.Text ?? string.Empty;
            }

            if (s.TextBody != null)
            {
                var bodyPart = await folder.GetBodyPartAsync(mailKitUid, s.TextBody, ct);
                if (bodyPart is TextPart tp) plainText = tp.Text ?? string.Empty;
            }

            await folder.AddFlagsAsync(mailKitUid, MessageFlags.Seen, true, ct);

            return new MailMessageDetail
            {
                UniqueId      = uid,
                AccountId     = accountId,
                FolderName    = folderName,
                From          = FormatAddressList(s.Envelope?.From),
                To            = FormatAddressList(s.Envelope?.To),
                Cc            = FormatAddressList(s.Envelope?.Cc),
                ReplyTo       = FormatAddressList(s.Envelope?.ReplyTo),
                Subject       = s.Envelope?.Subject ?? "(no subject)",
                Date          = s.Envelope?.Date ?? DateTimeOffset.MinValue,
                IsRead        = true,
                MessageId     = s.Envelope?.MessageId ?? string.Empty,
                PlainTextBody = plainText,
                HtmlBody      = htmlText
            };
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Mutations ────────────────────────────────────────────────────────────────

    public async Task MarkReadAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default)
    {
        var client = await GetOrReconnectAsync(accountId, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try   { await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Seen, true, ct); }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default)
    {
        var client  = await GetOrReconnectAsync(accountId, ct);
        var folder  = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var uidList = uids.Select(u => new UniqueId(u)).ToList();
            var trash   = FindSpecialFolder(client, SpecialFolder.Trash, SpecialFolder.Junk);
            if (trash != null) await folder.MoveToAsync(uidList, trash, ct);
            else               await folder.AddFlagsAsync(uidList, MessageFlags.Deleted, true, ct);
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task MoveToTrashAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default)
    {
        var client = await GetOrReconnectAsync(accountId, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var trash = FindSpecialFolder(client, SpecialFolder.Trash, SpecialFolder.Junk);
            if (trash != null) await folder.MoveToAsync(new UniqueId(uid), trash, ct);
            else               await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Deleted, true, ct);
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    public async Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
    {
        var client = await GetOrReconnectAsync(accountId, ct);
        var trash  = FindSpecialFolder(client, SpecialFolder.Trash, SpecialFolder.Junk);

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

    public async Task<IList<uint>> GetFolderUidsAsync(Guid accountId, string folderName, CancellationToken ct = default)
    {
        var client = await GetOrReconnectAsync(accountId, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            if (folder.Count == 0) return [];
            var uids = await folder.SearchAsync(SearchQuery.All, ct);
            return uids.Select(u => u.Id).ToList();
        }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Body-download preview fallback (used when server lacks IMAP PREVIEW) ────

    public async Task<IReadOnlyDictionary<uint, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<uint> uids,
        int maxLines, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, string>();
        if (uids.Count == 0 || maxLines <= 0) return result;

        var client = await GetOrReconnectAsync(accountId, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            var mailKitUids = uids.Select(u => new UniqueId(u)).ToList();
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
                    if (!string.IsNullOrEmpty(preview)) result[s.UniqueId.Id] = preview;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { LogService.Log($"FetchPreview/{s.UniqueId}", ex); }
            }
        }
        finally { await folder.CloseAsync(false, ct); }
        return result;
    }

    public async Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default)
    {
        var client = await GetOrReconnectAsync(accountId, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try   { await folder.CheckAsync(ct); return folder.Unread; }
        finally { await folder.CloseAsync(false, ct); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns an authenticated client, reconnecting transparently if the connection dropped.
    /// </summary>
    private async Task<ImapClient> GetOrReconnectAsync(Guid accountId, CancellationToken ct)
    {
        if (_clients.TryGetValue(accountId, out var client) && client.IsAuthenticated)
            return client;

        if (!_accounts.TryGetValue(accountId, out var account))
            throw new InvalidOperationException($"Account {accountId} is not connected.");

        string? password = null;
        if (account.AuthType == AuthType.Password && !_passwords.TryGetValue(accountId, out password))
            throw new InvalidOperationException($"Account {accountId} is not connected.");

        // One reconnect attempt at a time per account
        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // Re-check after acquiring — another waiter may have reconnected already
            if (_clients.TryGetValue(accountId, out client) && client.IsAuthenticated)
                return client;

            LogService.Log($"Reconnecting {account.Username}…");
            await ConnectAsync(account, password, ct);
            return _clients[accountId];
        }
        finally { sem.Release(); }
    }

    private static MailMessageSummary SummaryToModel(IMessageSummary s, Guid accountId, string folderName) =>
        new()
        {
            UniqueId    = s.UniqueId.Id,
            AccountId   = accountId,
            FolderName  = folderName,
            From        = FormatAddressListDisplay(s.Envelope?.From),
            Subject     = s.Envelope?.Subject ?? "(no subject)",
            Date        = s.Envelope?.Date ?? DateTimeOffset.MinValue,
            IsRead      = (s.Flags & MessageFlags.Seen)     != 0,
            IsReplied   = (s.Flags & MessageFlags.Answered) != 0,
            IsForwarded = s.Keywords?.Any(k =>
                              k.Equals("$Forwarded", StringComparison.OrdinalIgnoreCase) ||
                              k.Equals("Forwarded",  StringComparison.OrdinalIgnoreCase)) == true,
            Preview     = s.PreviewText ?? string.Empty,   // populated when server supports IMAP PREVIEW
        };

    private static IMailFolder? FindSpecialFolder(ImapClient client, params SpecialFolder[] candidates)
    {
        foreach (var sf in candidates)
        {
            try { return client.GetFolder(sf); }
            catch { /* not available */ }
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

    private static bool IsExcludedFromAllMail(FolderAttributes attrs) =>
        (attrs & (FolderAttributes.Trash | FolderAttributes.Junk |
                  FolderAttributes.Sent  | FolderAttributes.Drafts)) != 0;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var client in _clients.Values)
        {
            try { client.Dispose(); } catch { /* best effort */ }
        }
        _clients.Clear();
    }
}
