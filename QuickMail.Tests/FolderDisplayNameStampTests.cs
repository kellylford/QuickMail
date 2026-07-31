using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #423: locks the VM-side folder-name composition for aggregate views — account-qualified when the
/// view spans more than one account, folder-only otherwise, plus the two miss fallbacks. The
/// spans-multiple-accounts decision itself is view-based (per-account All Mail vs global All Mail vs
/// saved views) and lives in the VM; here we pin the pure formatting given that decision.
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

        MainViewModel.StampFolderDisplayNames(list, qualifyAccount: true, Labels, Folders);

        Assert.Equal("kelly -- Inbox", a.FolderDisplayName);
        Assert.Equal("tim -- Inbox", b.FolderDisplayName);
    }

    [Fact]
    public void SingleAccountView_AnnouncesFolderOnly()
    {
        // qualifyAccount false (the view implies the account) → the account is not prefixed even though
        // a label is available for it.
        var a1 = Msg(A, "AAMk-inbox-a");
        var list = new List<MailMessageSummary> { a1 };

        MainViewModel.StampFolderDisplayNames(list, qualifyAccount: false, Labels, Folders);

        Assert.Equal("Inbox", a1.FolderDisplayName);
    }

    [Fact]
    public void UnknownFolder_LeavesExistingValue_NeverARawId()
    {
        // Folder not in the cache → leave whatever's there (a caller's plain-name fallback here),
        // never overwrite with the raw id and never announce it.
        var m = Msg(A, "AAMk-unknown-folder");
        m.FolderDisplayName = "Newsletters"; // caller-supplied fallback (e.g. saved-view stored name)
        var list = new List<MailMessageSummary> { m };

        MainViewModel.StampFolderDisplayNames(list, qualifyAccount: true, Labels, Folders);

        Assert.Equal("Newsletters", m.FolderDisplayName);            // fallback preserved
        Assert.DoesNotContain("AAMk", m.FolderDisplayName);          // never the raw id
    }

    [Fact]
    public void MissingAccountLabel_FallsBackToFolderAlone()
    {
        // Qualifying, but one account has no label → folder alone, never a dangling "account -- ".
        var a = Msg(A, "AAMk-inbox-a");
        var b = Msg(B, "AAMk-inbox-b");
        var labels = new Dictionary<Guid, string> { [A] = "kelly" }; // B has no label
        var list = new List<MailMessageSummary> { a, b };

        MainViewModel.StampFolderDisplayNames(list, qualifyAccount: true, labels, Folders);

        Assert.Equal("kelly -- Inbox", a.FolderDisplayName);
        Assert.Equal("Inbox", b.FolderDisplayName);                  // no "-- " with an empty label
    }
}
