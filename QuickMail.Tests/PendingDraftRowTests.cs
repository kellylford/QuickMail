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

    // ── A draft the server refused ───────────────────────────────────────────

    [Fact]
    public void ARefusedDraft_DoesNotPromiseAnUploadThatWillNeverHappen()
    {
        // LoadPendingDraftsAsync excludes a row with a failure reason, so nothing will take this
        // draft to the server until the user edits and saves it again. "Not on server" carries
        // exactly that promise, which is why the row must stop saying it (#637).
        var row = Draft(pending: true);
        row.SendFailedReason = "mailbox does not exist";

        Assert.Equal("not uploaded", row.LocationLabel);
        Assert.Equal("Not uploaded", row.StatusDisplay);
        Assert.Equal("could not be uploaded, still on this computer", row.ReadStatusLabel);
    }

    [Fact]
    public void ADraftStillWaiting_SaysSo()
    {
        var row = Draft(pending: true);

        Assert.Equal("not on server", row.LocationLabel);
        Assert.Equal("Not on server", row.StatusDisplay);
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
        row.SendFailedReason = "Your mail server refused it: mailbox does not exist Edit the draft and save it again to try once more.";

        // Readable later, from the draft itself, without having been watching the status bar at
        // the moment the sweep produced it.
        Assert.Contains("mailbox does not exist", row.DeliveryNotice, StringComparison.Ordinal);
        Assert.Contains("save it again", row.DeliveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalFailure_IsNotBlamedOnTheServer()
    {
        // The same column carries both, and prefixing every reason with "your mail server refused
        // to save this draft" attributed a LOCAL failure to the server — then told the user to fix
        // and re-save a draft that cannot be opened at all (#637).
        var row = Draft(pending: true);
        row.SendFailedReason = "Its saved copy on this computer could not be read, so there was nothing to upload.";

        Assert.DoesNotContain("mail server", row.DeliveryNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be read", row.DeliveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedDraft_KeepsAWordInTheRow()
    {
        // Binding the row field to a plain "is it waiting?" bool made a refused draft say NOTHING —
        // indistinguishable from one that uploaded fine, on the one channel that reaches a user
        // running with custom announcements off. Two states, two words (#637).
        var waiting = Draft(pending: true);
        Assert.Equal("not on server", waiting.LocationLabel);

        var refused = Draft(pending: true);
        refused.SendFailedReason = "Your mail server refused it: over quota.";
        Assert.Equal("not uploaded", refused.LocationLabel);

        Assert.Equal(string.Empty, new MailMessageSummary { FolderName = "INBOX" }.LocationLabel);
    }

}
