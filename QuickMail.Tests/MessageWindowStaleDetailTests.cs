using System;
using System.IO;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #1016: in Window mode, after replying from a message window, Enter on any message in the list
/// opened a window showing the message that had been replied to, until QuickMail was restarted.
/// <para>
/// The window's Reply wrote its message into <see cref="MainViewModel.MessageDetail"/>, and in Window mode
/// nothing replaces it as the user moves on. Opening the next window then pre-filled its detail from
/// <c>MessageDetail</c> whenever <see cref="MainViewModel.SelectedMessage"/> was the message being opened,
/// which it always is, so every window showed the old detail. The window's actions now pass their own
/// message instead of writing it there, and the pre-fill checks the detail itself.
/// </para>
/// </summary>
public class MessageWindowStaleDetailTests
{
    private static readonly Guid Account = Guid.NewGuid();

    private static MainViewModel CreateVm() =>
        new(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

    private static MailMessageSummary Summary(string id, string subject, string folder = "INBOX", Guid? account = null) =>
        new() { AccountId = account ?? Account, FolderName = folder, MessageId = id, Subject = subject };

    private static MailMessageDetail Detail(string id, string subject, string folder = "INBOX", Guid? account = null) =>
        new()
        {
            AccountId = account ?? Account, FolderName = folder, MessageId = id, Subject = subject,
            InternetMessageId = $"<{id}@example.com>",
        };

    // ── LoadedDetailFor: the pre-fill guard ─────────────────────────────────

    [Fact]
    public void ADetailLeftFromAnotherMessage_IsNotGivenToTheMessageBeingOpened()
    {
        // What a window's Reply used to leave behind: its own message in MessageDetail. The old check
        // compared SelectedMessage with the message being opened, which always matched.
        var vm = CreateVm();
        vm.MessageDetail = Detail("1", "Testing");

        Assert.Null(vm.LoadedDetailFor(Summary("2", "Bug Report")));
    }

    [Fact]
    public void TheOpenMessagesOwnDetail_IsStillReused()
    {
        // Issue 48: a message already open in the reading pane opens in its window without a second load.
        var vm = CreateVm();
        var detail = Detail("1", "Testing");
        vm.MessageDetail = detail;

        Assert.Same(detail, vm.LoadedDetailFor(Summary("1", "Testing")));
    }

    [Fact]
    public void TheSameIdInAnotherFolder_IsADifferentMessage()
    {
        var vm = CreateVm();
        vm.MessageDetail = Detail("1", "Testing", folder: "Sent");

        Assert.Null(vm.LoadedDetailFor(Summary("1", "Testing", folder: "INBOX")));
    }

    [Fact]
    public void TheSameIdInAnotherAccount_IsADifferentMessage()
    {
        var vm = CreateVm();
        vm.MessageDetail = Detail("1", "Testing", account: Guid.NewGuid());

        Assert.Null(vm.LoadedDetailFor(Summary("1", "Testing")));
    }

    [Fact]
    public void NothingLoaded_GivesNothing()
    {
        var vm = CreateVm();
        Assert.Null(vm.LoadedDetailFor(Summary("1", "Testing")));
    }

    // ── Replying from a window ──────────────────────────────────────────────

    [Fact]
    public async Task ReplyingFromAWindow_LeavesTheMainSelectionAndDetailAlone()
    {
        var vm = CreateVm();
        var listSelection = Summary("2", "Bug Report");
        vm.SelectedMessage = listSelection;
        ComposeModel? composed = null;
        vm.ComposeRequested += m => composed = m;

        await vm.ReplyFromWindowAsync(Summary("1", "Testing"), Detail("1", "Testing"), all: false);

        Assert.NotNull(composed);
        Assert.Equal("<1@example.com>", composed.InReplyToMessageId);
        Assert.Null(vm.MessageDetail);
        Assert.Same(listSelection, vm.SelectedMessage);
    }

    [Fact]
    public async Task ReplyingFromAWindow_GoesToTheWindowsMessage_NotTheSelectedConversationsNewest()
    {
        // A conversation header selected in the list points Reply at the conversation's newest message
        // (RetargetToGroupNewest). The window went through that command, so after Previous in the window
        // a reply went to the newest message instead of the one being read.
        var vm = CreateVm();
        var newest = Summary("3", "Re: Testing");
        var older  = Summary("1", "Testing");
        vm.SelectedGroupResolver = () => [newest, older];
        vm.MessageDetail = Detail("3", "Re: Testing");
        ComposeModel? composed = null;
        vm.ComposeRequested += m => composed = m;

        await vm.ReplyFromWindowAsync(older, Detail("1", "Testing"), all: false);

        Assert.NotNull(composed);
        Assert.Equal("<1@example.com>", composed.InReplyToMessageId);
    }

    [Fact]
    public async Task ForwardingFromAWindow_LeavesTheMainDetailAlone()
    {
        var vm = CreateVm();
        ComposeModel? composed = null;
        vm.ComposeRequested += m => composed = m;

        await vm.ForwardFromWindowAsync(Summary("1", "Testing"), Detail("1", "Testing"));

        Assert.NotNull(composed);
        Assert.Null(vm.MessageDetail);
    }

    // ── The View half, read from source (it needs a real window and WebView2) ──

    [Fact]
    public void OpeningAMessageWindow_PrefillsOnlyTheShowingMessagesDetail()
    {
        var body = OpenMessageInNewWindowSource();

        Assert.Contains("_vm.IsMessageOpen && _vm.LoadedDetailFor(summary)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("winVm.MessageDetail = _vm.MessageDetail", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageWindowsActions_NeverWriteTheMainDetailOrSelection()
    {
        var body = OpenMessageInNewWindowSource();

        Assert.DoesNotContain("_vm.MessageDetail =", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_vm.ReplyCommand", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_vm.ReplyAllCommand", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_vm.ForwardCommand", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachmentCommand", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachmentsCommand", body, StringComparison.Ordinal);
    }

    private static string OpenMessageInNewWindowSource()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml.cs"));
        var start = code.IndexOf("private void OpenMessageInNewWindow(", StringComparison.Ordinal);
        Assert.True(start >= 0, "OpenMessageInNewWindow is gone.");
        return code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
