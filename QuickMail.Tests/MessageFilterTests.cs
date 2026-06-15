using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Verifies that the active message filter is correctly applied to the Messages collection.
/// These tests run on the default STA thread (no WPF window needed).
/// </summary>
public class MessageFilterTests
{
    // ── Configurable stub ───────────────────────────────────────────────────

    sealed class FilterableStore : ILocalStoreService
    {
        private readonly List<MailMessageSummary> _messages;

        public FilterableStore(IEnumerable<MailMessageSummary> messages)
            => _messages = new List<MailMessageSummary>(messages);

        public void Initialize() { }
        public Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries) => Task.CompletedTask;
        public Task<List<MailMessageSummary>> LoadAllSummariesAsync() => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId) => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null) => Task.FromResult(new List<MailMessageSummary>(_messages));
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

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static MailMessageSummary MakeMsg(bool isRead = false, bool hasAttachments = false,
        bool isReplied = false, bool isForwarded = false, uint uid = 1)
        => new() { MessageId = uid.ToString(), IsRead = isRead, HasAttachments = hasAttachments,
                   IsReplied = isReplied, IsForwarded = isForwarded };

    private static readonly MailMessageSummary[] SampleMessages =
    [
        MakeMsg(isRead: false, uid: 1),                                             // truly unread
        MakeMsg(isRead: true,  uid: 2),                                             // read
        MakeMsg(isRead: true,  hasAttachments: true, uid: 3),                       // read + attachment
        MakeMsg(isRead: true,  isReplied: true, uid: 4),                            // replied (read)
        MakeMsg(isRead: true,  isForwarded: true, uid: 5),                          // forwarded (read)
        MakeMsg(isRead: false, isReplied: true, uid: 6),                            // replied but \Seen not set
        MakeMsg(isRead: false, isForwarded: true, uid: 7),                          // forwarded but \Seen not set
    ];

    private static MainViewModel MakeVm(IEnumerable<MailMessageSummary> messages)
    {
        var store = new FilterableStore(messages);
        return new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
    }

    private static async Task<MainViewModel> LoadedVm(IEnumerable<MailMessageSummary> messages)
    {
        var vm = MakeVm(messages);
        await vm.InitialLoadAsync();
        return vm;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FilterAll_ShowsAllMessages()
    {
        var vm = await LoadedVm(SampleMessages);
        await vm.SetFilterCommand.ExecuteAsync("all");
        Assert.Equal(SampleMessages.Length, vm.Messages.Count);
    }

    [Fact]
    public async Task FilterUnread_ShowsOnlyTrulyUnreadMessages()
    {
        var vm = await LoadedVm(SampleMessages);
        await vm.SetFilterCommand.ExecuteAsync("unread");
        Assert.All(vm.Messages, m => Assert.False(m.IsRead));
        Assert.All(vm.Messages, m => Assert.False(m.IsReplied));
        Assert.All(vm.Messages, m => Assert.False(m.IsForwarded));
    }

    [Fact]
    public async Task FilterUnread_ExcludesRepliedEvenWhenUnread()
    {
        // A message can have \Answered without \Seen; it must not appear in Unread filter.
        var msgs = new[] { MakeMsg(isRead: false, isReplied: true, uid: 1) };
        var vm = await LoadedVm(msgs);
        await vm.SetFilterCommand.ExecuteAsync("unread");
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task FilterUnread_ExcludesForwardedEvenWhenUnread()
    {
        // A message can have \Forwarded without \Seen; it must not appear in Unread filter.
        var msgs = new[] { MakeMsg(isRead: false, isForwarded: true, uid: 1) };
        var vm = await LoadedVm(msgs);
        await vm.SetFilterCommand.ExecuteAsync("unread");
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task FilterRead_ShowsOnlyReadMessages()
    {
        var vm = await LoadedVm(SampleMessages);
        await vm.SetFilterCommand.ExecuteAsync("read");
        Assert.All(vm.Messages, m => Assert.True(m.IsRead));
    }

    [Fact]
    public async Task FilterWithAttachments_ShowsOnlyMessagesWithAttachments()
    {
        var vm = await LoadedVm(SampleMessages);
        await vm.SetFilterCommand.ExecuteAsync("attachments");
        Assert.All(vm.Messages, m => Assert.True(m.HasAttachments));
        Assert.Single(vm.Messages);
    }

    [Fact]
    public async Task FilterReplied_ShowsOnlyRepliedMessages()
    {
        var vm = await LoadedVm(SampleMessages);
        await vm.SetFilterCommand.ExecuteAsync("replied");
        Assert.All(vm.Messages, m => Assert.True(m.IsReplied));
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task FilterForwarded_ShowsOnlyForwardedMessages()
    {
        var vm = await LoadedVm(SampleMessages);
        await vm.SetFilterCommand.ExecuteAsync("forwarded");
        Assert.All(vm.Messages, m => Assert.True(m.IsForwarded));
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task ActiveFilter_DefaultsToAll()
    {
        var vm = await LoadedVm(SampleMessages);
        Assert.Equal(MessageFilter.All, vm.ActiveFilter);
    }

    [Fact]
    public async Task IsFilterActive_FalseWhenAll()
    {
        var vm = await LoadedVm(SampleMessages);
        Assert.False(vm.IsFilterActive);
    }

    [Fact]
    public async Task IsFilterActive_TrueWhenNotAll()
    {
        var vm = await LoadedVm(SampleMessages);
        await vm.SetFilterCommand.ExecuteAsync("unread");
        Assert.True(vm.IsFilterActive);
    }

    [Fact]
    public async Task FilterFlagged_ShowsOnlyFlaggedMessages()
    {
        var flaggedMsg = new MailMessageSummary
        {
            MessageId  = "99",
            FlagId     = FlagDefinition.BuiltInFlagId.ToString(),
        };
        var msgs = SampleMessages.Concat(new[] { flaggedMsg }).ToList();
        var vm = await LoadedVm(msgs);

        await vm.SetFilterCommand.ExecuteAsync("flagged");

        Assert.Single(vm.Messages);
        Assert.Equal("99", vm.Messages[0].MessageId);
    }

    [Fact]
    public async Task FilterFlagged_NoFlaggedMessages_Empty()
    {
        var vm = await LoadedVm(SampleMessages);
        await vm.SetFilterCommand.ExecuteAsync("flagged");
        Assert.Empty(vm.Messages);
    }
}
