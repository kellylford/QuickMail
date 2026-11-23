using System;
using System.IO;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #1016: in Window mode, after replying from a message window, Enter on any message in the list
/// opened a window showing the message that had been replied to, until QuickMail was restarted.
/// <para>
/// The window's Reply writes its message into <see cref="MainViewModel.MessageDetail"/>, and in Window mode
/// nothing replaces it as the user moves on. Opening the next window then pre-filled its detail from
/// <c>MessageDetail</c> whenever <see cref="MainViewModel.SelectedMessage"/> was the message being opened,
/// which it always is, so every window showed the old detail. The check now has to be on the detail itself.
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

    private static MailMessageSummary Summary(string id, string subject, string folder = "INBOX") =>
        new() { AccountId = Account, FolderName = folder, MessageId = id, Subject = subject };

    private static MailMessageDetail Detail(string id, string subject, string folder = "INBOX") =>
        new() { AccountId = Account, FolderName = folder, MessageId = id, Subject = subject };

    [Fact]
    public void AfterReplyingFromAWindow_TheNextMessageIsNotGivenTheRepliedMessagesDetail()
    {
        var vm = CreateVm();
        var bugReport = Summary("2", "Bug Report");

        // What the window's Reply leaves behind: its own message in MessageDetail.
        vm.MessageDetail = Detail("1", "Testing");
        // Then the user arrows to another message; the list (or the Enter handler) selects it.
        vm.SelectedMessage = bugReport;

        Assert.Null(vm.LoadedDetailFor(bugReport));
    }

    [Fact]
    public void TheOpenMessagesOwnDetail_IsStillReused()
    {
        // Issue 48: a message already open in the reading pane opens in its window without a second load.
        var vm = CreateVm();
        var testing = Summary("1", "Testing");
        var detail = Detail("1", "Testing");
        vm.MessageDetail = detail;
        vm.SelectedMessage = testing;

        Assert.Same(detail, vm.LoadedDetailFor(testing));
    }

    [Fact]
    public void TheSameIdInAnotherFolder_IsADifferentMessage()
    {
        var vm = CreateVm();
        vm.MessageDetail = Detail("1", "Testing", folder: "Sent");

        Assert.Null(vm.LoadedDetailFor(Summary("1", "Testing", folder: "INBOX")));
    }

    [Fact]
    public void NothingLoaded_GivesNothing()
    {
        var vm = CreateVm();
        Assert.Null(vm.LoadedDetailFor(Summary("1", "Testing")));
    }

    [Fact]
    public void OpeningAMessageWindow_PrefillsOnlyThatMessagesDetail()
    {
        // The View half: OpenMessageInNewWindow needs a real window and WebView2, so it is read from source.
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml.cs"));
        var start = code.IndexOf("private void OpenMessageInNewWindow(", StringComparison.Ordinal);
        Assert.True(start >= 0, "OpenMessageInNewWindow is gone.");
        var body = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("_vm.LoadedDetailFor(summary)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("winVm.MessageDetail = _vm.MessageDetail", body, StringComparison.Ordinal);
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
