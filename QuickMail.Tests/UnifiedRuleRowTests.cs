using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

public class UnifiedRuleRowTests
{
    [Fact]
    public void ServerRow_ReadsFromServerRule_AndMarksOnServer()
    {
        var row = UnifiedRuleRow.ForServer(new ServerRuleModel
        {
            DisplayName = "Move digests",
            IsEnabled = true,
            SubjectContains = "digest",
            MoveToFolderId = "f1",
            MoveToFolderName = "Archive",
        });

        Assert.Equal(RuleRunsWhere.Server, row.RunsWhere);
        Assert.Equal("Move digests", row.Name);
        Assert.True(row.IsEnabled);
        Assert.Contains("on server", row.RowText);
        Assert.Contains("enabled", row.RowText);
        Assert.Contains("Archive", row.RowText);           // from the server summary
        Assert.Equal(row.RowText, row.ToString());          // screen reader reads ToString()
    }

    [Fact]
    public void ClientRow_ReadsFromMailRule_AndMarksInQuickMail()
    {
        var row = UnifiedRuleRow.ForClient(new MailRule
        {
            Name = "Keep unread",
            IsEnabled = false,
            UseSubjectCondition = true,
            SubjectContains = "later",
            Action = RuleAction.MarkAsUnread,
        });

        Assert.Equal(RuleRunsWhere.Client, row.RunsWhere);
        Assert.Equal("Keep unread", row.Name);
        Assert.False(row.IsEnabled);
        Assert.Contains("in QuickMail", row.RowText);
        Assert.Contains("disabled", row.RowText);
        Assert.Contains("subject contains 'later'", row.RowText);
        Assert.Contains("mark as unread", row.RowText);
    }

    [Fact]
    public void ClientRow_NoConditions_ReadsAllMessages()
    {
        var row = UnifiedRuleRow.ForClient(new MailRule
        {
            Name = "Catch-all",
            Action = RuleAction.MoveToFolder,
            TargetFolder = "INBOX/Sorted",
        });

        Assert.Contains("All messages", row.RowText);
        Assert.Contains("move to INBOX/Sorted", row.RowText);
    }
}
