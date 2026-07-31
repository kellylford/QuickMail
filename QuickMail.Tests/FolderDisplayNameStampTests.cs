using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #423: locks the VM-side folder-name composition for aggregate views — account-qualified only when
/// the view spans more than one account, folder-only otherwise, and the two miss fallbacks.
/// </summary>
public class FolderDisplayNameStampTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static MailMessageSummary Msg(Guid acct, string folderId) =>
        new() { MessageId = Guid.NewGuid().ToString(), AccountId = acct, FolderName = folderId };

    private static Dictionary<Guid, string> Labels => new() { [A] = "kelly", [B] = "tim" };
    private static Dictionary<(Guid, string), string> Folders => new()
    {
        [(A, "AAMk-inbox-a")] = "Inbox",
        [(B, "AAMk-inbox-b")] = "Inbox",
    };

    [Fact]
    public void MultiAccountView_QualifiesWithAccount()
    {
        var a = Msg(A, "AAMk-inbox-a");
        var b = Msg(B, "AAMk-inbox-b");
        var list = new List<MailMessageSummary> { a, b };

        MainViewModel.StampFolderDisplayNames(list, list, Labels, Folders);

        Assert.Equal("kelly -- Inbox", a.FolderDisplayName);
        Assert.Equal("tim -- Inbox", b.FolderDisplayName);
    }

    [Fact]
    public void SingleAccountView_AnnouncesFolderOnly()
    {
        // Only one account in the view → the account is implied, so no redundant prefix.
        var a1 = Msg(A, "AAMk-inbox-a");
        var list = new List<MailMessageSummary> { a1 };

        MainViewModel.StampFolderDisplayNames(list, list, Labels, Folders);

        Assert.Equal("Inbox", a1.FolderDisplayName);
    }

    [Fact]
    public void IncrementalBatch_UsesFullViewScope_NotJustTheBatch()
    {
        // A live arrival from one account into a two-account view must still qualify — the decision is
        // based on the whole view scope, not the single-account batch.
        var existingA = Msg(A, "AAMk-inbox-a");
        var existingB = Msg(B, "AAMk-inbox-b");
        var arrival   = Msg(A, "AAMk-inbox-a");
        var viewScope = new List<MailMessageSummary> { existingA, existingB };

        MainViewModel.StampFolderDisplayNames(new[] { arrival }, viewScope, Labels, Folders);

        Assert.Equal("kelly -- Inbox", arrival.FolderDisplayName);
    }

    [Fact]
    public void UnknownFolder_LeavesExistingValue_NeverARawId()
    {
        // Folder not in the cache → leave whatever's there (a caller's plain-name fallback here),
        // never overwrite with the raw id and never announce it.
        var m = Msg(A, "AAMk-unknown-folder");
        m.FolderDisplayName = "Newsletters"; // caller-supplied fallback (e.g. saved-view stored name)
        var list = new List<MailMessageSummary> { m, Msg(B, "AAMk-inbox-b") };

        MainViewModel.StampFolderDisplayNames(list, list, Labels, Folders);

        Assert.Equal("Newsletters", m.FolderDisplayName);            // fallback preserved
        Assert.DoesNotContain("AAMk", m.FolderDisplayName);          // never the raw id
    }

    [Fact]
    public void MissingAccountLabel_FallsBackToFolderAlone()
    {
        // Two accounts in view (so qualification is on), but one has no label → folder alone, never a
        // dangling "account -- ".
        var a = Msg(A, "AAMk-inbox-a");
        var b = Msg(B, "AAMk-inbox-b");
        var labels = new Dictionary<Guid, string> { [A] = "kelly" }; // B has no label
        var list = new List<MailMessageSummary> { a, b };

        MainViewModel.StampFolderDisplayNames(list, list, labels, Folders);

        Assert.Equal("kelly -- Inbox", a.FolderDisplayName);
        Assert.Equal("Inbox", b.FolderDisplayName);                  // no "-- " with an empty label
    }
}
