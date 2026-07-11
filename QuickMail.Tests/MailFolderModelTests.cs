using System.Linq;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="MailFolderModel.SuppressUnreadCount"/> and the account-total exclusion it
/// drives (issue #227): Gmail's virtual folders (All Mail / Important / Starred) don't contribute a
/// misleading, overlapping unread count.
/// </summary>
public class MailFolderModelTests
{
    [Theory]
    [InlineData(SpecialFolderKind.AllMail, true)]
    [InlineData(SpecialFolderKind.Important, true)]
    [InlineData(SpecialFolderKind.Starred, true)]
    [InlineData(SpecialFolderKind.Inbox, false)]
    [InlineData(SpecialFolderKind.None, false)]
    [InlineData(SpecialFolderKind.Junk, false)]
    [InlineData(SpecialFolderKind.Trash, false)]
    [InlineData(SpecialFolderKind.Sent, false)]
    [InlineData(SpecialFolderKind.Drafts, false)]
    public void SuppressUnreadCount_TrueOnlyForGmailVirtualFolders(SpecialFolderKind kind, bool expected)
    {
        Assert.Equal(expected, new MailFolderModel { Kind = kind }.SuppressUnreadCount);
    }

    [Fact]
    public void AccountTotal_ExcludesSuppressedFolders()
    {
        // Mirrors ApplyAccountStatus / ApplyFolderCounts: the virtual folders don't add to the total,
        // so it isn't inflated by All Mail's superset or the Important/Inbox overlap.
        var folders = new[]
        {
            new MailFolderModel { Kind = SpecialFolderKind.Inbox,     UnreadCount = 5 },
            new MailFolderModel { Kind = SpecialFolderKind.AllMail,   UnreadCount = 23 },
            new MailFolderModel { Kind = SpecialFolderKind.Important, UnreadCount = 4 },
            new MailFolderModel { Kind = SpecialFolderKind.Junk,      UnreadCount = 6 },
        };

        var total = folders.Where(f => !f.SuppressUnreadCount).Sum(f => f.UnreadCount);

        Assert.Equal(11, total); // Inbox 5 + Junk 6; All Mail 23 and Important 4 excluded.
    }
}
