using System.Collections.Generic;
using System.ComponentModel;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="FolderTreeNode"/>'s in-place unread-count refresh (issue #227): the folder
/// tree must reflect a new count without a rebuild, so keyboard focus in the tree is preserved.
/// </summary>
public class FolderTreeNodeTests
{
    private static FolderTreeNode NodeFor(int unread) => new()
    {
        Folder = new MailFolderModel { FullName = "INBOX", DisplayName = "Inbox", UnreadCount = unread },
        Label  = "Inbox",
    };

    [Fact]
    public void CountDisplays_ReflectUnderlyingUnreadCount()
    {
        var node = NodeFor(3);
        Assert.Equal("3 unread", node.ItemStatusLabel);
        Assert.Equal("(3)", node.UnreadDisplay);
    }

    [Fact]
    public void ZeroUnread_ProducesEmptyDisplays()
    {
        var node = NodeFor(0);
        Assert.Equal("", node.ItemStatusLabel);
        Assert.Equal("", node.UnreadDisplay);
    }

    [Fact]
    public void NotifyUnreadChanged_RaisesPropertyChangedForBothDisplays()
    {
        var node = NodeFor(5);
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Simulate the in-place update: mutate the model, then notify.
        node.Folder!.UnreadCount = 4;
        node.NotifyUnreadChanged();

        Assert.Contains(nameof(FolderTreeNode.ItemStatusLabel), changed);
        Assert.Contains(nameof(FolderTreeNode.UnreadDisplay), changed);
        // Displays now reflect the new count without any tree rebuild.
        Assert.Equal("4 unread", node.ItemStatusLabel);
        Assert.Equal("(4)", node.UnreadDisplay);
    }

    [Fact]
    public void NotifyUnreadChanged_DoesNotRaiseForLabelOrExpansion()
    {
        var node = NodeFor(2);
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.NotifyUnreadChanged();

        Assert.DoesNotContain(nameof(FolderTreeNode.IsExpanded), changed);
        Assert.DoesNotContain(nameof(FolderTreeNode.AutomationName), changed);
    }
}
