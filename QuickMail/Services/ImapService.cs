using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var result = new List<MailFolderModel>();

            // Always put INBOX first — many servers don't return it via GetFoldersAsync
            try
            {
                var inbox = client.Inbox!;
                await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                LogService.Log($"INBOX: FullName={inbox.FullName} Count={inbox.Count} Unread={inbox.Unread}");
                result.Add(new MailFolderModel
                {
                    FullName    = inbox.FullName,
                    DisplayName = "Inbox",
                    UnreadCount = inbox.Unread,
                    AccountId   = accountId,
                    Kind        = SpecialFolderKind.Inbox
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
                if (folder.FullName == client.Inbox!.FullName)              continue;

                try
                {
                    await folder.OpenAsync(FolderAccess.ReadOnly, ct);
                    LogService.Log($"  Folder: {folder.FullName} Count={folder.Count} Unread={folder.Unread}");
                    var unread   = folder.Unread;
                    var excluded = IsExcludedFromAllMail(folder.Attributes);
                    var kind     = GetSpecialFolderKind(folder.Attributes);
                    await folder.CloseAsync(false, ct);
                    result.Add(new MailFolderModel
                    {
                        FullName           = folder.FullName,
                        DisplayName        = folder.Name,
                        UnreadCount        = unread,
                        AccountId          = accountId,
                        ExcludeFromAllMail = excluded,
                        Kind               = kind
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
                        ExcludeFromAllMail = IsExcludedFromAllMail(folder.Attributes),
                        Kind               = GetSpecialFolderKind(folder.Attributes)
                    });
                }
            }

            LogService.Log($"GetFoldersAsync: returning {result.Count} folders");
            return result;
        });

    // ── Message lists ────────────────────────────────────────────────────────────

    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
    {
        LogService.Log($"GetMessageSummaries: folder={folderName} maxMessages={maxMessages}");
        return ExecuteWithRetryAsync(accountId, ct, async client =>
        {
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
                    ct);

                var result = summaries
                    .OrderByDescending(s => s.Envelope?.Date ?? DateTimeOffset.MinValue)
                    .Select(s => SummaryToModel(s, accountId, folderName))
                    .ToList();

                LogService.Log($"  Returning {result.Count} summaries (preview={result.Count(r => !string.IsNullOrEmpty(r.Preview))} prefilled)");
                return result;
            }
            finally { await folder.CloseAsync(false, ct); }
        });
    }

    public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(
        Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
    {
        LogService.Log($"GetMessagesSinceDate: folder={folderName} since={since:yyyy-MM-dd}");
        return ExecuteWithRetryAsync(accountId, ct, async client =>
        {
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

                var summaries = await folder.FetchAsync(uids, items, ct);
                var result    = summaries
                    .OrderByDescending(s => s.Envelope?.Date ?? DateTimeOffset.MinValue)
                    .Select(s => SummaryToModel(s, accountId, folderName))
                    .ToList();

                LogService.Log($"  Returning {result.Count} summaries since {since:yyyy-MM-dd}");
                return result;
            }
            finally { await folder.CloseAsync(false, ct); }
        });
    }

    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(
        Guid accountId, string folderName, uint sinceUid, int initialCount, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
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
                    if (folder.Count == 0) return new List<MailMessageSummary>();
                    var count = initialCount > 0 ? initialCount : folder.Count;
                    var startIndex = Math.Max(0, folder.Count - count);
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
        });

    // ── Message detail ───────────────────────────────────────────────────────────

    public Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, uint uid, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
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

                var attachments = ExtractAttachments(s.Body);

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
                    HtmlBody      = htmlText,
                    Attachments   = attachments,
                };
            }
            finally { await folder.CloseAsync(false, ct); }
        });

    // ── Mutations ────────────────────────────────────────────────────────────────

    public Task MarkReadAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            try   { await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Seen, true, ct); }
            finally { await folder.CloseAsync(false, ct); }
        });

    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default)
        => ExecuteWithRetryAsync<bool>(accountId, ct, async client =>
        {
            var folder  = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            try
            {
                var uidList = uids.Select(u => new UniqueId(u)).ToList();
                var trash   = FindSpecialFolder(client, SpecialFolder.Trash, SpecialFolder.Junk);
                if (trash != null) await folder.MoveToAsync(uidList, trash, ct);
                else               await folder.AddFlagsAsync(uidList, MessageFlags.Deleted, true, ct);
                return true;
            }
            finally { await folder.CloseAsync(false, ct); }
        });

    public Task MoveToTrashAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            try
            {
                var trash = FindSpecialFolder(client, SpecialFolder.Trash, SpecialFolder.Junk);
                if (trash != null) await folder.MoveToAsync(new UniqueId(uid), trash, ct);
                else               await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Deleted, true, ct);
            }
            finally { await folder.CloseAsync(false, ct); }
        });

    // ── Drafts ───────────────────────────────────────────────────────────────────

    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, client =>
            Task.FromResult(FindSpecialFolder(client, SpecialFolder.Drafts)?.FullName));

    public Task<uint> AppendDraftAsync(
        Guid accountId, ComposeModel draft, uint? replaceUid, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var account = _accounts[accountId];

            var draftsFolder = FindSpecialFolder(client, SpecialFolder.Drafts)
                ?? throw new InvalidOperationException("No Drafts folder found for this account.");

            var msg = MimeMessageBuilder.Build(draft, account);

            if (replaceUid.HasValue)
            {
                await draftsFolder.OpenAsync(FolderAccess.ReadWrite, ct);
                try
                {
                    await draftsFolder.AddFlagsAsync(new UniqueId(replaceUid.Value), MessageFlags.Deleted, true, ct);
                    await draftsFolder.ExpungeAsync(ct);
                }
                finally { await draftsFolder.CloseAsync(false, ct); }
            }

            await draftsFolder.OpenAsync(FolderAccess.ReadWrite, ct);
            try
            {
                var newUid = await draftsFolder.AppendAsync(msg, MessageFlags.Draft, ct);
                LogService.Log($"AppendDraft: saved draft to {draftsFolder.FullName} UID={newUid?.Id}");
                return newUid?.Id ?? 0;
            }
            finally { await draftsFolder.CloseAsync(false, ct); }
        });

    // ── Copy / Move messages ─────────────────────────────────────────────────────

    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<uint> uids, string destinationFolder, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            var dest   = await client.GetFolderAsync(destinationFolder, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            try   { await folder.CopyToAsync(uids.Select(u => new UniqueId(u)).ToList(), dest, ct); }
            finally { await folder.CloseAsync(false, ct); }
        });

    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<uint> uids, string destinationFolder, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            var dest   = await client.GetFolderAsync(destinationFolder, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            try   { await folder.MoveToAsync(uids.Select(u => new UniqueId(u)).ToList(), dest, ct); }
            finally { await folder.CloseAsync(false, ct); }
        });

    // ── Folder CRUD ──────────────────────────────────────────────────────────────

    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            IMailFolder parent = string.IsNullOrEmpty(parentFolderName)
                ? client.GetFolder(client.PersonalNamespaces[0])
                : await client.GetFolderAsync(parentFolderName, ct);
            await parent.CreateAsync(name, true, ct);
        });

    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);

            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            try
            {
                var uids = await folder.SearchAsync(SearchQuery.All, ct);
                if (uids.Count > 0)
                {
                    var trash = FindSpecialFolder(client, SpecialFolder.Trash);
                    if (trash != null) await folder.MoveToAsync(uids, trash, ct);
                    else               await folder.AddFlagsAsync(uids, MessageFlags.Deleted, true, ct);
                }
            }
            finally { await folder.CloseAsync(false, ct); }

            await folder.DeleteAsync(ct);
        });

    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            IMailFolder parent = string.IsNullOrEmpty(newParentFolderName)
                ? client.GetFolder(client.PersonalNamespaces[0])
                : await client.GetFolderAsync(newParentFolderName, ct);
            await folder.RenameAsync(parent, newName, ct);
        });

    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, client =>
            CopyFolderCoreAsync(client, folderName, destinationParentName, ct));

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

    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var trash = FindSpecialFolder(client, SpecialFolder.Trash, SpecialFolder.Junk);

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
        });

    // ── UID queries ──────────────────────────────────────────────────────────────

    public Task<IList<uint>> GetFolderUidsAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            try
            {
                if (folder.Count == 0) return (IList<uint>)Array.Empty<uint>();
                var uids = await folder.SearchAsync(SearchQuery.All, ct);
                return (IList<uint>)uids.Select(u => u.Id).ToList();
            }
            finally { await folder.CloseAsync(false, ct); }
        });

    // ── Body-download preview fallback (used when server lacks IMAP PREVIEW) ────

    public Task<IReadOnlyDictionary<uint, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<uint> uids,
        int maxLines, CancellationToken ct = default)
    {
        if (uids.Count == 0 || maxLines <= 0)
            return Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string>());

        return ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var result = new Dictionary<uint, string>();
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
            return (IReadOnlyDictionary<uint, string>)result;
        });
    }

    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            try   { await folder.CheckAsync(ct); return folder.Unread; }
            finally { await folder.CloseAsync(false, ct); }
        });

    // ── Attachment download ───────────────────────────────────────────────────────

    public Task<byte[]> DownloadAttachmentAsync(
        Guid accountId, string folderName, uint uid, string partSpecifier, CancellationToken ct = default)
        => ExecuteWithRetryAsync(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            try
            {
                var mailKitUid = new UniqueId(uid);
                var summaries  = await folder.FetchAsync(
                    new[] { mailKitUid },
                    MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure,
                    ct);

                var s        = summaries.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Message UID {uid} not found.");
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
        });

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns an authenticated client, reconnecting transparently if the connection dropped.
    /// </summary>
    private async Task<ImapClient> GetOrReconnectAsync(Guid accountId, CancellationToken ct)
    {
        if (_clients.TryGetValue(accountId, out var client) && client.IsConnected && client.IsAuthenticated)
            return client;

        if (!_accounts.TryGetValue(accountId, out var account))
            throw new InvalidOperationException($"Account {accountId} is not connected.");

        string? password = null;
        if (account.AuthType == AuthType.Password && !_passwords.TryGetValue(accountId, out password))
            throw new InvalidOperationException($"Account {accountId} is not connected.");

        // ExecuteWithRetryAsync already holds the per-account lock, so reconnect
        // without re-acquiring it (avoids deadlock). The double-check below handles
        // the rare case where two direct callers both see the client as disconnected.
        if (_clients.TryGetValue(accountId, out client) && client.IsConnected && client.IsAuthenticated)
            return client;

        LogService.Debug($"Reconnecting…");
        await ConnectAsync(account, password, ct);
        return _clients[accountId];
    }

    private static MailMessageSummary SummaryToModel(IMessageSummary s, Guid accountId, string folderName) =>
        new()
        {
            UniqueId    = s.UniqueId.Id,
            AccountId   = accountId,
            FolderName  = folderName,
            From        = FormatAddressListDisplay(s.Envelope?.From),
            To          = FormatAddressListDisplay(s.Envelope?.To),
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

    private static SpecialFolderKind GetSpecialFolderKind(FolderAttributes attrs)
    {
        if ((attrs & (FolderAttributes.Trash | FolderAttributes.Junk)) != 0) return SpecialFolderKind.Trash;
        if ((attrs & FolderAttributes.Sent)   != 0) return SpecialFolderKind.Sent;
        if ((attrs & FolderAttributes.Drafts) != 0) return SpecialFolderKind.Drafts;
        return SpecialFolderKind.None;
    }

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

    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default)
        => ExecuteWithRetryAsync<bool>(accountId, ct, async client =>
        {
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            try
            {
                var uidList = uids.Select(u => new UniqueId(u)).ToList();
                await folder.AddFlagsAsync(uidList, MessageFlags.Deleted, true, ct);
                await folder.ExpungeAsync(ct);
                return true;
            }
            finally { await folder.CloseAsync(false, ct); }
        });

    public async Task NoOpAsync(Guid accountId, CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(accountId, out var client) || !client.IsConnected) return;

        // Skip if another operation already holds the lock — NoOp is just a keepalive.
        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct)) return;
        try
        {
            try   { await client.NoOpAsync(ct); }
            catch (Exception ex) when (IsTransientConnectionError(ex))
            {
                LogService.Log($"NoOp failed for {accountId}, discarding stale connection: {ex.GetType().Name}");
                ForceDisconnect(accountId);
            }
        }
        finally { sem.Release(); }
    }

    // ── Connection retry helpers ─────────────────────────────────────────────────

    private static bool IsTransientConnectionError(Exception ex) =>
        ex is ServiceNotConnectedException
           or ImapProtocolException
           or IOException
           or SocketException;

    private void ForceDisconnect(Guid accountId)
    {
        if (_clients.TryRemove(accountId, out var stale))
        {
            try { stale.Dispose(); } catch { /* best effort */ }
        }
    }

    private Task ExecuteWithRetryAsync(Guid accountId, CancellationToken ct, Func<ImapClient, Task> operation)
        => ExecuteWithRetryAsync<bool>(accountId, ct, async client => { await operation(client); return true; });

    /// <summary>
    /// Runs <paramref name="operation"/> with an authenticated client.
    /// On a transient connection error (BYE, socket drop) the stale client is
    /// discarded, a fresh connection is established, and the operation retried once.
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Guid accountId, CancellationToken ct, Func<ImapClient, Task<T>> operation)
    {
        // Serialize all IMAP operations per account so background sync and
        // foreground actions (delete, send, etc.) never share the client concurrently.
        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var client = await GetOrReconnectAsync(accountId, ct);
            try
            {
                return await operation(client);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientConnectionError(ex))
            {
                LogService.Log($"Transient IMAP error, reconnecting ({accountId}): {ex.GetType().Name}");
                ForceDisconnect(accountId);
                client = await GetOrReconnectAsync(accountId, ct);
                return await operation(client);
            }
        }
        finally { sem.Release(); }
    }

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
