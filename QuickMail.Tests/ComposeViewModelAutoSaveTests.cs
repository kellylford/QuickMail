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
    private static (ComposeViewModel vm, RecordingMailService imap, FakeLocalDraftService drafts) MakeVm()
    {
        var imap = new RecordingMailService();
        var drafts = new FakeLocalDraftService();
        var vm = new ComposeViewModel(
            new StubSmtpService(),
            new StubAccountService(),
            new StubCredentialService(),
            imap,
            drafts,
            new StubTemplateService());
        return (vm, imap, drafts);
    }

    private static AccountModel Account() => new() { Id = Guid.NewGuid() };

    [Fact]
    public async Task AutoSave_DirtyWithContent_SavesDraftQuietly()
    {
        var (vm, imap, _) = MakeVm();
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
        var (vm, imap, _) = MakeVm();
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
        var (vm, imap, _) = MakeVm();
        vm.Seed(new ComposeModel { Body = "seeded reply text" }); // seeding is not a user edit
        vm.SenderAccount = Account();

        await vm.AutoSaveAsync();

        Assert.Equal(0, imap.AppendDraftCalls);
        Assert.Equal(string.Empty, vm.AutoSaveText);
    }

    [Fact]
    public async Task AutoSave_EmptyCompose_DoesNothing()
    {
        var (vm, imap, _) = MakeVm();
        vm.SenderAccount = Account();
        vm.Subject = "x";
        vm.Subject = "";   // dirty, but nothing worth keeping

        await vm.AutoSaveAsync();

        Assert.Equal(0, imap.AppendDraftCalls);
    }

    [Fact]
    public async Task AutoSave_EditingTemplate_DoesNothing()
    {
        var (vm, imap, _) = MakeVm();
        vm.Seed(new ComposeModel { Kind = ComposeKind.EditTemplate, Body = "template body" });
        vm.SenderAccount = Account();
        vm.Subject = "edited title"; // dirty

        await vm.AutoSaveAsync();

        Assert.Equal(0, imap.AppendDraftCalls);
    }

    [Fact]
    public async Task AutoSave_NoSenderAccount_DoesNothing()
    {
        var (vm, imap, _) = MakeVm();
        vm.Subject = "no account yet";

        await vm.AutoSaveAsync();

        Assert.Equal(0, imap.AppendDraftCalls);
    }

    /// <summary>
    /// The server being unreachable is not an auto-save failure any more (#637). The draft is on
    /// disk, so the visible text says where it is and nothing is announced — announcing "failed" for
    /// a draft that was in fact saved is the wrong alarm, and it was the old behaviour.
    /// </summary>
    [Fact]
    public async Task AutoSave_ServerUnreachable_KeepsTheDraftAndStaysQuiet()
    {
        var (vm, imap, drafts) = MakeVm();
        imap.AppendDraftThrows = true;
        vm.SenderAccount = Account();
        vm.Subject = "airport draft";

        var announcements = new List<string>();
        vm.AutoSaveFailed += msg => announcements.Add(msg);

        await vm.AutoSaveAsync();

        Assert.Empty(announcements);
        Assert.Equal(1, drafts.SaveCalls);
        Assert.Single(drafts.Stored);
        Assert.StartsWith("Auto-saved on this computer", vm.AutoSaveText);
        Assert.True(vm.IsDraftPendingUpload);
        Assert.False(vm.IsDirty);
    }

    /// <summary>
    /// Once the server is reachable again the draft goes up, the local copy is dropped so the same
    /// draft does not sit in Drafts twice, and the wording stops hedging.
    /// </summary>
    [Fact]
    public async Task AutoSave_AfterReconnect_UploadsAndDropsTheLocalCopy()
    {
        var (vm, imap, drafts) = MakeVm();
        imap.AppendDraftThrows = true;
        vm.SenderAccount = Account();
        vm.Subject = "airport draft";
        await vm.AutoSaveAsync();

        imap.AppendDraftThrows = false;
        vm.Subject = "airport draft, revised";
        await vm.AutoSaveAsync();

        Assert.Empty(drafts.Stored);
        Assert.Equal(1, imap.AppendDraftCalls);
        Assert.StartsWith("Auto-saved", vm.AutoSaveText);
        Assert.DoesNotContain("this computer", vm.AutoSaveText);
        Assert.False(vm.IsDraftPendingUpload);
    }

    /// <summary>
    /// --online mode runs with no SQLite schema at all, so the local leg is unavailable rather than
    /// broken (see the runtime-modes table in docs/ARCHITECTURE.md). The server leg is then the only
    /// one there has ever been, and the draft must still save exactly as it did before #637.
    /// </summary>
    [Fact]
    public async Task AutoSave_WithNoLocalStore_FallsBackToTheServerAlone()
    {
        var (vm, imap, drafts) = MakeVm();
        drafts.SaveThrows = true;          // stands in for --online, where the store throws
        vm.SenderAccount = Account();
        vm.Subject = "online mode";

        var announcements = new List<string>();
        vm.AutoSaveFailed += msg => announcements.Add(msg);

        await vm.AutoSaveAsync();

        Assert.Equal(1, imap.AppendDraftCalls);
        Assert.Empty(announcements);
        Assert.False(vm.IsDraftPendingUpload);
        Assert.StartsWith("Auto-saved", vm.AutoSaveText);
        Assert.DoesNotContain("this computer", vm.AutoSaveText);
    }

    /// <summary>
    /// Both legs failing is the one remaining real failure — the draft is genuinely nowhere. Still
    /// announced exactly once until the next success rather than at every interval.
    /// </summary>
    [Fact]
    public async Task AutoSave_WhenNeitherLegSucceeds_AnnouncesOnceUntilNextSuccess()
    {
        var (vm, imap, drafts) = MakeVm();
        drafts.SaveThrows = true;
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
        drafts.SaveThrows = false;
        imap.AppendDraftThrows = false;
        await vm.AutoSaveAsync();
        Assert.StartsWith("Auto-saved", vm.AutoSaveText);

        drafts.SaveThrows = true;
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
    public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>([]);
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
    public void Dispose() { }
}
