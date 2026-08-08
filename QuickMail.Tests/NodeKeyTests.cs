using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pins the folder-tree node-key fix (#31, spec §5.4): a header node keyed only by its label collides
/// for two same-named accounts (their expansion state and selection get confused). Header nodes for
/// accounts (and shared mailboxes) now carry the account id, so their keys are distinct.
/// </summary>
public class NodeKeyTests
{
    private static FolderTreeNode Header(string label, Guid? accountId = null) =>
        new() { IsHeader = true, Label = label, AccountId = accountId };

    [Fact]
    public void SameNamedAccountHeaders_GetDistinctKeys()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.NotEqual(MainViewModel.NodeKey(Header("Work", a)), MainViewModel.NodeKey(Header("Work", b)));
    }

    [Fact]
    public void HeaderWithoutAccountId_StillKeysByLabel()
    {
        // Non-account group headers (All Mail, Views) have no account id and are unique by label.
        Assert.Equal(MainViewModel.NodeKey(Header("All Mail")), MainViewModel.NodeKey(Header("All Mail")));
    }

    [Fact]
    public void FolderNode_KeysByAccountAndFullName()
    {
        var acct = Guid.NewGuid();
        var node = new FolderTreeNode { Folder = new MailFolderModel { AccountId = acct, FullName = "INBOX" } };
        var key = MainViewModel.NodeKey(node);
        Assert.Contains(acct.ToString(), key);
        Assert.Contains("INBOX", key);
    }
}
