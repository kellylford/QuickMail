using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #781: auto-save wrote a draft every two minutes and then stopped for good, with nothing in
/// the log. One save never returned, <c>IsBusy</c> stayed true, and every later tick skipped. A draft
/// save now gives up at its deadline whether or not the code beneath it listens to its token, keeps
/// the draft on this computer the way an unreachable server does (#637), and a save that comes back
/// after it was given up on cannot overwrite what a newer save recorded.
/// </summary>
public class ComposeViewModelStuckSaveTests
{
    /// <summary>
    /// A mail service whose draft appends do whatever the test queued: complete, hang without ever
    /// looking at the token, or hang until the token fires.
    /// </summary>
    private sealed class ScriptedDraftMail : StubImapMailServiceBase
    {
        private readonly Queue<Func<CancellationToken, Task<string>>> _appends = new();
        private int _defaultId;

        public List<string?> ReplaceIds { get; } = [];
        public int AppendCalls { get; private set; }

        public void Next(Func<CancellationToken, Task<string>> append) => _appends.Enqueue(append);

        /// <summary>An append that never completes and ignores its token — the #781 shape.</summary>
        public TaskCompletionSource<string> NextHangs()
        {
            var tcs = new TaskCompletionSource<string>();
            Next(_ => tcs.Task);
            return tcs;
        }

        public List<(string Folder, string Id)> Trashed { get; } = [];

        public override Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            Trashed.Add((folderName, messageId));
            return Task.CompletedTask;
        }

        public override Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult<string?>("Drafts");

        public override Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
        {
            AppendCalls++;
            ReplaceIds.Add(replaceMessageId);
            return _appends.Count > 0
                ? _appends.Dequeue()(ct)
                : Task.FromResult($"saved-{++_defaultId}");
        }
    }

    private sealed class Harness
    {
        public ScriptedDraftMail Mail { get; } = new();
        public StubOutboxService Outbox { get; } = new();
        public ComposeViewModel Vm { get; }
        public List<string> Notices { get; } = [];
        public List<string> Failures { get; } = [];

        public Harness(bool withOutbox = true)
        {
            Vm = new ComposeViewModel(new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
                Mail, new StubTemplateService(), outbox: withOutbox ? Outbox : null)
            {
                // Short enough to keep the suite quick, long enough that a save which does complete
                // is never mistaken for a stuck one.
                DraftSaveTimeout = TimeSpan.FromMilliseconds(150),
                DraftSaveAbandonGrace = TimeSpan.FromMilliseconds(150),
            };
            Vm.AutoSaveNotice += Notices.Add;
            Vm.AutoSaveFailed += Failures.Add;
            // OAuth, so a send needs no stored password.
            Vm.SenderAccount = new AccountModel { Id = Guid.NewGuid(), Username = "kelly@example.com", AuthType = AuthType.OAuth2Google };
            Vm.To = "someone@example.com";
            Vm.Subject = "Lunch";
            Vm.Body = "Friday?";
        }
    }

    /// <summary>The save under test must return on its own; this only stops a regression hanging the run.</summary>
    private static Task Bounded(Task t) => t.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    [Fact]
    public async Task AutoSave_WhenTheSaveNeverReturns_GivesUpAndReleasesIsBusy()
    {
        var h = new Harness();
        h.Mail.NextHangs();

        await Bounded(h.Vm.AutoSaveAsync());

        Assert.False(h.Vm.IsBusy);
    }

    [Fact]
    public async Task AutoSave_WhenTheSaveNeverReturns_KeepsTheDraftOnThisComputer()
    {
        var h = new Harness();
        h.Mail.NextHangs();

        await Bounded(h.Vm.AutoSaveAsync());

        var kept = Assert.Single(h.Outbox.Enqueued);
        Assert.Equal(OutboxKind.Draft, kept.Kind);
        Assert.Equal("Lunch", kept.Compose.Subject);
        Assert.StartsWith("Kept on this computer", h.Vm.AutoSaveText, StringComparison.Ordinal);
        Assert.Single(h.Notices);
        Assert.Empty(h.Failures);
        Assert.False(h.Vm.IsDirty);
    }

    [Fact]
    public async Task AutoSave_AfterASaveThatNeverReturned_TheNextTickSavesAgain()
    {
        var h = new Harness();
        h.Mail.NextHangs();
        await Bounded(h.Vm.AutoSaveAsync());

        h.Vm.Body = "Friday at noon?";   // edited since the last tick
        await Bounded(h.Vm.AutoSaveAsync());

        Assert.Equal(2, h.Mail.AppendCalls);
        Assert.StartsWith("Auto-saved", h.Vm.AutoSaveText, StringComparison.Ordinal);
        Assert.False(h.Vm.IsDirty);
        Assert.False(h.Vm.IsBusy);
        // The server now has the draft, so the copy kept on this computer is no longer needed.
        Assert.Equal([h.Outbox.Enqueued[0].Id], h.Outbox.Removed);
    }

    [Fact]
    public async Task AutoSave_WhenTheSaveHonorsItsTimeout_KeepsTheDraftOnThisComputer()
    {
        // The well-behaved case: whatever is underneath listens to the token and gives up first.
        var h = new Harness();
        h.Mail.Next(async ct => { await Task.Delay(Timeout.Infinite, ct); return "never"; });

        await Bounded(h.Vm.AutoSaveAsync());

        Assert.False(h.Vm.IsBusy);
        Assert.Single(h.Outbox.Enqueued);
        Assert.StartsWith("Kept on this computer", h.Vm.AutoSaveText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoSave_WhenTheSaveNeverReturnsAndThereIsNoOutbox_ReportsTheFailureOnce()
    {
        var h = new Harness(withOutbox: false);   // --online mode
        h.Mail.NextHangs();

        await Bounded(h.Vm.AutoSaveAsync());

        Assert.False(h.Vm.IsBusy);
        Assert.Equal("Auto-save failed", h.Vm.AutoSaveText);
        Assert.Single(h.Failures);
        Assert.True(h.Vm.IsDirty);
    }

    [Fact]
    public async Task AutoSave_WhenTheWindowClosesDuringAStuckSave_ReturnsWithoutKeepingACopy()
    {
        // Closing decides the draft's fate through its own save; an abandoned auto-save must neither
        // hold the close up nor write a local copy behind its back.
        var h = new Harness();
        h.Vm.DraftSaveTimeout = TimeSpan.FromSeconds(30);
        h.Mail.NextHangs();

        var tick = h.Vm.AutoSaveAsync();
        h.Vm.CancelAutoSave();
        await Bounded(tick);

        Assert.False(h.Vm.IsBusy);
        Assert.Empty(h.Outbox.Enqueued);
        Assert.Empty(h.Failures);
    }

    [Fact]
    public async Task SaveDraft_WhenTheSaveNeverReturns_KeepsTheDraftOnThisComputer()
    {
        // Ctrl+S and the save offered when closing go through the same path, and before #781 a stuck
        // one also left the Save Draft command disabled for the life of the window.
        var h = new Harness();
        h.Mail.NextHangs();

        await Bounded(h.Vm.SaveDraftCommand.ExecuteAsync(null));

        Assert.Equal(DraftSaveOutcome.SavedLocally, h.Vm.LastSaveOutcome);
        Assert.False(h.Vm.IsBusy);
        Assert.False(h.Vm.SaveDraftCommand.IsRunning);
        Assert.Single(h.Outbox.Enqueued);
    }

    [Fact]
    public async Task AbandonedSave_FinishingAfterANewerSave_DoesNotOverwriteIt()
    {
        var h = new Harness();
        var stuck = h.Mail.NextHangs();
        await Bounded(h.Vm.AutoSaveAsync());           // abandoned

        h.Vm.Body = "Friday at noon?";
        await Bounded(h.Vm.AutoSaveAsync());           // saved-1
        stuck.SetResult("late");                       // the first save turns up after all
        await Bounded(h.Vm.AbandonedDraftSave);

        h.Vm.Body = "Friday at one?";
        await Bounded(h.Vm.AutoSaveAsync());

        // The third save replaces the newer save's draft, not the late one.
        Assert.Equal("saved-1", h.Mail.ReplaceIds[^1]);
    }

    [Fact]
    public async Task AbandonedSave_FinishingWithNoNewerSave_IsTheDraftTheNextSaveReplaces()
    {
        // Nothing newer exists, so the late copy is the one on the server; adopting it means the next
        // save replaces it instead of leaving it behind as a duplicate draft.
        var h = new Harness();
        var stuck = h.Mail.NextHangs();
        await Bounded(h.Vm.AutoSaveAsync());           // abandoned, kept locally
        stuck.SetResult("late");
        await Bounded(h.Vm.AbandonedDraftSave);

        h.Vm.Body = "Friday at noon?";
        await Bounded(h.Vm.AutoSaveAsync());

        Assert.Equal("late", h.Mail.ReplaceIds[^1]);
    }

    [Fact]
    public async Task AbandonedSave_FailingLate_ChangesNothing()
    {
        var h = new Harness();
        var stuck = h.Mail.NextHangs();
        await Bounded(h.Vm.AutoSaveAsync());
        stuck.SetException(new InvalidOperationException("too late to matter"));
        await Bounded(h.Vm.AbandonedDraftSave);   // observed, not thrown

        h.Vm.Body = "Friday at noon?";
        await Bounded(h.Vm.AutoSaveAsync());

        Assert.Null(h.Mail.ReplaceIds[^1]);
        Assert.StartsWith("Auto-saved", h.Vm.AutoSaveText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveDraft_WhenTheSaveNeverReturnsAndThereIsNoOutbox_FailsWithTheTimeout()
    {
        var h = new Harness(withOutbox: false);   // --online mode
        h.Mail.NextHangs();

        await Bounded(h.Vm.SaveDraftCommand.ExecuteAsync(null));

        Assert.Equal(DraftSaveOutcome.Failed, h.Vm.LastSaveOutcome);
        Assert.StartsWith("Save draft failed: Saving the draft did not finish within", h.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(h.Vm.IsBusy);
        Assert.True(h.Vm.IsDirty);
    }

    [Fact]
    public async Task AbandonedSave_FinishingLateAndCurrent_DropsTheCopyKeptOnThisComputer()
    {
        // The Outbox row replaces the draft id it was given, not the late one, so left alone it
        // would upload a second copy beside the late one when the window closes.
        var h = new Harness();
        var stuck = h.Mail.NextHangs();
        await Bounded(h.Vm.AutoSaveAsync());           // abandoned, kept locally
        var rowId = Assert.Single(h.Outbox.Enqueued).Id;
        Assert.Contains(rowId, h.Outbox.Held);

        stuck.SetResult("late");                       // nothing typed since it started
        await Bounded(h.Vm.AbandonedDraftSave);

        Assert.Equal([rowId], h.Outbox.Removed);
        Assert.DoesNotContain(rowId, h.Outbox.Held);
        Assert.False(h.Vm.IsDirty);

        h.Vm.Body = "Friday at noon?";
        await Bounded(h.Vm.AutoSaveAsync());
        Assert.Equal("late", h.Mail.ReplaceIds[^1]);
    }

    [Fact]
    public async Task AbandonedSave_FinishingLateAndBehind_LeavesTheComposeUnsavedSoTheNextSaveReplacesIt()
    {
        var h = new Harness();
        var stuck = h.Mail.NextHangs();
        var tick = h.Vm.AutoSaveAsync();
        h.Vm.Body = "Friday at noon?";                 // typed while the save hung
        await Bounded(tick);                           // abandoned; the local copy has the edit
        var rowId = Assert.Single(h.Outbox.Enqueued).Id;
        Assert.False(h.Vm.IsDirty);

        stuck.SetResult("late");                       // the server copy predates the edit
        await Bounded(h.Vm.AbandonedDraftSave);

        Assert.True(h.Vm.IsDirty);
        Assert.Empty(h.Outbox.Removed);

        await Bounded(h.Vm.AutoSaveAsync());
        Assert.Equal("late", h.Mail.ReplaceIds[^1]);
        Assert.Equal([rowId], h.Outbox.Removed);
        Assert.False(h.Vm.IsDirty);
    }

    [Fact]
    public async Task AbandonedSave_FinishingWhileTheDraftIsBeingKeptLocally_LeavesTheComposeUnsaved()
    {
        // The late id is adopted while the Outbox write is in flight, so the row being written still
        // replaces the older id. Left marked saved, closing would let that row upload a second copy.
        var h = new Harness();
        var stuck = h.Mail.NextHangs();
        h.Outbox.DuringEnqueue = () => stuck.TrySetResult("late");

        await Bounded(h.Vm.AutoSaveAsync());
        await Bounded(h.Vm.AbandonedDraftSave);
        h.Outbox.DuringEnqueue = null;

        var rowId = Assert.Single(h.Outbox.Enqueued).Id;
        Assert.True(h.Vm.IsDirty);

        await Bounded(h.Vm.AutoSaveAsync());
        Assert.Equal("late", h.Mail.ReplaceIds[^1]);
        Assert.Equal([rowId], h.Outbox.Removed);
        Assert.False(h.Vm.IsDirty);
    }

    [Fact]
    public async Task AbandonedSave_FinishingAfterTheMessageWasSent_RemovesTheLateDraft()
    {
        var h = new Harness();
        var stuck = h.Mail.NextHangs();
        await Bounded(h.Vm.AutoSaveAsync());           // abandoned
        await Bounded(h.Vm.SendCommand.ExecuteAsync(null));
        Assert.True(h.Vm.IsSent);

        stuck.SetResult("late");
        await Bounded(h.Vm.AbandonedDraftSave);

        Assert.Contains(("Drafts", "late"), h.Mail.Trashed);
    }

    [Fact]
    public async Task AbandonedSave_FinishingAfterANewerSave_IsNotRemoved()
    {
        // Only a draft of a sent message is deleted; a late copy that a newer save overtook might be
        // the only one with some edit in it, so it is left alone.
        var h = new Harness();
        var stuck = h.Mail.NextHangs();
        await Bounded(h.Vm.AutoSaveAsync());
        h.Vm.Body = "Friday at noon?";
        await Bounded(h.Vm.AutoSaveAsync());

        stuck.SetResult("late");
        await Bounded(h.Vm.AbandonedDraftSave);

        Assert.Empty(h.Mail.Trashed);
    }

    [Fact]
    public async Task AutoSave_ThatCompletesInTime_IsNotAbandoned()
    {
        var h = new Harness();
        // Generous here: this test is about the ordinary path, and a loaded runner must not turn a
        // 20 ms save into an abandoned one.
        h.Vm.DraftSaveTimeout = TimeSpan.FromSeconds(30);
        h.Mail.Next(async ct => { await Task.Delay(20, ct); return "quick"; });

        await Bounded(h.Vm.AutoSaveAsync());

        Assert.StartsWith("Auto-saved", h.Vm.AutoSaveText, StringComparison.Ordinal);
        Assert.Empty(h.Outbox.Enqueued);
        Assert.True(h.Vm.AbandonedDraftSave.IsCompletedSuccessfully);
    }
}
