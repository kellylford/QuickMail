// Minimal no-op implementations of every service interface.
// These are used exclusively for constructing ViewModels and Windows in tests —
// no IMAP/SMTP/credential calls are ever made.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

sealed class StubImapMailService : IMailService
{
    public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
    public bool IsConnected(Guid accountId) => true;
    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(new List<MailFolderModel>());
    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
    public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
    public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Ids handed to the backend for deletion, so a test can assert what the server saw.</summary>
    public List<(string Folder, string Id)> TrashedIds { get; } = [];

    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        foreach (var id in messageIds) TrashedIds.Add((folderName, id));
        return Task.CompletedTask;
    }
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
    {
        foreach (var id in messageIds) TrashedIds.Add((folderName, id));
        return Task.CompletedTask;
    }
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>([]);
    public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
    public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult((0, 0));
    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default) => Task.FromResult("0");
    public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => Task.CompletedTask;
    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => Task.CompletedTask;
    public void Dispose() { }
}

/// <summary>
/// Delegates every <see cref="IMailService"/> member to a real <see cref="StubImapMailService"/>, so
/// a test double only has to override the one call it cares about.
/// </summary>
class StubImapMailServiceBase : IMailService
{
    private readonly StubImapMailService _inner = new();

    public virtual Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => _inner.ConnectAsync(account, password, ct);
    public bool IsConnected(Guid accountId) => _inner.IsConnected(accountId);
    public virtual Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => _inner.DisconnectAsync(accountId, ct);
    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) => _inner.GetFoldersAsync(accountId, ct);
    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => _inner.GetMessageSummariesAsync(accountId, folderName, maxMessages, ct);
    // Virtual: the two fetch entry points a sync exercises, so a double can record which folders
    // were actually visited (#516 startup sync scope) without reimplementing IMailService.
    public virtual Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => _inner.GetMessagesSinceDateAsync(accountId, folderName, since, ct);
    public virtual Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default) => _inner.GetMessagesSinceAsync(accountId, folderName, sinceMessageId, initialCount, ct);
    public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => _inner.GetMessageDetailAsync(accountId, folderName, messageId, ct);
    public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => _inner.PrefetchMessageDetailAsync(accountId, folderName, messageId, ct);
    public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => _inner.MarkReadAsync(accountId, folderName, messageId, ct);
    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => _inner.MarkReadBatchAsync(accountId, folderName, messageIds, ct);
    public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default) => _inner.SetMessageFlaggedAsync(accountId, folderName, messageId, flagged, ct);
    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => _inner.MoveToTrashAsync(accountId, folderName, messageId, ct);
    // Virtual: a double that models the real backend's id parsing overrides this. The backend
    // rejects a local id, and a test that cannot express that cannot pin the mixed-selection
    // delete (#637).
    public virtual Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => _inner.MoveToTrashBatchAsync(accountId, folderName, messageIds, ct);
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => _inner.PermanentlyDeleteBatchAsync(accountId, folderName, messageIds, ct);
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => _inner.NoOpAsync(accountId, ct);
    public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => _inner.CountTrashMessagesAsync(accountId, ct);
    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => _inner.EmptyTrashAsync(accountId, ct);
    public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => _inner.GetFolderMessageIdsAsync(accountId, folderName, ct);
    public Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid accountId, string folderName, CancellationToken ct = default) => _inner.GetFolderMessageIdDatesAsync(accountId, folderName, ct);
    public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default) => _inner.FetchPreviewsAsync(accountId, folderName, messageIds, maxLines, ct);
    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => _inner.PollAsync(accountId, folderName, ct);
    public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => _inner.GetInboxStatusAsync(accountId, ct);
    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => _inner.FindDraftsFolderNameAsync(accountId, ct);
    public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default) => _inner.AppendDraftAsync(accountId, draft, replaceMessageId, ct);
    public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => _inner.AppendToSentAsync(accountId, sent, ct);
    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default) => _inner.DownloadAttachmentAsync(accountId, folderName, messageId, partSpecifier, ct);
    // Virtual: a double needs to make the server refuse, so a test can tell "it worked" from "it
    // was recorded anyway" — which is the whole of the remembered-destination rule (#515).
    public virtual Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => _inner.CopyMessagesAsync(accountId, folderName, messageIds, destinationFolder, ct);
    public virtual Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => _inner.MoveMessagesAsync(accountId, folderName, messageIds, destinationFolder, ct);
    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => _inner.CreateFolderAsync(accountId, parentFolderName, name, ct);
    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => _inner.DeleteFolderAsync(accountId, folderName, ct);
    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => _inner.RenameFolderAsync(accountId, folderName, newName, newParentFolderName, ct);
    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => _inner.CopyFolderAsync(accountId, folderName, destinationParentName, ct);
    public void Dispose() => _inner.Dispose();
}

sealed class StubSmtpService : ISendMailService
{
    /// <summary>Records every ICS reply sent, so tests can assert which account it was routed from.</summary>
    public List<(AccountModel Account, string Ics, string OrganizerEmail)> SentReplies { get; } = new();

    /// <summary>Set to make a send fail, the way a rejected MAIL FROM or a refused login does.</summary>
    public Exception? SendFailure { get; set; }

    /// <summary>Every message handed to SendAsync, so a test can assert a send never happened.</summary>
    public List<(ComposeModel Compose, AccountModel Account)> Sent { get; } = new();

    public Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
    {
        if (SendFailure is not null) return Task.FromException(SendFailure);
        Sent.Add((compose, account));
        return Task.CompletedTask;
    }
    public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
        string organizerEmail, CancellationToken ct = default)
    {
        SentReplies.Add((account, icsReplyContent, organizerEmail));
        return Task.CompletedTask;
    }

    /// <summary>Set to make Test Connection's SMTP leg report a failure.</summary>
    public Exception? VerifyFailure { get; set; }

    public int VerifyCalls { get; private set; }

    public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default)
    {
        VerifyCalls++;
        return VerifyFailure is null ? Task.CompletedTask : Task.FromException(VerifyFailure);
    }
}

sealed class StubAccountService : IAccountService
{
    public List<AccountModel> LoadAccounts() => [];
    public void SaveAccounts(List<AccountModel> accounts) { }
    public void SetDefaultAccount(Guid accountId) { }
}

sealed class StubCredentialService : ICredentialService
{
    public void SavePassword(Guid accountId, string password) { }
    public string? GetPassword(Guid accountId) => null;
    public void DeletePassword(Guid accountId) { }
    public void SaveSecret(string key, string value) { }
    public string? GetSecret(string key) => null;
    public void DeleteSecret(string key) { }
}

sealed class StubBugReportService : IBugReportService
{
    public Task<BugReportResult> SubmitAsync(BugReportModel report, CancellationToken cancellationToken = default) =>
        Task.FromResult(BugReportResult.Failed("stub"));
    public string BuildFallbackUrl(BugReportModel report) => "https://github.com/kellylford/QuickMail/issues/new";
    public string BuildReportText(BugReportModel report) => report.WhatHappened;
}

sealed class StubGoogleOAuthService : IGoogleOAuthService
{
    public Task<string> GetAccessTokenAsync(string username, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<OAuthResult> SignInInteractiveAsync(string loginHint, CancellationToken ct = default) => Task.FromResult(new OAuthResult(string.Empty, loginHint));
    public Task<OAuthResult> AuthorizeContactsAsync(string loginHint, CancellationToken ct = default) => Task.FromResult(new OAuthResult(string.Empty, loginHint));
    public Task SignOutAsync(string username) => Task.CompletedTask;
}

sealed class StubOAuthService : IOAuthService
{
    /// <summary>The scope array handed to the last explicit-scope GetAccessTokenAsync — lets a test
    /// assert a caller asked for Graph scopes rather than IMAP scopes (#529 step 4 token-before-purge).</summary>
    public string[]? LastAccessTokenScopes { get; private set; }

    /// <summary>When set, the explicit-scope GetAccessTokenAsync throws it — to simulate a failed or
    /// declined Graph sign-in so a test can assert the caller left state untouched.</summary>
    public Exception? ThrowOnGetAccessToken { get; set; }

    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
    {
        LastAccessTokenScopes = scopes;
        if (ThrowOnGetAccessToken is { } ex) throw ex;
        return Task.FromResult(string.Empty);
    }
    public Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>
    /// Username interactive sign-in completes as. Left null, sign-in returns an empty username —
    /// which the editor VMs treat as a wrong-identity mismatch (#202) and abandon, so any test that
    /// wants sign-in to SUCCEED must set this to the username it entered.
    /// </summary>
    public string? SignInUsername { get; set; }

    /// <summary>What the token's tenant id says about the signed-in account (#233).</summary>
    public bool SignInIsPersonalAccount { get; set; }

    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(SignInResult());
    public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(SignInResult());
    private OAuthResult SignInResult() => new(string.Empty, SignInUsername ?? string.Empty, SignInIsPersonalAccount);
    public Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;
    public Task RequestCalendarConsentAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Parent account ids RequestSharedMailboxConsentAsync was called for (#31) — lets a test
    /// assert the add-shared flow drove consent on the parent.</summary>
    public List<Guid> SharedConsentRequestedFor { get; } = [];
    /// <summary>When set, RequestSharedMailboxConsentAsync throws it — to simulate a declined or
    /// admin-approval-pending consent so a test can assert the shared mailbox is left disconnected.</summary>
    public Exception? ThrowOnSharedMailboxConsent { get; set; }
    public Task RequestSharedMailboxConsentAsync(AccountModel parent, CancellationToken ct = default)
    {
        SharedConsentRequestedFor.Add(parent.Id);
        if (ThrowOnSharedMailboxConsent is { } ex) throw ex;
        return Task.CompletedTask;
    }

    public Task SignOutAsync(AccountModel account) => Task.CompletedTask;
}

class StubLocalStoreService : ILocalStoreService
{
    public virtual void Initialize() { }
    public virtual Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries) => Task.CompletedTask;

    /// <summary>Cached rows keyed by (account, folder). Empty by default, so every existing test that
    /// never seeds it keeps seeing an empty cache; seed it to exercise a cache-first load such as the
    /// startup folder (#516).</summary>
    public Dictionary<(Guid AccountId, string Folder), List<MailMessageSummary>> SeededSummaries { get; } = [];

    private static List<MailMessageSummary> Newest(IEnumerable<MailMessageSummary> rows)
        => [.. rows.OrderByDescending(m => m.Date)];

    public virtual Task<List<MailMessageSummary>> LoadAllSummariesAsync()
        => Task.FromResult(Newest(SeededSummaries.Values.SelectMany(v => v)));
    public virtual Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId)
        => Task.FromResult(Newest(SeededSummaries.Where(kv => kv.Key.AccountId == accountId).SelectMany(kv => kv.Value)));
    public virtual Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null)
        => Task.FromResult(SeededSummaries.TryGetValue((accountId, folderName), out var rows)
            ? Newest(rows) : []);
    public virtual Task<List<MailMessageSummary>> LoadFolderSummariesSinceAsync(Guid accountId, string folderName, DateTimeOffset since)
        => Task.FromResult(SeededSummaries.TryGetValue((accountId, folderName), out var rows)
            ? Newest(rows.Where(m => m.Date >= since)) : []);
    /// <summary>
    /// Actually removes the rows, like the real store. A no-op here made a delete that never ran
    /// indistinguishable from one that did (#637).
    /// </summary>
    /// <summary>Set to make the unsent count fail, the way --online mode's absent store does.</summary>
    public Exception? CountUnsentMailFailure { get; set; }

    public virtual Task<int> CountUnsentMailAsync(Guid accountId)
    {
        if (CountUnsentMailFailure is not null) return Task.FromException<int>(CountUnsentMailFailure);
        return Task.FromResult(SeededSummaries.Where(kv => kv.Key.AccountId == accountId)
                                              .SelectMany(kv => kv.Value)
                                              .Count(m => m.IsPendingUpload || !string.IsNullOrEmpty(m.SendFailedReason)));
    }

    /// <summary>Marks a seeded row failed, mirroring the real folder-scoped UPDATE (#637).</summary>
    public virtual Task MarkSendFailedAsync(Guid accountId, string folderName, string messageId, string reason)
    {
        foreach (var row in SeededSummaries.Where(kv => kv.Key.AccountId == accountId)
                                           .SelectMany(kv => kv.Value)
                                           .Where(m => m.MessageId == messageId &&
                                                       string.Equals(m.FolderName, folderName, StringComparison.Ordinal)))
        {
            row.SendFailedReason = reason;
        }
        return Task.CompletedTask;
    }

    public virtual Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds)
    {
        if (SeededSummaries.TryGetValue((accountId, folderName), out var rows))
        {
            var ids = new HashSet<string>(messageIds, StringComparer.Ordinal);
            rows.RemoveAll(m => ids.Contains(m.MessageId));
        }
        return Task.CompletedTask;
    }

    /// <summary>Pending local drafts (#637). Reads the same seeded rows as everything else, so a
    /// test seeds a pending draft by setting <see cref="MailMessageSummary.IsPendingUpload"/> on a
    /// seeded summary rather than through a second mechanism.</summary>
    public virtual Task<List<MailMessageSummary>> LoadPendingDraftsAsync(Guid accountId)
        => Task.FromResult<List<MailMessageSummary>>(
            [.. SeededSummaries.Where(kv => kv.Key.AccountId == accountId)
                               .SelectMany(kv => kv.Value)
                               .Where(m => m.IsPendingUpload && string.IsNullOrEmpty(m.SendFailedReason))
                               .OrderBy(m => m.Date)]);
    public virtual Task DeleteAccountDataAsync(Guid accountId) => Task.CompletedTask;
    /// <summary>Account ids passed to <see cref="ClearCachedMailAsync"/>, so a test can assert the
    /// convert purged the right account (#529 step 4).</summary>
    public List<Guid> ClearedMailAccountIds { get; } = [];
    public virtual Task ClearCachedMailAsync(System.Collections.Generic.IEnumerable<System.Guid> accountIds)
    {
        ClearedMailAccountIds.AddRange(accountIds);
        return Task.CompletedTask;
    }
    public virtual Task PurgeCalendarEventsForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds) => Task.CompletedTask;

    /// <summary>Folders the stub hands back from <see cref="LoadFoldersAsync"/> — seed this to stand in
    /// for a persisted folder list at startup (#516). <see cref="SaveFoldersAsync"/> writes into it too,
    /// so a save/load round-trip works without a real database.</summary>
    public Dictionary<Guid, List<MailFolderModel>> SeededFolders { get; } = [];
    public virtual Task SaveFoldersAsync(Guid accountId, IReadOnlyList<MailFolderModel> folders)
    {
        SeededFolders[accountId] = [.. folders.Where(f => !f.IsHeader && f.FullName.Length > 0)];
        return Task.CompletedTask;
    }
    public virtual Task<Dictionary<Guid, List<MailFolderModel>>> LoadFoldersAsync()
        => Task.FromResult(SeededFolders.ToDictionary(kv => kv.Key, kv => new List<MailFolderModel>(kv.Value)));
    public virtual Task PurgeFoldersForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds)
    {
        foreach (var id in SeededFolders.Keys.Where(k => !knownAccountIds.Contains(k)).ToList())
            SeededFolders.Remove(id);
        return Task.CompletedTask;
    }

    public virtual Task UpdateIsReadAsync(Guid accountId, string folderName, string messageId, bool isRead) => Task.CompletedTask;
    public virtual Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, bool isRead) => Task.CompletedTask;
    public virtual Task UpdatePreviewAsync(Guid accountId, string folderName, string messageId, string preview) => Task.CompletedTask;
    public virtual Task UpdatePreviewsBatchAsync(Guid accountId, string folderName, IEnumerable<(string MessageId, string Preview)> updates) => Task.CompletedTask;
    public virtual Task<bool> HasSummariesMissingRecipientsAsync() => Task.FromResult(false);
    public virtual Task UpsertDetailAsync(MailMessageDetail detail) => Task.CompletedTask;
    /// <summary>When set, <see cref="LoadDetailAsync"/> returns this seeded detail (tests use it to
    /// stand in for a cached invite email); otherwise it returns null like the real cache-miss path.</summary>
    public MailMessageDetail? SeededDetail { get; set; }
    public virtual Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId) => Task.FromResult(SeededDetail);

    /// <summary>Raw POP3 message bytes (#128), kept so a test can assert what was stored.</summary>
    public Dictionary<(Guid AccountId, string Folder, string MessageId), byte[]?> MimeBytes { get; } = new();

    public virtual Task StoreMimeBytesAsync(Guid accountId, string folderName, string messageId, byte[]? mimeBytes)
    {
        MimeBytes[(accountId, folderName, messageId)] = mimeBytes;
        return Task.CompletedTask;
    }

    public virtual Task<byte[]?> LoadMimeBytesAsync(Guid accountId, string folderName, string messageId) =>
        Task.FromResult(MimeBytes.TryGetValue((accountId, folderName, messageId), out var bytes) ? bytes : null);

    /// <summary>Functional in-memory POP3 collected-UIDL ledger, so backend tests exercise the real
    /// record/prune arithmetic rather than a silent no-op.</summary>
    public Dictionary<Guid, HashSet<string>> Pop3Uidls { get; } = [];
    public virtual Task<HashSet<string>> LoadPop3CollectedUidlsAsync(Guid accountId)
        => Task.FromResult(Pop3Uidls.TryGetValue(accountId, out var set)
            ? new HashSet<string>(set, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal));
    public virtual Task AddPop3CollectedUidlsAsync(Guid accountId, IEnumerable<string> uidls)
    {
        if (!Pop3Uidls.TryGetValue(accountId, out var set))
            Pop3Uidls[accountId] = set = new HashSet<string>(StringComparer.Ordinal);
        set.UnionWith(uidls);
        return Task.CompletedTask;
    }
    public virtual Task RemovePop3CollectedUidlsAsync(Guid accountId, IEnumerable<string> uidls)
    {
        if (Pop3Uidls.TryGetValue(accountId, out var set)) set.ExceptWith(uidls);
        return Task.CompletedTask;
    }
    public virtual Task<string> GetMaxMessageKeyAsync(Guid accountId, string folderName) => Task.FromResult("0");
    public virtual Task<HashSet<string>> GetAllMessageIdsAsync(Guid accountId, string folderName) => Task.FromResult(new HashSet<string>());
    public virtual Task<Dictionary<string, bool>> LoadFolderReadStatesAsync(Guid accountId, string folderName) => Task.FromResult(new Dictionary<string, bool>());
    public virtual Task<IReadOnlyList<(string Id, DateTimeOffset Date, bool IsRead)>> LoadFolderMessageStatesAsync(Guid accountId, string folderName)
        => Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>(
            SeededSummaries.TryGetValue((accountId, folderName), out var rows)
                ? [.. rows.Select(m => (m.MessageId, m.Date, m.IsRead))] : []);

    /// <summary>Functional enough to be worth asserting on: the seeded rows really are moved (or
    /// copied) between folders, so a POP3 re-file test sees the outcome rather than a no-op.
    /// <para>A copy shares the row instance with the source rather than cloning it — the seeded lists
    /// are keyed by folder, so membership is the thing to assert on, not the copy's
    /// <c>FolderName</c>.</para></summary>
    public virtual Task<int> RefileMessagesAsync(Guid accountId, string fromFolder, string toFolder, IEnumerable<string> messageIds, bool copy)
    {
        // Same-folder is a no-op in the real store. Without this the stub would find source and
        // destination to be the same list and remove the row from it — the fake destroying mail the
        // shipping code leaves alone is exactly the wrong direction for a fake to be wrong in.
        if (string.Equals(fromFolder, toFolder, StringComparison.Ordinal)) return Task.FromResult(0);
        if (!SeededSummaries.TryGetValue((accountId, fromFolder), out var source)) return Task.FromResult(0);
        var wanted = new HashSet<string>(messageIds, StringComparer.Ordinal);
        var moving = source.Where(m => wanted.Contains(m.MessageId)).ToList();
        if (moving.Count == 0) return Task.FromResult(0);

        if (!SeededSummaries.TryGetValue((accountId, toFolder), out var destination))
            SeededSummaries[(accountId, toFolder)] = destination = [];
        foreach (var m in moving)
        {
            destination.RemoveAll(d => d.MessageId == m.MessageId);
            destination.Add(m);
            if (!copy)
            {
                source.Remove(m);
                m.FolderName = toFolder;
            }
        }
        return Task.FromResult(moving.Count);
    }
    public virtual Task<HashSet<string>> GetExistingMessageIdsAsync(Guid accountId, string folderName, IEnumerable<string> messageIds) => Task.FromResult(new HashSet<string>());
    public virtual Task<int> CountSummariesAsync(Guid accountId) => Task.FromResult(0);
    public virtual Task<Dictionary<string, int>> CountSummariesByFolderAsync(Guid accountId) => Task.FromResult(new Dictionary<string, int>());
    public virtual Task<Dictionary<string, (int Total, int Unread)>> CountMessagesByFolderAsync(Guid accountId)
        => Task.FromResult(SeededSummaries
            .Where(kv => kv.Key.AccountId == accountId)
            .ToDictionary(kv => kv.Key.Folder, kv => (kv.Value.Count, kv.Value.Count(m => !m.IsRead))));
    public virtual Task<DateTimeOffset?> GetOldestMessageDateAsync(Guid accountId) => Task.FromResult<DateTimeOffset?>(null);
    public virtual Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId) => Task.CompletedTask;
    public virtual Task UpdateFlagIdBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, string? flagId) => Task.CompletedTask;
    public virtual Task UpsertCalendarEventAsync(CalendarEvent evt) => Task.CompletedTask;
    public virtual Task<List<CalendarEvent>> LoadCalendarEventsAsync() => Task.FromResult(new List<CalendarEvent>());
    /// <summary>Each account's recorded calendar list, as ReplaceCalendarSourcesAsync left it.</summary>
    public Dictionary<Guid, List<CalendarSourceInfo>> CalendarSources { get; } = [];

    public virtual Task<IReadOnlyList<CalendarSourceInfo>> LoadCalendarSourcesAsync()
        => Task.FromResult<IReadOnlyList<CalendarSourceInfo>>(
            CalendarSources.Values.SelectMany(v => v).ToList());

    public virtual Task ReplaceCalendarSourcesAsync(Guid accountId, IReadOnlyList<CalendarSourceInfo> sources)
    {
        CalendarSources[accountId] = [.. sources];
        return Task.CompletedTask;
    }

    public virtual Task DeleteCalendarSourcesAsync(Guid accountId)
    {
        CalendarSources.Remove(accountId);
        return Task.CompletedTask;
    }
    public virtual Task UpdateCalendarResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status) => Task.CompletedTask;
    public virtual Task DeleteCalendarEventAsync(string uid, Guid accountId) => Task.CompletedTask;
    public virtual Task<List<(Guid AccountId, string FolderName, string MessageId, string IcsText)>> LoadAllCalendarIcsAsync()
        => Task.FromResult(new List<(Guid, string, string, string)>());
    public virtual Task ClearOrphanedCalendarSourceLinksAsync() => Task.CompletedTask;
    public virtual Task ReplaceGraphCalendarEventsAsync(Guid accountId, IReadOnlyList<CalendarEvent> events) => Task.CompletedTask;
    public virtual Task<string?> GetDeltaTokenAsync(Guid accountId, string folderId) => Task.FromResult<string?>(null);
    public virtual Task SetDeltaTokenAsync(Guid accountId, string folderId, string deltaToken) => Task.CompletedTask;
}

sealed class StubContactService : IContactService
{
    public Task UpsertContactAsync(ContactModel contact) => Task.CompletedTask;
    public Task<List<ContactModel>> SearchContactsAsync(string prefix, CancellationToken ct = default) => Task.FromResult(new List<ContactModel>());
    public Task<List<ContactModel>> LoadAllContactsAsync() => Task.FromResult(new List<ContactModel>());
    public Task DeleteContactAsync(int id) => Task.CompletedTask;
    public Task<bool> UpdateContactAsync(int id, string displayName, string emailAddress) => Task.FromResult(true);
    public Task ReplaceSyncedContactsAsync(Guid accountId, ContactSource source, IReadOnlyList<ContactModel> serverContacts) => Task.CompletedTask;
    public Task RemoveSyncedContactsAsync(Guid accountId) => Task.CompletedTask;

    // Groups — no-op stubs. Tests that need real group behaviour construct
    // a real ContactService pointed at a temp directory.
    public Task<List<GroupModel>> LoadAllGroupsAsync() => Task.FromResult(new List<GroupModel>());
    public Task<int> CreateGroupAsync(string name) => Task.FromResult(0);
    public Task RenameGroupAsync(int id, string newName) => Task.CompletedTask;
    public Task DeleteGroupAsync(int id) => Task.CompletedTask;
    public Task AddMemberAsync(int groupId, int contactId) => Task.CompletedTask;
    public Task RemoveMemberAsync(int groupId, int contactId) => Task.CompletedTask;
    public Task<List<int>> ListGroupsForContactAsync(int contactId) => Task.FromResult(new List<int>());
    public Task TouchGroupAsync(int groupId) => Task.CompletedTask;
    public Task<List<GroupModel>> SearchGroupsAsync(string prefix, CancellationToken ct = default)
        => Task.FromResult(new List<GroupModel>());
}

sealed class StubContactSyncService : IContactSyncService
{
    public bool CanSync(AccountModel account) => false;
    public Task<ContactSyncResult> SyncAccountAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(ContactSyncResult.None);
    public Task<ContactSyncResult> SyncAllAsync(CancellationToken ct = default) => Task.FromResult(ContactSyncResult.None);
    public Task<ContactSyncResult> SyncAllDueAsync(TimeSpan minInterval, CancellationToken ct = default) => Task.FromResult(ContactSyncResult.None);
    public Task RemoveAccountContactsAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
}

sealed class StubViewService : IViewService
{
    public List<SavedView> Load() => [];
    public void Save(List<SavedView> views) { }
}

/// <summary>In-memory per-folder view state (#520) — no folderviews.json, no disk. Keyed the same
/// way the real service keys, so resolve-order and key-isolation tests exercise real behaviour.
/// <see cref="Writes"/> counts Set calls so tests can assert that applying a saved view records
/// nothing.</summary>
sealed class StubFolderViewStateService : IFolderViewStateService
{
    private readonly Dictionary<string, ListState> _states = new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    private static string Key(Guid accountId, string folderFullName) =>
        $"{accountId:N}|{folderFullName}";

    public ListState? Recall(Guid accountId, string folderFullName) =>
        _states.TryGetValue(Key(accountId, folderFullName), out var s) ? s : null;

    public void Remember(Guid accountId, string folderFullName, ListState state)
    {
        _states[Key(accountId, folderFullName)] = state;
        Writes++;
    }

    public void Forget(Guid accountId, string folderFullName) =>
        _states.Remove(Key(accountId, folderFullName));
}

/// <summary>In-memory watched conversations — no watches.json, no disk. Matching mirrors the real
/// service (normalized subject, case-insensitive) so VM tests exercise the real predicate.</summary>
sealed class StubWatchService : IWatchService
{
    private readonly List<WatchedConversation> _watches = [];

    public IReadOnlyList<WatchedConversation> GetAll() => _watches;

    public bool IsWatched(string subject)
    {
        var key = ConversationBuilder.NormalizeSubject(subject ?? string.Empty);
        return key.Length > 0 && _watches.Any(
            w => string.Equals(w.NormalizedSubject, key, StringComparison.OrdinalIgnoreCase));
    }

    public bool Watch(string subject)
    {
        var key = ConversationBuilder.NormalizeSubject(subject ?? string.Empty);
        if (key.Length == 0 || IsWatched(key)) return false;
        _watches.Add(new WatchedConversation { NormalizedSubject = key, Label = (subject ?? string.Empty).Trim() });
        return true;
    }

    public bool Unwatch(Guid id)
    {
        var removed = _watches.RemoveAll(w => w.Id == id);
        return removed > 0;
    }

    public bool Rename(Guid id, string label)
    {
        var trimmed = (label ?? string.Empty).Trim();
        if (trimmed.Length == 0) return false;
        var watch = _watches.FirstOrDefault(w => w.Id == id);
        if (watch == null) return false;
        watch.Label = trimmed;   // label only — NormalizedSubject is the matching key
        return true;
    }

    public bool Unwatch(string subject)
    {
        var key = ConversationBuilder.NormalizeSubject(subject ?? string.Empty);
        if (key.Length == 0) return false;
        var removed = _watches.RemoveAll(
            w => string.Equals(w.NormalizedSubject, key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        return true;
    }
}

/// <summary>In-memory spoken field layouts — no rowlayout.json, no disk.</summary>
sealed class StubRowLayoutService : IRowLayoutService
{
    private RowLayouts _layouts = RowFieldCatalog.DefaultLayouts();

    public int SaveCount { get; private set; }

    public event EventHandler? LayoutsChanged;

    // Clone on the way out so callers cannot mutate the stored copy without saving —
    // matching the real service, where Load() deserializes a fresh object each time.
    public RowLayouts Load() => _layouts.Clone();

    public void Save(RowLayouts layouts)
    {
        _layouts = layouts.Clone();
        SaveCount++;
        LayoutsChanged?.Invoke(this, EventArgs.Empty);
    }
}

sealed class StubRuleService : IRuleService
{
    public List<MailRule> LoadedRules { get; set; } = [];
    public int ApplyRulesReturnValue { get; set; } = 0;
    public List<MailMessageSummary> ApplyRulesRemovedMessages { get; set; } = [];

    public List<MailRule> LoadRules() => LoadedRules;
    public void SaveRules(List<MailRule> rules) => LoadedRules = rules;

    public Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRulesAsync(
        List<MailMessageSummary> incoming, Guid accountId, CancellationToken ct)
        => Task.FromResult((ApplyRulesReturnValue, ApplyRulesRemovedMessages));

    public List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages)
        => messages.ToList(); // Stub matches everything

    public Task<List<MailMessageSummary>> ApplyRulesToExistingAsync(
        ILocalStoreService store, IReadOnlyDictionary<Guid, string> inboxFolderByAccount, CancellationToken ct)
        => Task.FromResult(new List<MailMessageSummary>());
}

sealed class StubSyncService : ISyncService
{
#pragma warning disable CS0067 // events required by interface but never raised in stubs
    public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
    public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
    public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
    public event Action<int>? RulesApplied;
    public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067
    public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts, IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
    public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
    public Task<int> ReconcileFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.FromResult(0);
    public Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
    public void SeedRebuildBaseline(IEnumerable<Guid> accountIds) { }
    public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
}

sealed class StubCommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _commands = [];

    public void Register(CommandDefinition command)
        => _commands[command.Id] = command;

    public IReadOnlyList<CommandDefinition> GetAll()
        => _commands.Values.OrderBy(c => c.Category).ThenBy(c => c.Title).ToList();

    public CommandDefinition? FindById(string id)
        => _commands.TryGetValue(id, out var cmd) ? cmd : null;

    public CommandDefinition? FindByGesture(Key key, ModifierKeys modifiers)
        => _commands.Values.FirstOrDefault(c => c.DefaultKey == key && c.DefaultModifiers == modifiers);

    public void Unregister(string id) => _commands.Remove(id);
    public void ApplyUserOverrides(IEnumerable<HotkeyBinding> bindings) { }
    public IReadOnlyList<string> GetOrphanOverrideCommandIds() => [];

    // Test helpers
    public void RegisterTestCommand(string id, string category, string title)
    {
        Register(new CommandDefinition(id, category, title, () => { }));
    }
}

sealed class StubConfigService : IConfigService
{
    private ConfigModel _config = new();

    public ConfigModel Load() => _config;

    public void Save(ConfigModel config)
        => _config = config;
}

sealed class StubTemplateService : ITemplateService
{
    public Task<List<MessageTemplate>> LoadAllAsync() => Task.FromResult(new List<MessageTemplate>());
    public Task<MessageTemplate> AddAsync(MessageTemplate item) => Task.FromResult(item);
    public Task UpdateAsync(MessageTemplate item) => Task.CompletedTask;
    public Task DeleteAsync(int id) => Task.CompletedTask;
}

sealed class StubFeatureGate : IFeatureGate
{
    private readonly Dictionary<FeatureFlag, bool> _flags = new();

    /// <summary>Enable/disable a flag for the test, e.g. <c>gate[FeatureFlag.GraphBackend] = true;</c></summary>
    public bool this[FeatureFlag flag] { set => _flags[flag] = value; }

    public bool IsEnabled(FeatureFlag flag) => _flags.TryGetValue(flag, out var v) && v;
}

sealed class StubFlagService : IFlagService
{
#pragma warning disable CS0067
    public event EventHandler? FlagDefinitionsChanged;
#pragma warning restore CS0067
    public FlagDefinition GetBuiltInFlag() => FlagDefinition.CreateBuiltIn();
    public Task<List<FlagDefinition>> LoadFlagDefinitionsAsync() => Task.FromResult(new List<FlagDefinition> { FlagDefinition.CreateBuiltIn() });
    public Task SaveFlagDefinitionsAsync(List<FlagDefinition> flags) => Task.CompletedTask;
    public Task<FlagDefinition> GetKDefaultFlagAsync() => Task.FromResult(FlagDefinition.CreateBuiltIn());
    public Task SetKDefaultFlagAsync(Guid flagId) => Task.CompletedTask;
    public Task<FlagDefinition?> SetMessageFlagAsync(MailMessageSummary message, string? flagId, FlagDefinition? resolvedDef = null, CancellationToken ct = default)
        => Task.FromResult<FlagDefinition?>(resolvedDef ?? (flagId != null ? FlagDefinition.CreateBuiltIn() : null));
    public Task<FlagDefinition?> ToggleDefaultFlagAsync(MailMessageSummary message, CancellationToken ct = default)
        => Task.FromResult<FlagDefinition?>(message.IsFlagged ? null : FlagDefinition.CreateBuiltIn());
}

sealed class StubCalendarService : ICalendarService
{
    /// <summary>
    /// What the cache holds. Assigning the whole list is how a test says "the store already had
    /// these" — a starting state, not a write — so it counts as loaded too and the test does not
    /// have to refresh before reading. A write that goes through the service afterwards still
    /// needs its reload to show up in <see cref="Events"/>, which is the part that matters.
    /// </summary>
    public List<CalendarEvent> StoredEvents
    {
        get => _stored;
        set { _stored = value; _loaded = value.ToList(); }
    }
    private List<CalendarEvent> _stored = [];

    public int RefreshCallCount { get; private set; }

    /// <summary>
    /// What a caller sees, which is what the last load put here — deliberately NOT an alias for
    /// <see cref="StoredEvents"/>.
    ///
    /// <para>
    /// The real <c>CalendarService</c> holds an in-memory list that changes only when something
    /// reloads it from the provider, so a ViewModel that persists an event and then forgets to
    /// reload goes on showing the list from before. A stub whose Events pointed straight at its
    /// storage could not tell that apart from a working save — which is the shape of issue #519,
    /// where a new appointment was not in the list until F5. Mirroring the real service's shape
    /// (reload on refresh and upsert, mutate in place on status and delete) is what makes a
    /// missing reload fail a test.
    /// </para>
    /// </summary>
    public IReadOnlyList<CalendarEvent> Events => _loaded;
    private List<CalendarEvent> _loaded = [];

    public Task RefreshAsync(CancellationToken ct = default)
    {
        RefreshCallCount++;
        _loaded = StoredEvents.ToList();
        return Task.CompletedTask;
    }

    public Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default)
    {
        Upsert(evt);
        _loaded = StoredEvents.ToList();   // the real service reloads after an upsert
        return Task.CompletedTask;
    }

    /// <summary>
    /// The same store write, callable from a stub whose own body is not async — the calendar sync
    /// stub writes the server's copy here the way the real sync service writes it to SQLite.
    /// </summary>
    public void Upsert(CalendarEvent evt)
    {
        var idx = StoredEvents.FindIndex(e => e.Uid == evt.Uid && e.AccountId == evt.AccountId);
        if (idx >= 0)
            StoredEvents[idx] = evt;
        else
            StoredEvents.Add(evt);
    }

    public Task SetResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status, CancellationToken ct = default)
    {
        // In place, as the real service does. _loaded is a shallow copy, so its rows ARE these
        // objects and this is visible through Events without touching it — unlike DeleteEventAsync
        // below, which changes list membership and so has to be applied to both.
        var idx = StoredEvents.FindIndex(e => e.Uid == uid && e.AccountId == accountId);
        if (idx >= 0) StoredEvents[idx].ResponseStatus = status;
        return Task.CompletedTask;
    }

    public Task DeleteEventAsync(string uid, Guid accountId, CancellationToken ct = default)
    {
        StoredEvents.RemoveAll(e => e.Uid == uid && e.AccountId == accountId);
        _loaded.RemoveAll(e => e.Uid == uid && e.AccountId == accountId);
        return Task.CompletedTask;
    }
}

sealed class StubGraphCalendarSyncService : IGraphCalendarSyncService
{
    public int SyncCallCount { get; private set; }
    public GraphCalendarSyncResult Result { get; set; } = GraphCalendarSyncResult.None;

    /// <summary>When set, CreateEventAsync throws it (simulates a failed push).</summary>
    public Exception? CreateFailure { get; set; }
    public List<CalendarEvent> CreatedEvents { get; } = [];

    /// <summary>
    /// The calendar store the real service writes the server's copy into. Wire it and a create or
    /// update push shows up in the calendar exactly as it does in the app:
    /// <c>GraphCalendarSyncService</c> upserts the returned event before returning, so the
    /// <c>RefreshAsync</c> the ViewModel does next reloads a list that already contains it. A stub
    /// that only records the push cannot tell a working create apart from one the list never picks
    /// up until F5 (issue #519).
    /// </summary>
    public StubCalendarService? CalendarStore { get; set; }

    /// <summary>
    /// What the account's default calendar resolves to — what a created event with no chosen
    /// calendar gets tagged with. Set it to a calendar a test also filters on, to exercise the
    /// per-calendar folder-tree node that #569 was reported against.
    /// </summary>
    public string DefaultCalendarId { get; set; } = string.Empty;
    public string DefaultCalendarName { get; set; } = string.Empty;

    /// <summary>
    /// Runs when a pull happens, so a test can put into the store what the server "had" — the real
    /// service writes the fetched slice there before returning, and the caller reloads from it.
    /// </summary>
    public Action? OnSync { get; set; }

    public Task<GraphCalendarSyncResult> SyncAllAsync(CancellationToken ct = default)
    {
        SyncCallCount++;
        OnSync?.Invoke();
        return Task.FromResult(Result);
    }

    public List<Guid> SyncedAccounts { get; } = [];
    public List<Guid> RemovedAccounts { get; } = [];

    public Task<int> SyncAccountCalendarAsync(AccountModel account, CancellationToken ct = default)
    {
        SyncedAccounts.Add(account.Id);
        return Task.FromResult(0);
    }

    public Task RemoveAccountCalendarAsync(Guid accountId, CancellationToken ct = default)
    {
        RemovedAccounts.Add(accountId);
        return Task.CompletedTask;
    }

    public Task<CalendarEvent> CreateEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct = default)
    {
        if (CreateFailure != null) throw CreateFailure;
        // Mimic the real service: the stored copy carries the server id and the Graph flag.
        var created = new CalendarEvent
        {
            Uid = "graph-" + CreatedEvents.Count, AccountId = account.Id, IsGraph = true,
            Summary = evt.Summary, Location = evt.Location, Description = evt.Description,
            StartTimeTicks = evt.StartTimeTicks, EndTimeTicks = evt.EndTimeTicks,
            IsAllDay = evt.IsAllDay, ResponseStatus = CalendarResponseStatus.Accepted,
            // Tagged with the calendar it was filed on, as the real service does since #569. The
            // save target names no calendar for a Google or Microsoft account, so the service
            // resolves the account's default; DefaultCalendar stands in for that here. A stub that
            // left this blank would model the BUG — a row that fails the per-calendar folder-tree
            // node's filter and is invisible under the calendar it was just saved to.
            CalendarId = string.IsNullOrEmpty(evt.CalendarId) ? DefaultCalendarId : evt.CalendarId,
            CalendarName = string.IsNullOrEmpty(evt.CalendarId) ? DefaultCalendarName : evt.CalendarName,
        };
        CreatedEvents.Add(created);
        CalendarStore?.Upsert(created);
        return Task.FromResult(created);
    }

    /// <summary>When set, UpdateEventAsync/DeleteEventAsync throw it (simulates a failed push).</summary>
    public Exception? WriteFailure { get; set; }
    public List<CalendarEvent> UpdatedEvents { get; } = [];
    public List<CalendarEvent> DeletedEvents { get; } = [];

    public Task<CalendarEvent> UpdateEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct = default)
    {
        if (WriteFailure != null) throw WriteFailure;
        evt.IsGraph = true;
        UpdatedEvents.Add(evt);
        CalendarStore?.Upsert(evt);
        return Task.FromResult(evt);
    }

    public Task DeleteEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct = default)
    {
        if (WriteFailure != null) throw WriteFailure;
        DeletedEvents.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>Runs everything inline — VM unit tests are single-threaded by design.</summary>
sealed class StubUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
    public void Post(Action action) => action();
}

/// <summary>
/// In-memory theme service: a fixed light theme, no OS probes, no Application
/// resources. Records applied ids so tests can assert theme switching.
/// </summary>
sealed class StubThemeService : IThemeService
{
    private readonly List<ThemeDefinition> _userThemes = [];

    public string ConfiguredThemeId { get; private set; } = "system";
    public string ConfiguredThemeName =>
        string.Equals(ConfiguredThemeId, "system", StringComparison.OrdinalIgnoreCase)
            ? $"System, showing {ResolvedTheme.Name}"
            : ResolvedTheme.Name;
    public bool IsHighContrastActive { get; set; }
    public string UserThemesFolder { get; set; } = string.Empty;
    public List<string> AppliedThemeIds { get; } = [];

    public event EventHandler? ThemeChanged;

    public ThemeDefinition ResolvedTheme { get; set; } = BuildDefaultResolved();

    private static ThemeDefinition BuildDefaultResolved()
    {
        var theme = new ThemeDefinition { Id = "parchment", Name = "Parchment", Base = "light", IsBuiltIn = true };
        foreach (var key in QuickMail.Theming.ThemeKeys.ColorTokens.Keys)
            theme.Colors[key] = "#000000";
        return theme;
    }

    public IReadOnlyList<ThemeDefinition> GetAvailableThemes()
    {
        var list = new List<ThemeDefinition>
        {
            new() { Id = "system", Name = "System", Base = "light", IsBuiltIn = true },
            new() { Id = "parchment", Name = "Parchment", Base = "light", IsBuiltIn = true },
            new() { Id = "dark",      Name = "Parchment Dark", Base = "dark", IsBuiltIn = true },
        };
        list.AddRange(_userThemes);
        return list;
    }

    public void ApplyTheme(string themeId)
    {
        ConfiguredThemeId = themeId;
        AppliedThemeIds.Add(themeId);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public ThemeDefinition ImportTheme(string filePath)
    {
        var theme = ThemeDefinition.Parse(System.IO.File.ReadAllText(filePath));
        _userThemes.Add(theme);
        return theme;
    }

    public void ExportTheme(string themeId, string filePath)
    {
        ThemeDefinition? theme = null;
        foreach (var t in GetAvailableThemes())
            if (string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase)) { theme = t; break; }
        if (theme is null) throw new InvalidOperationException($"No theme {themeId}.");
        System.IO.File.WriteAllText(filePath, theme.ToJson());
    }

    public void SaveUserTheme(ThemeDefinition theme)
    {
        _userThemes.RemoveAll(t => string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase));
        _userThemes.Add(theme);
    }

    public void DeleteUserTheme(string themeId)
    {
        _userThemes.RemoveAll(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(ConfiguredThemeId, themeId, StringComparison.OrdinalIgnoreCase))
            ApplyTheme("system");
    }

    public ThemeDefinition ResolveForPreview(string themeId)
    {
        ThemeDefinition? match = null;
        foreach (var t in GetAvailableThemes())
            if (string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase)) { match = t; break; }

        var full = (match ?? ResolvedTheme).Clone();
        if (string.IsNullOrEmpty(full.Name)) full.Name = ResolvedTheme.Name;
        var fill = BuildDefaultResolved();
        foreach (var key in QuickMail.Theming.ThemeKeys.ColorTokens.Keys)
            if (!full.Colors.ContainsKey(key)) full.Colors[key] = fill.Colors[key];
        return full;
    }

    public void ApplyVisionSettings(ConfigModel config) { }

    public void ApplyAppearance(ConfigModel config) =>
        ApplyTheme(string.IsNullOrWhiteSpace(config.AppearanceThemeId) ? "system" : config.AppearanceThemeId);

    public string BuildMessageCss(bool forceOnContent) => string.Empty;

    public void Dispose() { }
}

/// <summary>
/// In-memory <see cref="ILocalDraftService"/> (#637). Honest rather than inert: it mints local ids,
/// replaces in place, remembers which server draft each local one supersedes, and can be told to
/// fail — which is what makes "the draft survived even though the server did not take it" assertable
/// without a SQLite file.
/// </summary>
sealed class FakeLocalDraftService : ILocalDraftService
{
    private readonly Dictionary<string, (ComposeModel Draft, string? Supersedes, string Folder)> _drafts = [];
    private int _next;

    /// <summary>Drafts folder handed back offline. Null models an account whose folders never synced.</summary>
    public string? DraftsFolderName { get; set; } = "Drafts";

    /// <summary>Simulates the local store refusing the write — the only remaining hard save failure.</summary>
    public bool SaveThrows { get; set; }

    public int SaveCalls { get; private set; }
    public List<string> Discarded { get; } = [];
    public IReadOnlyDictionary<string, (ComposeModel Draft, string? Supersedes, string Folder)> Stored => _drafts;

    public Task<PendingDraftSave> SaveAsync(
        AccountModel account, ComposeModel draft, string folderName,
        string? previousMessageId, CancellationToken ct = default)
    {
        if (SaveThrows) throw new InvalidOperationException("simulated local store failure");
        SaveCalls++;

        var replacingLocal = LocalMessageId.IsLocal(previousMessageId);
        var id = replacingLocal ? previousMessageId! : LocalMessageId.Prefix + (++_next);
        var supersedes = replacingLocal
            ? (_drafts.TryGetValue(id, out var existing) ? existing.Supersedes : null)
            : previousMessageId;

        _drafts[id] = (draft, supersedes, folderName);
        return Task.FromResult(new PendingDraftSave(id, supersedes));
    }

    public Task<ComposeModel?> LoadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        => Task.FromResult(_drafts.TryGetValue(messageId, out var d) ? d.Draft : null);

    public Task<string?> GetSupersededServerIdAsync(Guid accountId, string folderName, string messageId)
        => Task.FromResult(_drafts.TryGetValue(messageId, out var d) ? d.Supersedes : null);

    /// <summary>Why each draft was refused, for tests that assert the row carries it (#637).</summary>
    public Dictionary<string, string> FailedReasons { get; } = [];

    public Task MarkSendFailedAsync(Guid accountId, string folderName, string messageId, string reason)
    {
        FailedReasons[messageId] = reason;
        return Task.CompletedTask;
    }

    public Task DiscardAsync(Guid accountId, string folderName, string messageId)
    {
        Discarded.Add(messageId);
        _drafts.Remove(messageId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid accountId)
        => Task.FromResult<IReadOnlyList<MailMessageSummary>>(
            [.. _drafts.Select(kv => new MailMessageSummary
            {
                MessageId       = kv.Key,
                AccountId       = accountId,
                FolderName      = kv.Value.Folder,
                Subject         = kv.Value.Draft.Subject,
                IsPendingUpload = true,
            })]);

    public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult(DraftsFolderName);
}
