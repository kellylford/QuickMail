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
}
