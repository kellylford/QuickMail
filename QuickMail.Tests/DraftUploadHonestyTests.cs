// What the user is told when an upload does not happen, and what the second save leaves behind —
// issue #637.
//
// Round sixteen. Two failures of the same kind: the durable sentence said something that was not
// known to be true, and the tidy-up ran off what this call happened to write rather than off what
// the store actually holds.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftUploadHonestyTests
{
    private static readonly Guid AccountId = Guid.Parse("2b2b2b2b-2b2b-2b2b-2b2b-2b2b2b2b2b2b");

    private static AccountModel Account() => new()
    {
        Id = AccountId, Username = "samuel@interfree.ca", AuthType = AuthType.OAuth2Google,
    };

    // ── who is blamed ────────────────────────────────────────────────────────

    [Fact]
    public void AServerCommandFailure_IsTheServersVerdict()
    {
        // A renamed Drafts folder, a message the server will not accept, a refused login.
        Assert.True(SendFailure.IsServerVerdict(
            new MailKit.Net.Smtp.SmtpCommandException(
                MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted,
                MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable, "over quota")));
    }

    [Fact]
    public void ARefusedSignIn_IsNotAVerdictOnThisMessage()
    {
        // It was, until the failure taxonomy grew an account scope. Nothing about the draft is
        // wrong, so calling it a verdict on the draft sent the user to edit drafts for ever while
        // the actual fix -- signing in again -- went unsaid. Handled before this is asked now.
        Assert.False(SendFailure.IsServerVerdict(new MailKit.Security.AuthenticationException("no")));
    }

    [Fact]
    public void QuickMailsOwnBug_IsNotTheServersVerdict()
    {
        // The failure this exists for. These are thrown inside QuickMail before the server is
        // contacted at all, and every one of them used to reach the user, on the durable field, as
        // "Your mail server refused it: ..." with an instruction to save again that cannot work.
        Assert.False(SendFailure.IsServerVerdict(new NullReferenceException()));
        Assert.False(SendFailure.IsServerVerdict(new FormatException("'local-1' was not in a correct format")));
        Assert.False(SendFailure.IsServerVerdict(new KeyNotFoundException()));
    }

    [Fact]
    public void AFailedHandshake_IsNotTheServerRefusingTheMessage()
    {
        // A certificate or protocol mismatch is the user's to fix, but it is not a verdict on this
        // draft, and saying it is sends them looking in the wrong place.
        Assert.False(SendFailure.IsServerVerdict(new MailKit.Security.SslHandshakeException("bad cert")));
    }

    [Fact]
    public void NothingUnrecognised_IsPromotedToAVerdict()
    {
        // The opposite failure to IsTransient's: that one is closed so unknowns are REPORTED
        // rather than retried for ever. This one is closed so unknowns are not ATTRIBUTED.
        Assert.False(SendFailure.IsServerVerdict(new InvalidOperationException("something odd")));
        Assert.False(SendFailure.IsServerVerdict(null));
    }

    // ── what the second save leaves behind ───────────────────────────────────

    [Fact]
    public async Task ALocalWriteThatFailsOnTheSecondSave_StillTidiesTheRowTheFirstOneLeft()
    {
        // localId is assigned only inside the local leg, so a leg that threw left it null even
        // though a stored row existed under the id the FIRST save minted. The upload then happened,
        // the window reported the draft fully saved, and the row stayed marked pending -- so the
        // next sweep uploaded the same draft a second time.
        var drafts = new ScriptedDraftService();
        var mail   = new RecordingMailService();
        var vm = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            mail, drafts, new StubTemplateService())
        {
            SenderAccount = Account(), To = "someone@example.com", Subject = "Airport thoughts",
        };

        mail.AppendDraftThrows = true;                 // save one: local only
        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Contains("local-1", drafts.Stored);

        mail.AppendDraftThrows = false;                // save two: the store fails, the server does not
        drafts.SaveThrows = true;
        vm.Body = "Boarding now.";
        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.False(vm.IsDraftPendingUpload);         // the window says it reached the server
        Assert.DoesNotContain("local-1", drafts.Stored);   // ...so the row must not still be queued
        Assert.Contains("local-1", drafts.Discarded);
    }

    [Fact]
    public async Task ALocalWriteThatFails_StillTellsTheServerWhichDraftThisReplaces()
    {
        // A server draft edited offline: the local row records which server draft it supersedes,
        // and Seed deliberately does NOT carry that id on the window -- "the next save reads it
        // back from there". The only place it was read back was inside the local leg, so a leg that
        // threw appended with no replaces header at all, and the server kept the old draft AND
        // gained the new one: the duplication the header exists to prevent.
        var drafts = new ScriptedDraftService { Supersedes = "42" };
        drafts.Stored.Add("local-1");
        var mail   = new RecordingMailService();
        var vm = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            mail, drafts, new StubTemplateService());
        vm.Seed(new ComposeModel
        {
            AccountId = AccountId, DraftMessageId = "local-1", DraftFolderName = "Drafts",
            To = "someone@example.com", Subject = "Airport thoughts", Body = "Boarding soon.",
        });
        vm.SenderAccount = Account();

        drafts.SaveThrows = true;                      // the store will not take this save
        vm.Body = "Boarding now.";
        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal("42", mail.LastReplaceMessageId);
    }

    private sealed class ScriptedDraftService : ILocalDraftService
    {
        public HashSet<string> Stored { get; } = [];
        public List<string> Discarded { get; } = [];
        public bool SaveThrows { get; set; }
        public string? Supersedes { get; set; }

        public Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft,
            string folderName, string? previousMessageId, CancellationToken ct = default)
        {
            if (SaveThrows) throw new InvalidOperationException("database is locked");
            Stored.Add("local-1");
            return Task.FromResult(new PendingDraftSave("local-1", Supersedes));
        }

        public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult<string?>("Drafts");
        public Task<ComposeModel?> LoadAsync(Guid a, string f, string id, CancellationToken ct = default)
            => Task.FromResult<ComposeModel?>(null);
        public Task<string?> GetSupersededServerIdAsync(Guid a, string f, string id)
            => Task.FromResult(Stored.Contains(id) ? Supersedes : null);
        public Task MarkSendFailedAsync(Guid a, string f, string id, string reason) => Task.CompletedTask;
        public Task DiscardAsync(Guid a, string f, string id)
        {
            Stored.Remove(id);
            Discarded.Add(id);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid a)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<string> ReadDeliveryNoticeAsync(Guid a, string f, string id)
            => Task.FromResult(string.Empty);
    }
}
