// How a draft that has not reached the server yet reads in the message list — issue #637.
//
// The user chose to have local drafts appear in Drafts alongside server drafts rather than in a
// folder of their own, which makes the row's wording the only thing distinguishing them. So the
// wording is behaviour, not decoration: a row that does not say the draft is still on this computer
// leaves the user believing a message is somewhere it is not.

using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

public class PendingDraftRowTests
{
    private static MailMessageSummary Draft(bool pending) => new()
    {
        MessageId  = pending ? "local-abc" : "42",
        FolderName = "Drafts",
        Subject    = "Airport thoughts",
        IsRead     = true,
        IsPendingUpload = pending,
    };

    [Fact]
    public void PendingDraft_SaysItIsNotOnTheServer()
        => Assert.Equal("Not on server", Draft(pending: true).StatusDisplay);

    [Fact]
    public void UploadedDraft_ReadsLikeAnyOtherReadMessage()
        => Assert.Equal(string.Empty, Draft(pending: false).StatusDisplay);

    /// <summary>
    /// Outranks the other statuses deliberately. Every one of them describes a message that IS on
    /// the server; this one says it is not, which is the more consequential fact about the row.
    /// </summary>
    [Fact]
    public void PendingOutranksFlagRepliedAndUnread()
    {
        var summary = Draft(pending: true);
        summary.FlagId = System.Guid.NewGuid().ToString();
        summary.FlagName = "Follow up";
        summary.IsReplied = true;
        summary.IsRead = false;

        Assert.Equal("Not on server", summary.StatusDisplay);
    }

    /// <summary>
    /// The spoken form is a phrase rather than the column's short label: it is read as part of the
    /// row, where "Not on server" alone would be ambiguous about what is not on the server.
    /// </summary>
    [Fact]
    public void SpokenStatus_ExplainsWhereTheDraftIs()
        => Assert.Equal("saved on this computer, not yet on the server",
                        Draft(pending: true).ReadStatusLabel);

    /// <summary>
    /// Clearing the flag has to refresh the row in place — the upload pass flips it on a summary the
    /// list is already showing, and a row that kept saying "Not on server" after the draft went up
    /// would be worse than never having said it.
    /// </summary>
    [Fact]
    public void ClearingPending_RaisesChangeNotificationForTheStatus()
    {
        var summary = Draft(pending: true);
        var changed = new System.Collections.Generic.List<string?>();
        summary.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        summary.IsPendingUpload = false;

        Assert.Contains(nameof(MailMessageSummary.StatusDisplay), changed);
        Assert.Contains(nameof(MailMessageSummary.ReadStatusLabel), changed);
    }
    // ── A draft the server refused ───────────────────────────────────────────

    [Fact]
    public void ARefusedDraft_DoesNotPromiseAnUploadThatWillNeverHappen()
    {
        // LoadPendingDraftsAsync excludes a row with a failure reason, so nothing will take this
        // draft to the server until the user edits and saves it again. "Not on server" carries
        // exactly that promise, which is why the row must stop saying it (#637).
        var row = Draft(pending: true);
        row.SendFailedReason = "mailbox does not exist";

        Assert.False(row.IsAwaitingUpload);
        Assert.Equal("Not uploaded", row.StatusDisplay);
        Assert.Equal("could not be uploaded, still on this computer", row.ReadStatusLabel);
    }

    [Fact]
    public void ADraftStillWaiting_SaysSo()
    {
        var row = Draft(pending: true);

        Assert.True(row.IsAwaitingUpload);
        Assert.Equal("Not on server", row.StatusDisplay);
    }

    [Fact]
    public void TheRowUpdatesWhenTheRefusalArrives()
    {
        // Bound by the "not on server" field, so a row refused under an open list has to raise it
        // rather than wait for a rebuild that would move the user's focus.
        var row = Draft(pending: true);
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        row.SendFailedReason = "mailbox does not exist";

        Assert.Contains(nameof(MailMessageSummary.IsAwaitingUpload), raised);
        Assert.Contains(nameof(MailMessageSummary.StatusDisplay), raised);
    }
    // ── The reason, on a channel that survives ───────────────────────────────

    [Fact]
    public void ADraftStillWaiting_HasNothingExtraToSay()
        // Chosen deliberately: the row already says "not on server", and repeating it here would
        // add a focus stop on the common case to say what the user has just been told.
        => Assert.Equal(string.Empty, Draft(pending: true).DeliveryNotice);

    [Fact]
    public void OrdinaryMailHasNoNotice()
        => Assert.Equal(string.Empty, new MailMessageSummary { FolderName = "INBOX" }.DeliveryNotice);

    [Fact]
    public void ARefusedDraft_CarriesTheServersOwnWords()
    {
        var row = Draft(pending: true);
        row.SendFailedReason = "mailbox does not exist";

        // The whole point: readable later, from the message itself, without having been watching
        // the status bar at the moment the sweep produced it.
        Assert.Contains("mailbox does not exist", row.DeliveryNotice, StringComparison.Ordinal);
        // And what to do about it, which is the thing the guide tells the user to do.
        Assert.Contains("edit it and save it", row.DeliveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoticeAppearsWhenTheRefusalDoes()
    {
        // Bound in the reading pane, so a row refused under an open message has to raise it —
        // otherwise the pane goes on showing nothing for a draft that has just stopped uploading.
        var row = Draft(pending: true);
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        row.SendFailedReason = "mailbox does not exist";

        Assert.Contains(nameof(MailMessageSummary.DeliveryNotice), raised);
    }
}
