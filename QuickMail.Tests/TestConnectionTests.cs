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
/// Test Connection used to probe IMAP only — the SMTP fields were copied onto the probe account and
/// never used, so an account could pass the test and then fail on its first send. It also refused to
/// test Graph accounts at all, telling the user to press a different button. These tests lock both
/// fixes, and are the first coverage this command has ever had.
/// </summary>
public class TestConnectionTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AddAccountViewModel NewVm(
        IMailService? imap = null, StubSmtpService? smtp = null, bool wireSmtp = true) =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = true },
            imap ?? new StubImapMailService(), new StubOAuthService(), Catalog,
            autoDiscover: null,
            sendMail: wireSmtp ? smtp ?? new StubSmtpService() : null);

    private static AddAccountViewModel WithGmailSettings(AddAccountViewModel vm)
    {
        vm.Username = "kelly@gmail.com";   // selects Gmail, fills both hosts
        vm.Password = "app-password";
        return vm;
    }

    [Fact]
    public async Task BothLegsPassingIsReportedForEach()
    {
        var vm = WithGmailSettings(NewVm());

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("IMAP: OK.", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("SMTP: OK.", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmtpIsActuallyProbedNotJustCopied()
    {
        var smtp = new StubSmtpService();
        var vm = WithGmailSettings(NewVm(smtp: smtp));

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(1, smtp.VerifyCalls);
    }

    [Fact]
    public async Task AFailingSmtpLegIsReportedWhileImapStillPasses()
    {
        // The exact combination the old IMAP-only check called a success.
        var smtp = new StubSmtpService { VerifyFailure = new InvalidOperationException("535 authentication failed") };
        var vm = WithGmailSettings(NewVm(smtp: smtp));

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("IMAP: OK.", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("535 authentication failed", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingImapLegDoesNotSuppressTheSmtpResult()
    {
        var smtp = new StubSmtpService();
        var vm = WithGmailSettings(NewVm(new FailingConnectMailService("no route to host"), smtp));

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("no route to host", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("SMTP: OK.", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(1, smtp.VerifyCalls);
    }

    [Fact]
    public async Task AnUnconfiguredSmtpHostSaysSoRatherThanClaimingSuccess()
    {
        var smtp = new StubSmtpService();
        var vm = NewVm(smtp: smtp);
        vm.SelectedProvider = Catalog.Other;
        vm.Username = "kelly@theideaplace.net";
        vm.ImapHost = "mail.theideaplace.net";
        vm.SmtpHost = string.Empty;

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("SMTP not configured.", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, smtp.VerifyCalls);
    }

    [Fact]
    public async Task WithNoSendServiceWiredTheSmtpLegSaysUncheckedNotOk()
    {
        var vm = WithGmailSettings(NewVm(wireSmtp: false));

        await vm.TestConnectionCommand.ExecuteAsync(null);

        // "not checked" is honest; "OK" would be a lie the user acts on.
        Assert.Contains("SMTP: SMTP not checked.", vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("SMTP: OK.", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingHostOrUsernameIsRejectedBeforeAnyConnection()
    {
        var smtp = new StubSmtpService();
        var vm = NewVm(smtp: smtp);
        vm.SelectedProvider = Catalog.Other;

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("Fill in IMAP host and username first.", vm.StatusText);
        Assert.Equal(0, smtp.VerifyCalls);
    }

    // ── Graph ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGraphAccountIsActuallyProbedInsteadOfBeingWavedThrough()
    {
        var imap = new RecordingConnectMailService();
        var vm = NewVm(imap);
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        vm.SelectedBackend = vm.AvailableBackends[1];   // Microsoft 365 (Graph)
        vm.Username = "kelly@contoso.com";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        // The old behavior was a message telling the user to press Sign in instead.
        Assert.Equal(1, imap.ConnectCalls);
        Assert.Contains("successful", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("use Sign in", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGraphProbeFailureIsReportedWithTheServerMessage()
    {
        var vm = NewVm(new FailingConnectMailService("mailbox not REST-enabled"));
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        vm.SelectedBackend = vm.AvailableBackends[1];
        vm.Username = "kelly@contoso.com";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("mailbox not REST-enabled", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGraphAccountWithNoSignedInUserIsToldToSignInFirst()
    {
        var imap = new RecordingConnectMailService();
        var vm = NewVm(imap);
        vm.SelectedProvider = Catalog.ById(ProviderCatalog.MicrosoftId);
        vm.SelectedBackend = vm.AvailableBackends[1];

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("Sign in with Microsoft first", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, imap.ConnectCalls);
    }

    [Fact]
    public async Task IsBusyIsClearedAfterEveryPath()
    {
        var ok = WithGmailSettings(NewVm());
        await ok.TestConnectionCommand.ExecuteAsync(null);
        Assert.False(ok.IsBusy);

        var broken = WithGmailSettings(NewVm(new FailingConnectMailService("boom")));
        await broken.TestConnectionCommand.ExecuteAsync(null);
        Assert.False(broken.IsBusy);
    }

    // ── Doubles ──────────────────────────────────────────────────────────────────

    private sealed class RecordingConnectMailService : StubMailServiceBase
    {
        public int ConnectCalls { get; private set; }

        public override Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
        {
            ConnectCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingConnectMailService(string message) : StubMailServiceBase
    {
        public override Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException(message));
    }

    /// <summary>
    /// Delegates every member to a real <see cref="StubImapMailService"/> so a subclass only has to
    /// override the one call it cares about.
    /// </summary>
    private abstract class StubMailServiceBase : IMailService
    {
        private readonly StubImapMailService _inner = new();

        public virtual Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => _inner.ConnectAsync(account, password, ct);
        public bool IsConnected(Guid accountId) => _inner.IsConnected(accountId);
        public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => _inner.DisconnectAsync(accountId, ct);
        public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) => _inner.GetFoldersAsync(accountId, ct);
        public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => _inner.GetMessageSummariesAsync(accountId, folderName, maxMessages, ct);
        public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => _inner.GetMessagesSinceDateAsync(accountId, folderName, since, ct);
        public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default) => _inner.GetMessagesSinceAsync(accountId, folderName, sinceMessageId, initialCount, ct);
        public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => _inner.GetMessageDetailAsync(accountId, folderName, messageId, ct);
        public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => _inner.PrefetchMessageDetailAsync(accountId, folderName, messageId, ct);
        public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => _inner.MarkReadAsync(accountId, folderName, messageId, ct);
        public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => _inner.MarkReadBatchAsync(accountId, folderName, messageIds, ct);
        public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default) => _inner.SetMessageFlaggedAsync(accountId, folderName, messageId, flagged, ct);
        public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => _inner.MoveToTrashAsync(accountId, folderName, messageId, ct);
        public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => _inner.MoveToTrashBatchAsync(accountId, folderName, messageIds, ct);
        public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => _inner.PermanentlyDeleteBatchAsync(accountId, folderName, messageIds, ct);
        public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => _inner.NoOpAsync(accountId, ct);
        public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => _inner.CountTrashMessagesAsync(accountId, ct);
        public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => _inner.EmptyTrashAsync(accountId, ct);
        public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => _inner.GetFolderMessageIdsAsync(accountId, folderName, ct);
        public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default) => _inner.FetchPreviewsAsync(accountId, folderName, messageIds, maxLines, ct);
        public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => _inner.PollAsync(accountId, folderName, ct);
        public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => _inner.GetInboxStatusAsync(accountId, ct);
        public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => _inner.FindDraftsFolderNameAsync(accountId, ct);
        public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default) => _inner.AppendDraftAsync(accountId, draft, replaceMessageId, ct);
        public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => _inner.AppendToSentAsync(accountId, sent, ct);
        public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default) => _inner.DownloadAttachmentAsync(accountId, folderName, messageId, partSpecifier, ct);
        public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => _inner.CopyMessagesAsync(accountId, folderName, messageIds, destinationFolder, ct);
        public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => _inner.MoveMessagesAsync(accountId, folderName, messageIds, destinationFolder, ct);
        public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => _inner.CreateFolderAsync(accountId, parentFolderName, name, ct);
        public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => _inner.DeleteFolderAsync(accountId, folderName, ct);
        public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => _inner.RenameFolderAsync(accountId, folderName, newName, newParentFolderName, ct);
        public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => _inner.CopyFolderAsync(accountId, folderName, destinationParentName, ct);
        public void Dispose() => _inner.Dispose();
    }
}
