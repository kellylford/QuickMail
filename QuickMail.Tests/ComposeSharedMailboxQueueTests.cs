using System;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #614: a shared mailbox sends through its parent's sign-in, so while it is disconnected a send
/// from it can only fail. The compose window queues the message in the Outbox instead, and it goes out
/// when the mailbox connects again. The compose window learns what is connected from the main window:
/// the accounts it loads from disk are never marked connected.
/// </summary>
public class ComposeSharedMailboxQueueTests
{
    private sealed class Harness
    {
        public StubSmtpService Smtp { get; } = new();
        public StubOutboxService Outbox { get; } = new();
        public AccountModel Account { get; }
        public ComposeViewModel Vm { get; }
        public StatusAnnouncementRecorder Status { get; }
        public int CloseRequests { get; private set; }

        public Harness(bool shared, bool? connected, bool withOutbox = true)
        {
            Account = new AccountModel
            {
                Id = Guid.NewGuid(), AccountName = shared ? "Support" : "Work",
                Username = shared ? "support@example.com" : "me@example.com",
                AuthType = AuthType.OAuth2Microsoft, BackendKind = BackendKind.MicrosoftGraph,
                IsShared = shared, ParentAccountId = shared ? Guid.NewGuid() : null,
                SharedAddress = shared ? "support@example.com" : null,
            };
            Func<Guid, bool>? isConnected = connected is { } c ? _ => c : null;
            Vm = new ComposeViewModel(Smtp, new StubAccountService(), new StubCredentialService(),
                new StubImapMailService(), new StubTemplateService(),
                outbox: withOutbox ? Outbox : null, isAccountConnected: isConnected);
            Status = StatusAnnouncementRecorder.Watch(Vm);
            Vm.CloseRequested += () => CloseRequests++;
            Vm.SenderAccount = Account;
            Vm.To = "someone@example.com";
            Vm.Subject = "Rota";
            Vm.Body = "Next week";
        }
    }

    [Fact]
    public async Task DisconnectedSharedMailbox_QueuesWithoutTrying()
    {
        var h = new Harness(shared: true, connected: false);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(h.Smtp.Sent);
        var queued = Assert.Single(h.Outbox.Enqueued);
        Assert.Equal(OutboxKind.Send, queued.Kind);
        Assert.Equal(h.Account.Id, queued.AccountId);
        Assert.Equal(("Message queued. It will be sent when Support is connected again.", AnnouncementCategory.Result), h.Status.Last);
        Assert.True(h.Vm.IsSent);
        Assert.Equal(1, h.CloseRequests);
    }

    [Fact]
    public async Task ConnectedSharedMailbox_Sends()
    {
        var h = new Harness(shared: true, connected: true);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Single(h.Smtp.Sent);
        Assert.Empty(h.Outbox.Enqueued);
    }

    [Fact]
    public async Task SharedMailbox_SignInLapsesDuringTheSend_Queues()
    {
        // Connected when Send was pressed, but the parent's sign-in turned out to have lapsed.
        var h = new Harness(shared: true, connected: true);
        h.Smtp.SendFailure = new InteractiveSignInRequiredException("Shared mailbox 'support@example.com' is disconnected.");

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Single(h.Outbox.Enqueued);
        Assert.Equal("Message queued. It will be sent when Support is connected again.", h.Status.Last.Text);
    }

    [Fact]
    public async Task DisconnectedNormalAccount_IsNotAffected()
    {
        // #614 is about shared mailboxes; an ordinary account keeps its existing send path.
        var h = new Harness(shared: false, connected: false);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Single(h.Smtp.Sent);
        Assert.Empty(h.Outbox.Enqueued);
    }

    [Fact]
    public async Task NoConnectionStateGiven_SendsAsBefore()
    {
        var h = new Harness(shared: true, connected: null);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Single(h.Smtp.Sent);
        Assert.Empty(h.Outbox.Enqueued);
    }

    [Fact]
    public async Task WithoutAnOutbox_DisconnectedSharedMailbox_TriesTheSend()
    {
        // --online mode has no Outbox to queue into, so the send is attempted as it always was.
        var h = new Harness(shared: true, connected: false, withOutbox: false);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Single(h.Smtp.Sent);
    }
}
