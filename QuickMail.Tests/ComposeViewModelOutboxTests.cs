using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #637: on airport wifi, Save Draft and Send both used to end in a failure message and a
/// window that would not close. The compose window now tries the server first and falls back to
/// the local Outbox, and a send that never reached the server is queued rather than failed. What
/// is pinned here is the boundary: a server that answered "no" still fails in the window.
/// </summary>
public class ComposeViewModelOutboxTests
{
    private sealed class Harness
    {
        public StubSmtpService Smtp { get; } = new();
        public RecordingMailService Mail { get; } = new();
        public StubOutboxService Outbox { get; } = new();
        public StubConnectivityService Connectivity { get; } = new();
        public AccountModel Account { get; } = new()
        {
            Id = Guid.NewGuid(),
            Username = "kelly@example.com",
            AuthType = AuthType.OAuth2Google,
        };
        public ComposeViewModel Vm { get; }
        public StatusAnnouncementRecorder Status { get; }
        public int CloseRequests { get; private set; }

        public Harness(IMailService? mail = null, bool withOutbox = true, bool withConnectivity = true)
        {
            Vm = new ComposeViewModel(Smtp, new StubAccountService(), new StubCredentialService(),
                mail ?? Mail, new StubTemplateService(),
                outbox: withOutbox ? Outbox : null,
                connectivity: withConnectivity ? Connectivity : null);
            Status = StatusAnnouncementRecorder.Watch(Vm);
            Vm.CloseRequested += () => CloseRequests++;
            Vm.SenderAccount = Account;
            Vm.To = "someone@example.com";
            Vm.Subject = "Lunch";
            Vm.Body = "Friday?";
        }
    }

    private static SocketException Unreachable() => new((int)SocketError.HostUnreachable);

    private sealed class NoDraftsFolderMail : StubImapMailServiceBase
    {
        public override Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    // ── Save Draft ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveDraft_WhenTheServerFails_KeepsTheDraftOnThisComputer()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal(("Draft saved on this computer. It will upload when you're online.", AnnouncementCategory.Result), h.Status.Last);
        Assert.Equal(DraftSaveOutcome.SavedLocally, h.Vm.LastSaveOutcome);
        Assert.False(h.Vm.IsDirty);
        var queued = Assert.Single(h.Outbox.Enqueued);
        Assert.Equal(OutboxKind.Draft, queued.Kind);
        Assert.Equal(h.Account.Id, queued.AccountId);
        Assert.Equal("Lunch", queued.Compose.Subject);
        Assert.Null(queued.ExistingId);
    }

    [Fact]
    public async Task SaveDraft_Twice_ReplacesTheSameLocalRow()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);
        h.Vm.Subject = "Lunch (moved)";
        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal(2, h.Outbox.Enqueued.Count);
        Assert.Equal(h.Outbox.Enqueued[0].Id, h.Outbox.Enqueued[1].ExistingId);
        Assert.Single(h.Outbox.Items);
    }

    [Fact]
    public async Task SaveDraft_ServerSuccessAfterALocalSave_RemovesTheLocalRow()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();
        await h.Vm.SaveDraftCommand.ExecuteAsync(null);
        var localId = h.Outbox.Enqueued[0].Id;

        h.Mail.AppendDraftFailure = null;
        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal(("Draft saved.", AnnouncementCategory.Result), h.Status.Last);
        Assert.Equal(DraftSaveOutcome.SavedToServer, h.Vm.LastSaveOutcome);
        Assert.Equal([localId], h.Outbox.Removed);
        Assert.Equal(1, h.Mail.AppendDraftCalls);
    }

    [Fact]
    public async Task SaveDraft_WithNoDraftsFolder_StillKeepsTheDraftLocally()
    {
        // The text the user typed outranks folder purity: the Outbox row fails on upload with this
        // reason and stays reopenable, instead of the message being discarded on close.
        var h = new Harness(mail: new NoDraftsFolderMail());

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal(DraftSaveOutcome.SavedLocally, h.Vm.LastSaveOutcome);
        Assert.Single(h.Outbox.Enqueued);
    }

    [Fact]
    public async Task SaveDraft_WithoutAnOutbox_FailsTheWayItAlwaysDid()
    {
        var h = new Harness(withOutbox: false);
        h.Mail.AppendDraftFailure = Unreachable();

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.StartsWith("Save draft failed:", h.Status.Last.Text, StringComparison.Ordinal);
        Assert.Equal(AnnouncementCategory.Result, h.Status.Last.Category);
        Assert.Equal(DraftSaveOutcome.Failed, h.Vm.LastSaveOutcome);
        Assert.True(h.Vm.IsDirty);
    }

    [Fact]
    public async Task SaveDraft_WhenTheOutboxIsUnavailable_ReportsTheMissingDraftsFolder()
    {
        var h = new Harness(mail: new NoDraftsFolderMail());
        h.Outbox.IsAvailable = false;   // --online mode

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal("No Drafts folder found on this account.", h.Status.Last.Text);
        Assert.Equal(DraftSaveOutcome.Failed, h.Vm.LastSaveOutcome);
    }

    [Fact]
    public async Task SaveDraft_WhenTheLocalStoreAlsoFails_ReportsTheServerFailure()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();
        h.Outbox.EnqueueFailure = new InvalidOperationException("disk full");

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.StartsWith("Save draft failed:", h.Status.Last.Text, StringComparison.Ordinal);
        Assert.Equal(DraftSaveOutcome.Failed, h.Vm.LastSaveOutcome);
    }

    // ── Send ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_WhenTheServerCannotBeReached_QueuesAndCloses()
    {
        var h = new Harness();
        h.Smtp.SendFailure = new SocketException((int)SocketError.HostUnreachable);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(("Message queued. It will be sent when you're online.", AnnouncementCategory.Result), h.Status.Last);
        Assert.True(h.Vm.IsSent);
        Assert.False(h.Vm.IsDirty);
        Assert.Equal(1, h.CloseRequests);
        var queued = Assert.Single(h.Outbox.Enqueued);
        Assert.Equal(OutboxKind.Send, queued.Kind);
    }

    [Fact]
    public async Task Send_WhenTheServerRejects_StillFailsInTheWindow()
    {
        var h = new Harness();
        h.Smtp.SendFailure = new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted,
            SmtpStatusCode.MailboxUnavailable, "550 no such user");

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.StartsWith("Send failed:", h.Status.Last.Text, StringComparison.Ordinal);
        Assert.False(h.Vm.IsSent);
        Assert.Equal(0, h.CloseRequests);
        Assert.Empty(h.Outbox.Enqueued);
    }

    [Fact]
    public async Task Send_WhenTheAccountIsKnownOffline_QueuesWithoutTrying()
    {
        var h = new Harness();
        h.Connectivity.SetAccount(h.Account.Id, false);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(h.Smtp.Sent);
        Assert.Single(h.Outbox.Enqueued);
        Assert.True(h.Vm.IsSent);
        Assert.Equal(1, h.CloseRequests);
    }

    [Fact]
    public async Task Send_SupersedesTheLocalDraftRow()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();
        await h.Vm.SaveDraftCommand.ExecuteAsync(null);
        var draftRow = h.Outbox.Enqueued[0].Id;

        h.Smtp.SendFailure = new SocketException((int)SocketError.TimedOut);
        await h.Vm.SendCommand.ExecuteAsync(null);

        var send = h.Outbox.Enqueued[1];
        Assert.Equal(OutboxKind.Send, send.Kind);
        Assert.Equal(draftRow, send.ExistingId);
        Assert.Single(h.Outbox.Items);
    }

    [Fact]
    public async Task Send_SuccessAfterALocalSave_RemovesTheLocalRow()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();
        await h.Vm.SaveDraftCommand.ExecuteAsync(null);
        var localId = h.Outbox.Enqueued[0].Id;

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Single(h.Smtp.Sent);
        Assert.Equal([localId], h.Outbox.Removed);
        Assert.Equal(1, h.CloseRequests);
    }

    [Fact]
    public async Task Send_WithoutAnOutbox_FailsTheWayItAlwaysDid()
    {
        var h = new Harness(withOutbox: false);
        h.Smtp.SendFailure = new SocketException((int)SocketError.HostUnreachable);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.StartsWith("Send failed:", h.Status.Last.Text, StringComparison.Ordinal);
        Assert.False(h.Vm.IsSent);
        Assert.Equal(0, h.CloseRequests);
    }

    // ── Auto-save ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoSave_FallsBackLocallyAndSaysSoOnce()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();
        var notices = 0;
        h.Vm.AutoSaveNotice += _ => notices++;
        var failures = 0;
        h.Vm.AutoSaveFailed += _ => failures++;

        await h.Vm.AutoSaveAsync();
        h.Vm.Body = "Friday? Or Monday.";
        await h.Vm.AutoSaveAsync();

        Assert.Equal(1, notices);
        Assert.Equal(0, failures);
        Assert.StartsWith("Kept on this computer", h.Vm.AutoSaveText, StringComparison.Ordinal);
        Assert.Equal(2, h.Outbox.Enqueued.Count);
        Assert.Single(h.Outbox.Items);
        Assert.False(h.Vm.IsDirty);
    }

    [Fact]
    public async Task AutoSave_ServerSuccess_ResetsTheNoticeAndRemovesTheLocalRow()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();
        var notices = 0;
        h.Vm.AutoSaveNotice += _ => notices++;
        await h.Vm.AutoSaveAsync();

        h.Mail.AppendDraftFailure = null;
        h.Vm.Body = "edited";
        await h.Vm.AutoSaveAsync();
        Assert.Single(h.Outbox.Removed);
        Assert.StartsWith("Auto-saved", h.Vm.AutoSaveText, StringComparison.Ordinal);

        h.Mail.AppendDraftFailure = Unreachable();
        h.Vm.Body = "edited again";
        await h.Vm.AutoSaveAsync();
        Assert.Equal(2, notices);
    }

    // ── Reopened from the Outbox ────────────────────────────────────────────────

    [Fact]
    public async Task SeededFromAnOutboxRow_SavesBackIntoThatRowAndSkipsTheSignature()
    {
        var h = new Harness();
        h.Account.Signature = "-- Kelly";
        h.Vm.Seed(new ComposeModel
        {
            AccountId = h.Account.Id,
            OutboxId = "outbox-abc",
            To = "someone@example.com",
            Subject = "Reopened",
            Body = "text",
        });
        h.Vm.SenderAccount = h.Account;
        h.Mail.AppendDraftFailure = Unreachable();

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal("outbox-abc", Assert.Single(h.Outbox.Enqueued).ExistingId);
        Assert.DoesNotContain("-- Kelly", h.Vm.Body, StringComparison.Ordinal);
    }

    // ── Review fixes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveDraft_WhenTheServerRefuses_FailsInTheWindowInsteadOfQueuing()
    {
        // Over quota, a rejected append: the server answered. Queuing it would fail again on every
        // auto-save and announce a failure each time.
        var h = new Harness();
        h.Mail.AppendDraftFailure = new MailKit.Net.Imap.ImapCommandException(
            MailKit.Net.Imap.ImapCommandResponse.No, "NO [OVERQUOTA] Not enough space");

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.StartsWith("Save draft failed:", h.Status.Last.Text, StringComparison.Ordinal);
        Assert.Equal(DraftSaveOutcome.Failed, h.Vm.LastSaveOutcome);
        Assert.Empty(h.Outbox.Enqueued);
        Assert.True(h.Vm.IsDirty);
    }

    [Fact]
    public async Task SaveDraft_ThatReturnsEarly_CountsAsFailedSoTheWindowStaysOpen()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();
        await h.Vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Equal(DraftSaveOutcome.SavedLocally, h.Vm.LastSaveOutcome);

        // A stale success must not let a refused save close the window and lose the message.
        h.Vm.Attachments.Add(new AttachmentModel { FileName = "huge.iso", FileSize = 30_000_000, Content = [1] });
        await h.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal(DraftSaveOutcome.Failed, h.Vm.LastSaveOutcome);
        Assert.Contains("25 MB", h.Status.Last.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALocallyKeptDraftIsHeldWhileTheWindowIsOpenAndReleasedWhenItCloses()
    {
        var h = new Harness();
        h.Mail.AppendDraftFailure = Unreachable();

        await h.Vm.SaveDraftCommand.ExecuteAsync(null);
        var id = h.Outbox.Enqueued[0].Id;
        Assert.Contains(id, h.Outbox.Held);

        h.Vm.Dispose();
        Assert.DoesNotContain(id, h.Outbox.Held);
    }

    [Fact]
    public void AComposeReopenedFromTheOutboxHoldsItsRow()
    {
        var h = new Harness();
        h.Vm.Seed(new ComposeModel { AccountId = h.Account.Id, OutboxId = "outbox-held", To = "a@example.com" });
        Assert.Contains("outbox-held", h.Outbox.Held);
    }

    [Fact]
    public async Task AQueuedReplyIsStillAReply()
    {
        var h = new Harness();
        h.Vm.Seed(new ComposeModel { AccountId = h.Account.Id, Kind = ComposeKind.Reply, To = "a@example.com", InReplyToMessageId = "<p@x>" });
        h.Vm.SenderAccount = h.Account;
        h.Smtp.SendFailure = new SocketException((int)SocketError.HostUnreachable);

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(ComposeKind.Reply, Assert.Single(h.Outbox.Enqueued).Compose.Kind);
    }

    [Fact]
    public async Task ASendThatTimesOutWhileOnlineFailsInTheWindow()
    {
        var h = new Harness();
        h.Connectivity.IsOnline = true;
        h.Smtp.SendFailure = new OperationCanceledException();

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("Send timed out after 30 seconds. Try again.", h.Status.Last.Text);
        Assert.Empty(h.Outbox.Enqueued);
        Assert.Equal(0, h.CloseRequests);
    }
}
