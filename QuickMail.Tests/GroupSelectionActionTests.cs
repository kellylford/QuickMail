// Issue #566: what a mail command acts on when the selection is a group header, not a message.
//
// Selecting a conversation / sender / recipient header does not update SelectedMessage — the tree
// controller only assigns it for message nodes — so every command that read SelectedMessage acted
// on whatever had been selected before the user arrowed onto the header, or, with nothing selected
// yet, reported itself unavailable and did nothing at all. The View now hands the VM a
// SelectedGroupResolver, and these tests pin the two rules that resolver exists to enforce:
// reply-shaped actions answer the group's NEWEST message (one reply, not twenty), and
// delete-shaped actions act on the whole group.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class GroupSelectionActionTests
{
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class AccountsStub : IAccountService
    {
        public List<AccountModel> LoadAccounts() =>
            [new AccountModel { Id = AccountId, AccountName = "Work" }];
        public void SaveAccounts(List<AccountModel> a) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MailMessageSummary Message(string id, string subject = "Thread") => new()
    {
        MessageId  = id,
        AccountId  = AccountId,
        FolderName = "INBOX",
        Subject    = subject,
    };

    private static MainViewModel MakeVm() => new(
        new StubImapMailService(), new AccountsStub(), new StubCredentialService(),
        new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
        new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
        new StubRuleService(), new StubSmtpService());

    /// <summary>A three-message conversation, newest first, as the builders produce them.</summary>
    private static MailMessageSummary[] Conversation() =>
        [Message("newest"), Message("middle"), Message("oldest")];

    /// <summary>
    /// The reported symptom: no message had been selected, so the command was never even offered.
    /// </summary>
    [Fact]
    public void AGroupHeaderIsATarget_EvenWithNoSelectedMessage()
    {
        var vm = MakeVm();
        Assert.False(vm.CanActOnSelection());

        vm.SelectedGroupResolver = Conversation;

        Assert.True(vm.CanActOnSelection());
    }

    /// <summary>An empty group is not a target — there is nothing in it to act on.</summary>
    [Fact]
    public void AnEmptyGroupIsNotATarget()
    {
        var vm = MakeVm();
        vm.SelectedGroupResolver = () => [];

        Assert.False(vm.CanActOnSelection());
    }

    /// <summary>
    /// Reply, Reply All and Forward answer the newest message in the thread — the latest word in
    /// it — not the message that happened to be selected before the header was.
    /// </summary>
    [Theory]
    [InlineData("reply")]
    [InlineData("replyAll")]
    [InlineData("forward")]
    public async Task ReplyingToAGroup_AnswersItsNewestMessage(string which)
    {
        var vm    = MakeVm();
        var group = Conversation();
        vm.SelectedMessage      = Message("stale", "Some other thread");
        vm.SelectedGroupResolver = () => group;

        await (which switch
        {
            "reply"    => vm.ReplyCommand.ExecuteAsync(null),
            "replyAll" => vm.ReplyAllCommand.ExecuteAsync(null),
            _          => vm.ForwardCommand.ExecuteAsync(null),
        });

        Assert.Same(group[0], vm.SelectedMessage);
    }

    /// <summary>
    /// Delete acts on every message in the group. The count in the status text is the observable
    /// difference between filing the thread and filing one stale message.
    /// </summary>
    [Fact]
    public async Task DeletingAGroup_DeletesEveryMessageInIt()
    {
        var vm    = MakeVm();
        var group = Conversation();
        vm.SelectedMessage       = Message("stale", "Some other thread");
        vm.SelectedGroupResolver = () => group;

        await vm.DeleteMessageCommand.ExecuteAsync(null);

        Assert.Contains("3 messages deleted", vm.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fallback has to keep working: with a message selected and no group header, every command
    /// acts on that message exactly as it did before.
    /// </summary>
    [Fact]
    public async Task WithNoGroupSelected_TheSelectedMessageIsStillTheTarget()
    {
        var vm  = MakeVm();
        var msg = Message("only");
        vm.SelectedMessage       = msg;
        vm.SelectedGroupResolver = () => null;

        Assert.True(vm.CanActOnSelection());

        await vm.ReplyCommand.ExecuteAsync(null);
        Assert.Same(msg, vm.SelectedMessage);

        await vm.DeleteMessageCommand.ExecuteAsync(null);
        Assert.Contains("1 message deleted", vm.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// No resolver at all — the state of any host without group trees, and of the VM before the
    /// window wires itself up. Nothing may throw, and the selected message stays the target.
    /// </summary>
    [Fact]
    public async Task WithNoResolverWired_NothingChanges()
    {
        var vm  = MakeVm();
        var msg = Message("only");
        vm.SelectedMessage = msg;

        Assert.True(vm.CanActOnSelection());

        await vm.ReplyCommand.ExecuteAsync(null);
        Assert.Same(msg, vm.SelectedMessage);
    }

    /// <summary>
    /// HasMessageTarget is what the Message menu dims on. It is a snapshot, refreshed as the menu
    /// opens, because a header selection changes the answer without changing SelectedMessage.
    /// </summary>
    [Fact]
    public void RefreshMessageTarget_TracksTheGroupSelection()
    {
        var vm = MakeVm();
        vm.RefreshMessageTarget();
        Assert.False(vm.HasMessageTarget);

        vm.SelectedGroupResolver = Conversation;
        vm.RefreshMessageTarget();
        Assert.True(vm.HasMessageTarget);

        vm.SelectedGroupResolver = () => null;
        vm.RefreshMessageTarget();
        Assert.False(vm.HasMessageTarget);
    }
}
