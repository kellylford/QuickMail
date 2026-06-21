using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Verifies the folder picker shows readable paths for both backends — and never the raw Graph
/// folder id, which is what <see cref="MailFolderModel.FullName"/> holds for Graph accounts.
/// </summary>
public class FolderPickerPathTests
{
    private static MailFolderModel F(
        string fullName, string display, string? parentId = null)
        => new() { FullName = fullName, DisplayName = display, ParentId = parentId };

    [Fact]
    public void BuildFolderPath_Imap_UsesSeparatorFullName()
    {
        var folder = F("INBOX/Projects/2026", "2026"); // ParentId null → IMAP
        // IMAP exits before consulting byId, so an empty map makes that independence explicit.
        var byId = new Dictionary<string, MailFolderModel>();

        Assert.Equal("INBOX/Projects/2026", FolderPickerWindow.BuildFolderPath(folder, byId));
    }

    [Fact]
    public void BuildFolderPath_Graph_BuildsDisplayNamePathFromParentChain()
    {
        // Opaque Graph ids; the path must be reconstructed from DisplayNames, not the ids.
        var inbox    = F("AAA", "Inbox",    parentId: "root");
        var projects = F("BBB", "Projects", parentId: "AAA");
        var year     = F("CCC", "2026",     parentId: "BBB");
        var byId = new Dictionary<string, MailFolderModel>
        {
            [inbox.FullName] = inbox, [projects.FullName] = projects, [year.FullName] = year,
        };

        Assert.Equal("Inbox/Projects/2026", FolderPickerWindow.BuildFolderPath(year, byId));
        Assert.Equal("Inbox/Projects", FolderPickerWindow.BuildFolderPath(projects, byId));
        // A top-level Graph folder whose parent ("root") isn't in the set is just its own name.
        Assert.Equal("Inbox", FolderPickerWindow.BuildFolderPath(inbox, byId));
        // The raw id never appears.
        Assert.DoesNotContain("CCC", FolderPickerWindow.BuildFolderPath(year, byId));
    }
}
