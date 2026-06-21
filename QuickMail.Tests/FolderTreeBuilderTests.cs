using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class FolderTreeBuilderTests
{
    private static MailFolderModel F(
        string fullName, string display, string? parentId = null,
        SpecialFolderKind kind = SpecialFolderKind.None)
        => new() { FullName = fullName, DisplayName = display, ParentId = parentId, Kind = kind };

    [Fact]
    public void Build_Imap_NestsBySeparator()
    {
        // IMAP encodes hierarchy in the separator-delimited FullName; ParentId is null throughout.
        var flat = new List<MailFolderModel>
        {
            F("INBOX", "INBOX"),
            F("INBOX/Projects", "Projects"),
            F("INBOX/Projects/2026", "2026"),
        };

        var roots = FolderTreeBuilder.Build(flat);

        var inbox = Assert.Single(roots);
        Assert.Equal("INBOX", inbox.Label);
        var projects = Assert.Single(inbox.Children);
        Assert.Equal("Projects", projects.Label);
        var year = Assert.Single(projects.Children);
        Assert.Equal("2026", year.Label);
    }

    [Fact]
    public void Build_Graph_NestsByParentId()
    {
        // Graph: opaque ids, hierarchy via ParentId; top-level folders point at a parent ("root")
        // that is not part of the fetched set, so they surface as tree roots.
        var flat = new List<MailFolderModel>
        {
            F("f1", "Inbox",    parentId: "root", kind: SpecialFolderKind.Inbox),
            F("c1", "Projects", parentId: "f1"),
            F("g1", "2026",     parentId: "c1"),
            F("f2", "Archive",  parentId: "root"),
        };

        var roots = FolderTreeBuilder.Build(flat);

        // Two roots, Inbox first (by kind), then Archive (alphabetical).
        Assert.Equal(2, roots.Count);
        Assert.Equal("Inbox", roots[0].Folder!.DisplayName);
        Assert.Equal("Archive", roots[1].Folder!.DisplayName);

        // Inbox > Projects > 2026 nests three deep.
        var projects = Assert.Single(roots[0].Children);
        Assert.Equal("Projects", projects.Folder!.DisplayName);
        var year = Assert.Single(projects.Children);
        Assert.Equal("2026", year.Folder!.DisplayName);
    }

    [Fact]
    public void Build_Graph_WrapsRootsUnderAccountHeader_WithoutFlattening()
    {
        var account = new AccountModel { AccountName = "Work", Username = "w@x.com" };
        var flat = new List<MailFolderModel>
        {
            F("f1", "Inbox", parentId: "root", kind: SpecialFolderKind.Inbox),
            F("c1", "Sub",   parentId: "f1"),
        };

        var roots = FolderTreeBuilder.Build(flat, account);

        var header = Assert.Single(roots);
        Assert.True(header.IsHeader);
        Assert.Equal("Work", header.Label);

        var inbox = Assert.Single(header.Children);
        Assert.Equal("Inbox", inbox.Folder!.DisplayName);
        // The sub-folder nests under Inbox rather than being flattened to a sibling.
        var sub = Assert.Single(inbox.Children);
        Assert.Equal("Sub", sub.Folder!.DisplayName);
    }
}
