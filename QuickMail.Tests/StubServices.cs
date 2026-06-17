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
    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
    public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
    public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
    public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<string>>(Array.Empty<string>());
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
#pragma warning disable CS0067
    public event Action<Guid, bool>? AccountReachabilityChanged;
    public event Action<Guid>? InboxNewMailDetected;
#pragma warning restore CS0067
    public void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) { }
    public void StopIdleWatchers() { }
    public void Dispose() { }
}

sealed class StubSmtpService : ISendMailService
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
    public void SaveSecret(string key, string value) { }
    public string? GetSecret(string key) => null;
    public void DeleteSecret(string key) { }
}

sealed class StubGoogleOAuthService : IGoogleOAuthService
{
    public Task<string> GetAccessTokenAsync(string username, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<OAuthResult> SignInInteractiveAsync(string loginHint, CancellationToken ct = default) => Task.FromResult(new OAuthResult(string.Empty, loginHint));
    public Task SignOutAsync(string username) => Task.CompletedTask;
}

sealed class StubOAuthService : IOAuthService
{
    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => Task.FromResult(string.Empty);
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
    public Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds) => Task.CompletedTask;
    public Task DeleteAccountDataAsync(Guid accountId) => Task.CompletedTask;
    public Task UpdateIsReadAsync(Guid accountId, string folderName, string messageId, bool isRead) => Task.CompletedTask;
    public Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, bool isRead) => Task.CompletedTask;
    public Task UpdatePreviewAsync(Guid accountId, string folderName, string messageId, string preview) => Task.CompletedTask;
    public Task UpdatePreviewsBatchAsync(Guid accountId, string folderName, IEnumerable<(string MessageId, string Preview)> updates) => Task.CompletedTask;
    public Task<bool> HasSummariesMissingRecipientsAsync() => Task.FromResult(false);
    public Task UpsertDetailAsync(MailMessageDetail detail) => Task.CompletedTask;
    public Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId) => Task.FromResult<MailMessageDetail?>(null);
    public Task<string> GetMaxMessageKeyAsync(Guid accountId, string folderName) => Task.FromResult("0");
    public Task<HashSet<string>> GetAllMessageIdsAsync(Guid accountId, string folderName) => Task.FromResult(new HashSet<string>());
    public Task<int> CountSummariesAsync(Guid accountId) => Task.FromResult(0);
    public Task<DateTimeOffset?> GetOldestMessageDateAsync(Guid accountId) => Task.FromResult<DateTimeOffset?>(null);
    public Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId) => Task.CompletedTask;
    public Task UpdateFlagIdBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, string? flagId) => Task.CompletedTask;
}

sealed class StubContactService : IContactService
{
    public Task UpsertContactAsync(ContactModel contact) => Task.CompletedTask;
    public Task<List<ContactModel>> SearchContactsAsync(string prefix, CancellationToken ct = default) => Task.FromResult(new List<ContactModel>());
    public Task<List<ContactModel>> LoadAllContactsAsync() => Task.FromResult(new List<ContactModel>());
    public Task DeleteContactAsync(int id) => Task.CompletedTask;
    public Task<bool> UpdateContactAsync(int id, string displayName, string emailAddress) => Task.FromResult(true);

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
    public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067
    public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts, IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
    public Task SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.CompletedTask;
    public Task SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.CompletedTask;
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
