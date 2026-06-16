using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>Tests for MainViewModel.Forward() — download flow, cancellation, and status reporting.</summary>
public class MainViewModelForwardTests
{
    private static readonly Guid TestAccountId = Guid.NewGuid();

    private static MailMessageSummary MakeSummary(string id = "msg1") => new()
    {
        MessageId  = id,
        AccountId  = TestAccountId,
        FolderName = "INBOX",
        Subject    = "Test",
        From       = "alice@example.com",
        To         = "bob@example.com",
    };

    private static MailMessageDetail MakeDetail(string id = "msg1",
        IEnumerable<AttachmentModel>? attachments = null,
        string plainBody = "hello", string htmlBody = "") => new()
    {
        MessageId     = id,
        AccountId     = TestAccountId,
        FolderName    = "INBOX",
        Subject       = "Test",
        From          = "alice@example.com",
        To            = "bob@example.com",
        PlainTextBody = plainBody,
        HtmlBody      = htmlBody,
        Attachments   = attachments != null ? [..attachments] : [],
        Date          = DateTimeOffset.UtcNow,
    };

    private static AttachmentModel MakeAtt(string name, bool loaded = false) => new()
    {
        FileName      = name,
        PartSpecifier = name,
        FileSize      = 1024,
        Content       = loaded ? [1, 2, 3] : null,
    };

    private static MainViewModel MakeVm(IMailService? imap = null) => new(
        imap ?? new StubImapMailService(),
        new StubAccountService(),
        new StubCredentialService(),
        new StubLocalStoreService(),
        new StubOAuthService(),
        new StubSyncService(),
        new StubConfigService(),
        new StubCommandRegistry(),
        new StubViewService(),
        new StubRuleService(),
        new StubSmtpService());

    private static void PrepareVm(MainViewModel vm, MailMessageDetail detail)
    {
        vm.SelectedMessage = MakeSummary(detail.MessageId);
        vm.MessageDetail   = detail;
    }

    // ── No attachments — dialog skipped ─────────────────────────────────────────

    [Fact]
    public async Task Forward_NoAttachments_SkipsDialog()
    {
        var vm = MakeVm();
        PrepareVm(vm, MakeDetail());

        bool dialogCalled = false;
        vm.SelectAttachmentsForForwardRequested += _ =>
        {
            dialogCalled = true;
            return Task.FromResult<IReadOnlyList<AttachmentModel>?>(Array.Empty<AttachmentModel>());
        };

        ComposeModel? opened = null;
        vm.ComposeRequested += m => opened = m;

        await vm.ForwardCommand.ExecuteAsync(null);

        Assert.False(dialogCalled, "Dialog should not open when there are no attachments");
        Assert.NotNull(opened);
    }

    // ── User cancels dialog — compose must NOT open ──────────────────────────────

    [Fact]
    public async Task Forward_UserCancelsDialog_ComposeNotOpened()
    {
        var detail = MakeDetail(attachments: [MakeAtt("file.pdf")]);
        var vm = MakeVm();
        PrepareVm(vm, detail);

        vm.SelectAttachmentsForForwardRequested += _ =>
            Task.FromResult<IReadOnlyList<AttachmentModel>?>(null); // null = cancelled

        ComposeModel? opened = null;
        vm.ComposeRequested += m => opened = m;

        await vm.ForwardCommand.ExecuteAsync(null);

        Assert.Null(opened);
    }

    // ── Partial selection — only selected attachments downloaded ─────────────────

    [Fact]
    public async Task Forward_PartialSelection_DownloadsOnlySelected()
    {
        var att1 = MakeAtt("keep.pdf");
        var att2 = MakeAtt("skip.pdf");
        var detail = MakeDetail(attachments: [att1, att2]);
        var vm = MakeVm();
        PrepareVm(vm, detail);

        // User selects only att1
        vm.SelectAttachmentsForForwardRequested += _ =>
            Task.FromResult<IReadOnlyList<AttachmentModel>?>([att1]);

        ComposeModel? opened = null;
        vm.ComposeRequested += m => opened = m;

        await vm.ForwardCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Single(opened!.Attachments);
        Assert.Equal("keep.pdf", opened.Attachments[0].FileName);
    }

    // ── Include none — compose opens with empty attachment list ──────────────────

    [Fact]
    public async Task Forward_IncludeNone_ComposeOpensWithNoAttachments()
    {
        var detail = MakeDetail(attachments: [MakeAtt("big.zip")]);
        var vm = MakeVm();
        PrepareVm(vm, detail);

        vm.SelectAttachmentsForForwardRequested += _ =>
            Task.FromResult<IReadOnlyList<AttachmentModel>?>(Array.Empty<AttachmentModel>());

        ComposeModel? opened = null;
        vm.ComposeRequested += m => opened = m;

        await vm.ForwardCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Empty(opened!.Attachments);
    }

    // ── Already-loaded attachments count as successful ───────────────────────────

    [Fact]
    public async Task Forward_AlreadyLoadedAttachment_CountsAsSuccess()
    {
        var loaded = MakeAtt("cached.pdf", loaded: true);
        var detail = MakeDetail(attachments: [loaded]);
        var vm = MakeVm();
        PrepareVm(vm, detail);

        vm.SelectAttachmentsForForwardRequested += atts =>
            Task.FromResult<IReadOnlyList<AttachmentModel>?>(atts);

        string? finalStatus = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusText))
                finalStatus = vm.StatusText;
        };

        ComposeModel? opened = null;
        vm.ComposeRequested += m => opened = m;

        await vm.ForwardCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Single(opened!.Attachments);
        // Status should mention "ready" (not "could not be downloaded")
        Assert.True(finalStatus == null || !finalStatus.Contains("could not"), finalStatus);
    }

    // ── No subscriber → include all (backward-compat) ───────────────────────────

    [Fact]
    public async Task Forward_NoSubscriber_IncludesAllAttachments()
    {
        var att1 = MakeAtt("a.pdf");
        var att2 = MakeAtt("b.pdf");
        var detail = MakeDetail(attachments: [att1, att2]);
        var vm = MakeVm();
        PrepareVm(vm, detail);

        // No SelectAttachmentsForForwardRequested subscriber

        ComposeModel? opened = null;
        vm.ComposeRequested += m => opened = m;

        await vm.ForwardCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Equal(2, opened!.Attachments.Count);
    }
}
