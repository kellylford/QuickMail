using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class FlagServiceTests
{
    private static (FlagService svc, string dir) MakeService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlagServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var profile = new ProfileContext(dir);
        var svc = new FlagService(profile, new StubConfigService());
        return (svc, dir);
    }

    [Fact]
    public async Task LoadFlagDefinitions_NoFile_ReturnsBuiltIn()
    {
        var (svc, dir) = MakeService();
        try
        {
            var defs = await svc.LoadFlagDefinitionsAsync();
            Assert.Single(defs);
            Assert.Equal(FlagDefinition.BuiltInFlagId, defs[0].Id);
            Assert.True(defs[0].IsBuiltIn);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ToggleDefault_SetsFlagInLocalStore()
    {
        var (svc, dir) = MakeService();
        try
        {
            var store = new RecordingFlagStore();
            var mail  = new StubImapMailService();
            var msg   = new MailMessageSummary { MessageId = "1", AccountId = Guid.NewGuid(), FolderName = "INBOX" };

            await svc.ToggleDefaultFlagAsync(msg, store, mail);

            // Local store should have received the built-in flag id.
            Assert.Equal(1, store.UpdateFlagCalls);
            Assert.Equal(FlagDefinition.BuiltInFlagId.ToString(), store.LastFlagId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ToggleDefault_ClearsFlagInLocalStore()
    {
        var (svc, dir) = MakeService();
        try
        {
            var store = new RecordingFlagStore();
            var mail  = new StubImapMailService();
            var msg   = new MailMessageSummary
            {
                MessageId  = "1",
                AccountId  = Guid.NewGuid(),
                FolderName = "INBOX",
                FlagId     = FlagDefinition.BuiltInFlagId.ToString(),
            };

            await svc.ToggleDefaultFlagAsync(msg, store, mail);

            // Clear is a null flag id.
            Assert.Equal(1, store.UpdateFlagCalls);
            Assert.Null(store.LastFlagId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CorruptJson_RecoversWithBuiltInFlag()
    {
        var (svc, dir) = MakeService();
        try
        {
            // Write corrupt JSON
            var flagsFile = Path.Combine(dir, "flags.json");
            await File.WriteAllTextAsync(flagsFile, "{{NOT_VALID_JSON");

            var defs = await svc.LoadFlagDefinitionsAsync();

            Assert.Single(defs);
            Assert.Equal(FlagDefinition.BuiltInFlagId, defs[0].Id);
            // Backup file should exist
            Assert.True(Directory.GetFiles(dir, "flags.json.bak-*").Length > 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SaveFlagDefinitions_RaisesChanged()
    {
        var (svc, dir) = MakeService();
        try
        {
            bool raised = false;
            svc.FlagDefinitionsChanged += (_, _) => raised = true;

            await svc.SaveFlagDefinitionsAsync(new List<FlagDefinition> { FlagDefinition.CreateBuiltIn() });

            Assert.True(raised);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task GetKDefaultFlag_DefaultsToBuiltIn()
    {
        var (svc, dir) = MakeService();
        try
        {
            var kFlag = await svc.GetKDefaultFlagAsync();
            Assert.Equal(FlagDefinition.BuiltInFlagId, kFlag.Id);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>Records UpdateFlagIdAsync calls so tests can assert without a WPF dispatcher.</summary>
sealed class RecordingFlagStore : ILocalStoreService
{
    public int UpdateFlagCalls { get; private set; }
    public string? LastFlagId  { get; private set; }

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
    public Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId)
    {
        UpdateFlagCalls++;
        LastFlagId = flagId;
        return Task.CompletedTask;
    }
    public Task UpdateFlagIdBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, string? flagId) => Task.CompletedTask;
}
