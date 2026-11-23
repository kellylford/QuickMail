// Deleting on an account with no Trash folder — issue #999.
//
// Delete takes the rows out of the list first and asks the server afterwards. On an IMAP account
// with no Trash, the server half used to set \Deleted without expunging and report success, so the
// list emptied and the server kept every message. Now the backend says there is no Trash
// (TrashFolderNotFoundException, proved against a real server in the integration tests), and the
// ViewModel asks the user, through the View, whether to delete permanently. No puts the rows back.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DeleteWithoutTrashTests
{
    private static readonly Guid Work = Guid.Parse("99999999-1111-1111-1111-111111111111");

    private static MailMessageSummary Msg(string id, int daysAgo) => new()
    {
        MessageId  = id,
        AccountId  = Work,
        FolderName = "INBOX",
        From       = "someone@example.com",
        Subject    = $"Subject {id}",
        Date       = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required FolderedMailService Mail { get; init; }
        public required StatusAnnouncementRecorder Announced { get; init; }
        public List<(string Message, string Title)> Asked { get; } = new();

        /// <summary>What the stand-in View answers; null leaves the question unwired, as in a headless run.</summary>
        public bool? Answer
        {
            set
            {
                if (value is not { } answer) { Vm.ConfirmationRequested = null; return; }
                Vm.ConfirmationRequested = (message, title) => { Asked.Add((message, title)); return answer; };
            }
        }

        public MailMessageSummary Row(string id) => Vm.Messages.First(m => m.MessageId == id);

        public static async Task<Fixture> CreateAsync(bool hasTrash)
        {
            var inbox = new MailFolderModel
            {
                AccountId = Work, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox,
            };
            var mail = new FolderedMailService(
                new Dictionary<Guid, List<MailFolderModel>> { [Work] = [inbox] },
                new Dictionary<(Guid, string), List<MailMessageSummary>>
                {
                    [(Work, "INBOX")] = [Msg("a", 0), Msg("b", 1), Msg("c", 2)],
                });
            if (!hasTrash) mail.NoTrash.Add(Work);

            var vm = new MainViewModel(mail,
                new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
                new StubOAuthService(), new StubSyncService(), new StubConfigService(),
                new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
                new StubSmtpService());

            vm.Accounts.Add(new AccountModel
            {
                // OAuth only so connecting needs no stored password; the backend is the fake above.
                Id = Work, AccountName = "Home server", Username = "me@example.com", AuthType = AuthType.OAuth2Microsoft,
            });
            await vm.ConnectAllAccountsAsync();
            await vm.SelectFolderCommand.ExecuteAsync(inbox);
            Assert.Equal(3, vm.Messages.Count);

            return new Fixture { Vm = vm, Mail = mail, Announced = StatusAnnouncementRecorder.Watch(vm) };
        }
    }

    [Fact]
    public async Task NoTrash_AsksBeforeDeletingPermanently()
    {
        var f = await Fixture.CreateAsync(hasTrash: false);
        f.Answer = false;

        await f.Vm.DeleteMessagesAsync([f.Row("b")]);

        var (message, title) = Assert.Single(f.Asked);
        Assert.Equal("Delete Permanently", title);
        Assert.Contains("Home server has no Trash folder", message, StringComparison.Ordinal);
        Assert.Contains("Delete this message permanently?", message, StringComparison.Ordinal);
    }

    /// <summary>The reported bug: the rows must not just vanish while the server keeps them.</summary>
    [Fact]
    public async Task NoTrash_Declined_PutsTheMessageBackAndDeletesNothing()
    {
        var f = await Fixture.CreateAsync(hasTrash: false);
        f.Answer = false;
        var b = f.Row("b");

        await f.Vm.DeleteMessagesAsync([b]);

        Assert.Empty(f.Mail.PermanentlyDeleted);
        Assert.Empty(f.Mail.Trashed);
        Assert.Equal(new[] { "a", "b", "c" }, f.Vm.Messages.Select(m => m.MessageId));
        // Back where the user was when they pressed Delete.
        Assert.Same(b, f.Vm.SelectedMessage);
        Assert.Equal("The message was not deleted.", f.Vm.StatusText);
        // Not spoken: the user has just answered No, and the row is in front of them again.
        Assert.DoesNotContain(f.Announced.Announced, a => a.Category != AnnouncementCategory.Silent);
    }

    [Fact]
    public async Task NoTrash_Accepted_DeletesPermanently()
    {
        var f = await Fixture.CreateAsync(hasTrash: false);
        f.Answer = true;

        await f.Vm.DeleteMessagesAsync([f.Row("b")]);

        Assert.Equal(new[] { (Work, "INBOX", "b") }, f.Mail.PermanentlyDeleted);
        Assert.Equal(new[] { "a", "c" }, f.Vm.Messages.Select(m => m.MessageId));
        Assert.Equal("1 message deleted.", f.Vm.StatusText);
    }

    /// <summary>One question for the whole selection, not one per message.</summary>
    [Fact]
    public async Task NoTrash_SeveralMessages_AskOnce()
    {
        var f = await Fixture.CreateAsync(hasTrash: false);
        f.Answer = true;

        await f.Vm.DeleteMessagesAsync([f.Row("a"), f.Row("b"), f.Row("c")]);

        var (message, _) = Assert.Single(f.Asked);
        Assert.Contains("Delete these 3 messages permanently?", message, StringComparison.Ordinal);
        Assert.Equal(3, f.Mail.PermanentlyDeleted.Count);
        Assert.Empty(f.Vm.Messages);
    }

    [Fact]
    public async Task NoTrash_SeveralMessagesDeclined_AllComeBack()
    {
        var f = await Fixture.CreateAsync(hasTrash: false);
        f.Answer = false;

        await f.Vm.DeleteMessagesAsync([f.Row("a"), f.Row("c")]);

        Assert.Equal(new[] { "a", "b", "c" }, f.Vm.Messages.Select(m => m.MessageId));
        Assert.Equal("The messages were not deleted.", f.Vm.StatusText);
    }

    /// <summary>With nobody to ask, deleting for good is never assumed.</summary>
    [Fact]
    public async Task NoTrash_NoViewToAsk_KeepsTheMessages()
    {
        var f = await Fixture.CreateAsync(hasTrash: false);
        f.Answer = null;

        await f.Vm.DeleteMessagesAsync([f.Row("b")]);

        Assert.Empty(f.Mail.PermanentlyDeleted);
        Assert.Contains(f.Vm.Messages, m => m.MessageId == "b");
    }

    /// <summary>The optimistic decrement of the unread count is undone with the rows.</summary>
    [Fact]
    public async Task NoTrash_Declined_RestoresTheUnreadCount()
    {
        var f = await Fixture.CreateAsync(hasTrash: false);
        f.Answer = false;
        var account = f.Vm.Accounts.Single();
        account.TotalUnread = 3;
        Assert.False(f.Row("b").IsRead);

        await f.Vm.DeleteMessagesAsync([f.Row("b")]);

        Assert.Equal(3, account.TotalUnread);
    }

    /// <summary>The ordinary case is unchanged: no question, and the message goes to Trash.</summary>
    [Fact]
    public async Task WithTrash_DeletesToTrash_WithoutAsking()
    {
        var f = await Fixture.CreateAsync(hasTrash: true);
        f.Answer = false;

        await f.Vm.DeleteMessagesAsync([f.Row("b")]);

        Assert.Empty(f.Asked);
        Assert.Equal(new[] { (Work, "INBOX", "b") }, f.Mail.Trashed);
        Assert.Empty(f.Mail.PermanentlyDeleted);
        Assert.DoesNotContain(f.Vm.Messages, m => m.MessageId == "b");
    }
}
