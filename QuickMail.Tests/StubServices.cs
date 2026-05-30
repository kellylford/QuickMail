// Minimal no-op implementations of every service interface.
// These are used exclusively for constructing ViewModels and Windows in tests —
// no IMAP/SMTP/credential calls are ever made.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

sealed class StubImapMailService : IMailService
{
    public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(new List<MailFolderModel>());
    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, uint sinceUid, int initialCount, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
    public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
    public Task MarkReadAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveToTrashAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default) => Task.CompletedTask;
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default) => Task.CompletedTask;
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IList<uint>> GetFolderUidsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<uint>>(Array.Empty<uint>());
    public Task<IReadOnlyDictionary<uint, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<uint> uids, int maxLines, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string>());
    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
    public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult((0, 0));
    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<uint> AppendDraftAsync(Guid accountId, ComposeModel draft, uint? replaceUid, CancellationToken ct = default) => Task.FromResult(0u);
    public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, uint uid, string partSpecifier, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<uint> uids, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<uint> uids, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => Task.CompletedTask;
    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => Task.CompletedTask;
#pragma warning disable CS0067
    public event Action<Guid>? InboxNewMailDetected;
#pragma warning restore CS0067
    public void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) { }
    public void StopIdleWatchers() { }
    public void Dispose() { }
}

sealed class StubSmtpService : ISmtpService
{
    public Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
        string organizerEmail, CancellationToken ct = default) => Task.CompletedTask;
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
}

sealed class StubOAuthService : IOAuthService
{
    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(new OAuthResult(string.Empty, string.Empty));
    public Task SignOutAsync(AccountModel account) => Task.CompletedTask;
}

sealed class StubLocalStoreService : ILocalStoreService
{
    public void Initialize() { }
    public Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries) => Task.CompletedTask;
    public Task<List<MailMessageSummary>> LoadAllSummariesAsync() => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId) => Task.FromResult(new List<MailMessageSummary>());
    public Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null) => Task.FromResult(new List<MailMessageSummary>());
    public Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<uint> uniqueIds) => Task.CompletedTask;
    public Task DeleteAccountDataAsync(Guid accountId) => Task.CompletedTask;
    public Task UpdateIsReadAsync(Guid accountId, string folderName, uint uniqueId, bool isRead) => Task.CompletedTask;
    public Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, uint UniqueId)> items, bool isRead) => Task.CompletedTask;
    public Task UpdatePreviewAsync(Guid accountId, string folderName, uint uniqueId, string preview) => Task.CompletedTask;
    public Task UpdatePreviewsBatchAsync(Guid accountId, string folderName, IEnumerable<(uint UniqueId, string Preview)> updates) => Task.CompletedTask;
    public Task<bool> HasSummariesMissingRecipientsAsync() => Task.FromResult(false);
    public Task UpsertDetailAsync(MailMessageDetail detail) => Task.CompletedTask;
    public Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, uint uniqueId) => Task.FromResult<MailMessageDetail?>(null);
    public Task<uint> GetMaxUidAsync(Guid accountId, string folderName) => Task.FromResult(0u);
    public Task<HashSet<uint>> GetAllUidsAsync(Guid accountId, string folderName) => Task.FromResult(new HashSet<uint>());
}

sealed class StubContactService : IContactService
{
    public Task UpsertContactAsync(ContactModel contact) => Task.CompletedTask;
    public Task<List<ContactModel>> SearchContactsAsync(string prefix, CancellationToken ct = default) => Task.FromResult(new List<ContactModel>());
    public Task<List<ContactModel>> LoadAllContactsAsync() => Task.FromResult(new List<ContactModel>());
    public Task DeleteContactAsync(int id) => Task.CompletedTask;
}

sealed class StubViewService : IViewService
{
    public List<SavedView> Load() => [];
    public void Save(List<SavedView> views) { }
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
        ILocalStoreService store, CancellationToken ct)
        => Task.FromResult(new List<MailMessageSummary>());
}

sealed class StubSyncService : ISyncService
{
#pragma warning disable CS0067 // events required by interface but never raised in stubs
    public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
    public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
    public event Action<int>? RulesApplied;
#pragma warning restore CS0067
    public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts, IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
    public Task SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.CompletedTask;
    public Task SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.CompletedTask;
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
    public void ApplyUserOverrides(IEnumerable<HotkeyBinding> overrides) { }
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
    public Task<MessageTemplate> AddAsync(MessageTemplate template) => Task.FromResult(template);
    public Task UpdateAsync(MessageTemplate template) => Task.CompletedTask;
    public Task DeleteAsync(int id) => Task.CompletedTask;
}
