// The claim that keeps the upload pass off a draft someone is editing — issue #637.
//
// Uploading a draft deletes the local row AND its stored bytes. Do that while a compose window is
// still open on it and the window's next auto-save re-creates the row having lost the header that
// says which server draft it replaces — so the next upload files a SECOND copy instead of replacing
// the first. The user ends up with two or three drafts where they wrote one, and an orphan left in
// Drafts after they send.
//
// The holder and the checker are different objects: every compose window takes its own claim, and
// the sweep that must honour it runs inside SyncService. That is why the state is static, and it is
// why these tests assert it across two separate callers — making it an instance field would
// silently disable the protection with every other test in the suite still passing.

using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class DraftClaimTests
{
    private static readonly Guid AccountId = Guid.Parse("6d6d6d6d-6d6d-6d6d-6d6d-6d6d6d6d6d6d");

    [Fact]
    public void AClaimIsVisibleToACompletelyDifferentCaller()
    {
        using var claim = DraftClaims.Claim(AccountId, "Drafts", "local-1");

        // If the state were per-instance this would be false, and the sweep would go on to upload
        // a draft a window has open.
        Assert.True(DraftClaims.IsClaimed(AccountId, "Drafts", "local-1"));
    }

    [Fact]
    public void ReleasingIsVisibleToo()
    {
        var claim = DraftClaims.Claim(AccountId, "Drafts", "local-2");
        claim.Dispose();

        Assert.False(DraftClaims.IsClaimed(AccountId, "Drafts", "local-2"));
    }

    [Fact]
    public void AClaimIsScopedToItsAccountFolderAndMessage()
    {
        using var claim = DraftClaims.Claim(AccountId, "Drafts", "local-3");

        // The store keys rows on all three, and so must this: two accounts each hold a folder
        // called "Drafts", and matching on fewer has been the recurring source of bugs here.
        Assert.False(DraftClaims.IsClaimed(Guid.NewGuid(), "Drafts", "local-3"));
        Assert.False(DraftClaims.IsClaimed(AccountId, "Sent", "local-3"));
        Assert.False(DraftClaims.IsClaimed(AccountId, "Drafts", "local-4"));
    }

    [Fact]
    public void TwoWindowsOnOneDraft_TheFirstToCloseDoesNotUnclaimIt()
    {
        var first  = DraftClaims.Claim(AccountId, "Drafts", "local-5");
        var second = DraftClaims.Claim(AccountId, "Drafts", "local-5");

        first.Dispose();
        // Counted, not set. Releasing on the first close would let the sweep upload the draft the
        // second window is still being typed into, and discard the bytes underneath it.
        Assert.True(DraftClaims.IsClaimed(AccountId, "Drafts", "local-5"));

        second.Dispose();
        Assert.False(DraftClaims.IsClaimed(AccountId, "Drafts", "local-5"));
    }

    [Fact]
    public void ReleasingTheSameHandleTwiceDoesNotDropTheOther()
    {
        var mine   = DraftClaims.Claim(AccountId, "Drafts", "local-6");
        var theirs = DraftClaims.Claim(AccountId, "Drafts", "local-6");

        mine.Dispose();
        mine.Dispose();   // idempotent: a window disposed twice must not drop the other's count

        Assert.True(DraftClaims.IsClaimed(AccountId, "Drafts", "local-6"));
        theirs.Dispose();
        Assert.False(DraftClaims.IsClaimed(AccountId, "Drafts", "local-6"));
    }

    [Fact]
    public void ReClaimingBeforeReleasing_NeverLeavesTheRowUnclaimed()
    {
        // What a compose window does on every auto-save: the id it owns can change, so it takes
        // the new claim and then drops the old one. Doing it the other way round leaves the row
        // unclaimed for the width of one call, on every auto-save, and a sweep landing in that gap
        // uploads the draft and discards the bytes the window is still editing.
        var first = DraftClaims.Claim(AccountId, "Drafts", "local-7");
        var again = DraftClaims.Claim(AccountId, "Drafts", "local-7");
        first.Dispose();

        Assert.True(DraftClaims.IsClaimed(AccountId, "Drafts", "local-7"));
        again.Dispose();
    }

    [Fact]
    public async Task AClaimedDraftIsNotUploaded()
    {
        var imap = new RecordingMailService();
        var drafts = new ScriptedDrafts();
        drafts.AddPending("local-held");
        var sync = new SyncService(imap, new StubLocalStoreService(), new StubConfigService(),
            new StubRuleService(), ui: new StubUiDispatcher(), probeMode: false, localDrafts: drafts);

        using (DraftClaims.Claim(AccountId, "Drafts", "local-held"))
        {
            var held = await sync.UploadPendingDraftsAsync(Account(), CancellationToken.None);
            Assert.Equal(0, held);
            Assert.Equal(0, imap.AppendDraftCalls);
        }

        // Released, so the next sweep takes it.
        var after = await sync.UploadPendingDraftsAsync(Account(), CancellationToken.None);
        Assert.Equal(1, after);
        Assert.Equal(1, imap.AppendDraftCalls);
    }

    private static QuickMail.Models.AccountModel Account() => new()
    {
        Id = AccountId, Username = "me@example.com",
    };

    /// <summary>Minimal pending list, so the test turns on the claim and nothing else.</summary>
    private sealed class ScriptedDrafts : ILocalDraftService
    {
        private readonly System.Collections.Generic.List<QuickMail.Models.MailMessageSummary> _pending = [];

        public void AddPending(string id) => _pending.Add(new QuickMail.Models.MailMessageSummary
        {
            MessageId = id, AccountId = AccountId, FolderName = "Drafts",
            Date = DateTimeOffset.UtcNow, IsPendingUpload = true,
        });

        public Task<System.Collections.Generic.IReadOnlyList<QuickMail.Models.MailMessageSummary>> GetPendingAsync(Guid accountId)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<QuickMail.Models.MailMessageSummary>>([.. _pending]);

        public Task<QuickMail.Models.ComposeModel?> LoadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
            => Task.FromResult<QuickMail.Models.ComposeModel?>(new() { AccountId = accountId, Subject = messageId });

        public Task<string?> GetSupersededServerIdAsync(Guid accountId, string folderName, string messageId)
            => Task.FromResult<string?>(null);

        public Task MarkSendFailedAsync(Guid accountId, string folderName, string messageId, string reason)
            => Task.CompletedTask;

        public Task DiscardAsync(Guid accountId, string folderName, string messageId)
        {
            _pending.RemoveAll(p => p.MessageId == messageId);
            return Task.CompletedTask;
        }

        public Task<PendingDraftSave> SaveAsync(QuickMail.Models.AccountModel account, QuickMail.Models.ComposeModel draft,
            string folderName, string? previousMessageId, CancellationToken ct = default)
            => throw new NotSupportedException("the upload pass never saves");

        public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult<string?>("Drafts");
    }
}
