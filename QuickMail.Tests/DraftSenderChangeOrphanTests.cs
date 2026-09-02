// A sender change whose local tidy-up does not complete — issue #637.
//
// Changing the From account re-keys the stored row: the store keys on (id, account, folder), so the
// draft is written under the new account and the old row is dropped. The drop can fail — a busy
// database, or the local leg throwing after the re-key — and the key lived in a local that went out
// of scope with it. The old account's row then stayed queued and marked pending while the window
// reported plain success, and the next sweep uploaded the user's message into the mailbox they had
// just moved it out of.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftSenderChangeOrphanTests
{
    private static readonly Guid AccountA = Guid.Parse("5e5e5e5e-5e5e-5e5e-5e5e-5e5e5e5e5e5e");
    private static readonly Guid AccountB = Guid.Parse("6f6f6f6f-6f6f-6f6f-6f6f-6f6f6f6f6f6f");
    private static readonly Guid AccountC = Guid.Parse("8a8a8a8a-8a8a-8a8a-8a8a-8a8a8a8a8a8a");

    private static AccountModel Account(Guid id) => new()
    {
        Id = id, Username = $"user-{id:N}@example.com", AuthType = AuthType.OAuth2Google,
    };

    private static ComposeViewModel Compose(ILocalDraftService drafts, IMailService mail) => new(
        new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
        mail, drafts, new StubTemplateService())
    {
        SenderAccount = Account(AccountA),
        To = "someone@example.com",
        Subject = "Airport thoughts",
        Body = "Boarding soon.",
    };

    [Fact]
    public async Task ADiscardThatFails_IsRetriedOnTheNextSave()
    {
        // The row is not lost track of just because the delete did not take this time.
        var drafts = new TwoAccountDrafts { DiscardThrows = true };
        var mail   = new RecordingMailService { AppendDraftThrows = true };
        var vm = Compose(drafts, mail);

        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Equal([(AccountA, "local-1")], drafts.Rows);

        vm.SenderAccount = Account(AccountB);
        await vm.SaveDraftCommand.ExecuteAsync(null);

        // The discard was attempted and refused, so BOTH rows exist for the moment.
        Assert.Contains((AccountA, "local-1"), drafts.Rows);
        Assert.Contains((AccountB, "local-2"), drafts.Rows);

        drafts.DiscardThrows = false;
        vm.Body = "Boarding now.";
        await vm.SaveDraftCommand.ExecuteAsync(null);

        // Retried, and the old account is clear. Without the key held on the view model nothing
        // ever came back for it, and the sweep uploaded it into account A.
        Assert.DoesNotContain((AccountA, "local-1"), drafts.Rows);
    }

    [Fact]
    public async Task ALocalWriteThatFailsAfterTheReKey_StillLeavesTheOrphanKnown()
    {
        // The other route in: the re-key drops the old row's id from the window, then the save
        // throws. The window reported the draft saved to the server while account A's row stayed
        // queued.
        var drafts = new TwoAccountDrafts();
        var mail   = new RecordingMailService { AppendDraftThrows = true };
        var vm = Compose(drafts, mail);

        await vm.SaveDraftCommand.ExecuteAsync(null);

        drafts.SaveThrows = true;
        vm.SenderAccount = Account(AccountB);
        await vm.SaveDraftCommand.ExecuteAsync(null);

        drafts.SaveThrows = false;
        vm.Body = "Boarding now.";
        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.DoesNotContain((AccountA, "local-1"), drafts.Rows);
    }

    [Fact]
    public async Task ASecondSenderChange_DoesNotAbandonTheFirstOrphan()
    {
        // One slot kept the first orphan and silently dropped the next, so A→B with the discard
        // failing and then B→C left B's row queued and unreferenced -- and the sweep filed the
        // user's draft in the account they had moved it out of, which is the whole point of the
        // re-key.
        var drafts = new TwoAccountDrafts { DiscardThrows = true };
        var mail   = new RecordingMailService { AppendDraftThrows = true };
        var vm = Compose(drafts, mail);

        await vm.SaveDraftCommand.ExecuteAsync(null);          // A
        vm.SenderAccount = Account(AccountB);
        await vm.SaveDraftCommand.ExecuteAsync(null);          // B, A's discard refused
        vm.SenderAccount = Account(AccountC);
        await vm.SaveDraftCommand.ExecuteAsync(null);          // C, B's discard refused

        drafts.DiscardThrows = false;
        vm.Body = "Boarding now.";
        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.DoesNotContain((AccountA, "local-1"), drafts.Rows);
        Assert.DoesNotContain((AccountB, "local-2"), drafts.Rows);
        Assert.Contains((AccountC, "local-3"), drafts.Rows);
    }

    [Fact]
    public async Task AWindowThatCloses_TakesOneLastRunAtTheOrphan()
    {
        // The list lives on the window. A discard that kept failing until the user closed it left
        // that row queued under the account they had moved the draft OUT of, and nothing was left
        // to try again -- so the sweep filed their message there, which is the defect the re-key
        // exists to prevent, reached by waiting instead.
        var drafts = new TwoAccountDrafts { DiscardThrows = true };
        var mail   = new RecordingMailService { AppendDraftThrows = true };
        var vm = Compose(drafts, mail);

        await vm.SaveDraftCommand.ExecuteAsync(null);
        vm.SenderAccount = Account(AccountB);
        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Contains((AccountA, "local-1"), drafts.Rows);

        drafts.DiscardThrows = false;
        vm.Dispose();

        // The drain runs off the UI thread; give it a turn.
        for (var i = 0; i < 40 && drafts.Rows.Exists(r => r.Account == AccountA); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.DoesNotContain((AccountA, "local-1"), drafts.Rows);
    }

    /// <summary>A store that keys rows the way the real one does, and can be told to misbehave.</summary>
    private sealed class TwoAccountDrafts : ILocalDraftService
    {
        private int _next = 1;

        public List<(Guid Account, string Id)> Rows { get; } = [];
        public bool DiscardThrows { get; set; }
        public bool SaveThrows { get; set; }

        public Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft,
            string folderName, string? previousMessageId, CancellationToken ct = default)
        {
            if (SaveThrows) throw new InvalidOperationException("database is locked");
            // Re-saving under the same account keeps the id; a new account mints a new one, which
            // is what the re-key relies on.
            var existing = Rows.Find(r => r.Account == account.Id);
            if (existing.Id != null) return Task.FromResult(new PendingDraftSave(existing.Id, null));
            var id = $"local-{_next++}";
            Rows.Add((account.Id, id));
            return Task.FromResult(new PendingDraftSave(id, null));
        }

        public Task DiscardAsync(Guid accountId, string folderName, string messageId)
        {
            if (DiscardThrows) throw new InvalidOperationException("database is locked");
            Rows.RemoveAll(r => r.Account == accountId && r.Id == messageId);
            return Task.CompletedTask;
        }

        public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult<string?>("Drafts");
        public Task<ComposeModel?> LoadAsync(Guid a, string f, string id, CancellationToken ct = default)
            => Task.FromResult<ComposeModel?>(null);
        public Task<string?> GetSupersededServerIdAsync(Guid a, string f, string id) => Task.FromResult<string?>(null);
        public Task MarkSendFailedAsync(Guid a, string f, string id, string reason) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid a)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<string> ReadDeliveryNoticeAsync(Guid a, string f, string id) => Task.FromResult(string.Empty);
    }
}
