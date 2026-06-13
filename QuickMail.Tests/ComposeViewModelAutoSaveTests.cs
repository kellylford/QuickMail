using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for ComposeViewModel.AutoSaveAsync: when it saves, when it skips,
/// and how failures are surfaced (announced once, not every interval).
/// </summary>
public class ComposeViewModelAutoSaveTests
{
    private static (ComposeViewModel vm, RecordingMailService imap) MakeVm()
    {
        var imap = new RecordingMailService();
        var vm = new ComposeViewModel(
            new StubSmtpService(),
            new StubAccountService(),
            new StubCredentialService(),
            imap,
            new StubTemplateService());
        return (vm, imap);
    }

    private static AccountModel Account() => new() { Id = Guid.NewGuid() };

    [Fact]
    public async Task AutoSave_DirtyWithContent_SavesDraftQuietly()
    {
        var (vm, imap) = MakeVm();
        vm.SenderAccount = Account();
        vm.Subject = "important thought";   // marks dirty

        await vm.AutoSaveAsync();

        Assert.Equal(1, imap.AppendDraftCalls);
        Assert.StartsWith("Auto-saved", vm.AutoSaveText);
        Assert.False(vm.IsDirty);
        Assert.Equal(string.Empty, vm.StatusText); // success never touches the announced status
    }

    [Fact]
    public async Task AutoSave_SecondSaveReplacesFirstDraft()
    {
        var (vm, imap) = MakeVm();
        vm.SenderAccount = Account();
        vm.Subject = "v1";
        await vm.AutoSaveAsync();

        vm.Subject = "v2";  // dirty again
        await vm.AutoSaveAsync();

        Assert.Equal(2, imap.AppendDraftCalls);
        // The second append must pass the first append's message id so the
        // server-side draft is replaced rather than duplicated.
        Assert.Equal("draft-1", imap.LastReplaceMessageId);
    }

    [Fact]
    public async Task AutoSave_NotDirty_DoesNothing()
    {
        var (vm, imap) = MakeVm();
        vm.Seed(new ComposeModel { Body = "seeded reply text" }); // seeding is not a user edit
        vm.SenderAccount = Account();

        await vm.AutoSaveAsync();

        Assert.Equal(0, imap.AppendDraftCalls);
        Assert.Equal(string.Empty, vm.AutoSaveText);
    }

    [Fact]
    public async Task AutoSave_EmptyCompose_DoesNothing()
    {
        var (vm, imap) = MakeVm();
        vm.SenderAccount = Account();
        vm.Subject = "x";
        vm.Subject = "";   // dirty, but nothing worth keeping

        await vm.AutoSaveAsync();

        Assert.Equal(0, imap.AppendDraftCalls);
    }

    [Fact]
    public async Task AutoSave_EditingTemplate_DoesNothing()
    {
        var (vm, imap) = MakeVm();
        vm.Seed(new ComposeModel { Kind = ComposeKind.EditTemplate, Body = "template body" });
        vm.SenderAccount = Account();
        vm.Subject = "edited title"; // dirty

        await vm.AutoSaveAsync();

        Assert.Equal(0, imap.AppendDraftCalls);
    }

    [Fact]
    public async Task AutoSave_NoSenderAccount_DoesNothing()
    {
        var (vm, imap) = MakeVm();
        vm.Subject = "no account yet";

        await vm.AutoSaveAsync();

        Assert.Equal(0, imap.AppendDraftCalls);
    }

    [Fact]
    public async Task AutoSave_Failure_AnnouncesOnceUntilNextSuccess()
    {
        var (vm, imap) = MakeVm();
        imap.AppendDraftThrows = true;
        vm.SenderAccount = Account();
        vm.Subject = "will fail";

        var announcements = new List<string>();
        vm.AutoSaveFailed += msg => announcements.Add(msg);

        await vm.AutoSaveAsync();
        await vm.AutoSaveAsync();   // still dirty, fails again — must stay quiet

        Assert.Single(announcements);
        Assert.Equal("Auto-save failed", vm.AutoSaveText);

        // After a success the failure announcement re-arms.
        imap.AppendDraftThrows = false;
        await vm.AutoSaveAsync();
        Assert.StartsWith("Auto-saved", vm.AutoSaveText);

        imap.AppendDraftThrows = true;
        vm.Subject = "fails again";
        await vm.AutoSaveAsync();
        Assert.Equal(2, announcements.Count);
    }
}

/// <summary>IMailService stub that records draft appends and can simulate failure.</summary>
sealed class RecordingMailService : IMailService
{
    public int AppendDraftCalls { get; private set; }
    public string? LastReplaceMessageId { get; private set; }
    public bool AppendDraftThrows { get; set; }

    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) =>
        Task.FromResult<string?>("Drafts");

    public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
    {
        if (AppendDraftThrows) throw new InvalidOperationException("simulated append failure");
        AppendDraftCalls++;
        LastReplaceMessageId = replaceMessageId;
        return Task.FromResult($"draft-{AppendDraftCalls}");
    }

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
