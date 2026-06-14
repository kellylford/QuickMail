using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class MainViewModelFlagTests
{
    private static MailMessageSummary FlaggedMsg(string id = "1") => new()
    {
        MessageId  = id,
        AccountId  = Guid.NewGuid(),
        FolderName = "INBOX",
        FlagId     = FlagDefinition.BuiltInFlagId.ToString(),
    };

    private static MailMessageSummary UnflaggedMsg(string id = "1") => new()
    {
        MessageId  = id,
        AccountId  = Guid.NewGuid(),
        FolderName = "INBOX",
    };

    private static MainViewModel MakeVm(IFlagService flagService, IEnumerable<MailMessageSummary>? messages = null)
    {
        ILocalStoreService store = messages != null
            ? new FilterableStoreForFlags(messages)
            : new StubLocalStoreService();
        return new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
            flagService: flagService);
    }

    [Fact]
    public async Task FlaggedFilter_ShowsOnlyFlaggedMessages()
    {
        var flagged   = FlaggedMsg("1");
        var unflagged = UnflaggedMsg("2");
        var vm = MakeVm(new StubFlagService(), new[] { flagged, unflagged });
        await vm.InitialLoadAsync();

        await vm.SetFilterCommand.ExecuteAsync("flagged");

        Assert.Single(vm.Messages);
        Assert.Equal("1", vm.Messages[0].MessageId);
    }

    [Fact]
    public async Task FlaggedFilter_AllUnflagged_ShowsEmpty()
    {
        var msgs = new[] { UnflaggedMsg("1"), UnflaggedMsg("2") };
        var vm = MakeVm(new StubFlagService(), msgs);
        await vm.InitialLoadAsync();

        await vm.SetFilterCommand.ExecuteAsync("flagged");

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task IsFilterFlagged_TrueWhenFlaggedFilterActive()
    {
        var vm = MakeVm(new StubFlagService());
        await vm.InitialLoadAsync();

        await vm.SetFilterCommand.ExecuteAsync("flagged");

        Assert.True(vm.IsFilterFlagged);
    }

    [Fact]
    public async Task IsFilterFlagged_FalseWhenOtherFilterActive()
    {
        var vm = MakeVm(new StubFlagService());
        await vm.InitialLoadAsync();

        await vm.SetFilterCommand.ExecuteAsync("unread");

        Assert.False(vm.IsFilterFlagged);
    }
}

/// <summary>Local store stub that serves a fixed set of messages for flag filter tests.</summary>
sealed class FilterableStoreForFlags : ILocalStoreService
{
    private readonly List<MailMessageSummary> _messages;

    public FilterableStoreForFlags(IEnumerable<MailMessageSummary> messages)
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
