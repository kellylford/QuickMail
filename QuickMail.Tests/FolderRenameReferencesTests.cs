// Rename Folder (#1083): the pure half. How a stored folder reference is carried across a rename
// (FolderRename.Map), the rewrite of every setting that names a folder (FolderRenameReferences.Apply),
// and the checks on a proposed name (FolderRenameReferences.ValidateName).

using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class FolderRenameReferencesTests
{
    private static readonly Guid Acct  = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static FolderRename Imap(string from = "Projects", string to = "Work", char sep = '/') =>
        new(Acct, from, to, sep, from.Split(sep)[^1], to.Split(sep)[^1]);

    // An id-named folder (Microsoft 365, POP3): the full name stays, only the shown name changes.
    private static FolderRename ById(string id = "AQMkAD-id", string from = "Projects", string to = "Work") =>
        new(Acct, id, id, null, from, to);

    private static MailFolderModel F(string fullName, string display, string? parentId = null,
                                     SpecialFolderKind kind = SpecialFolderKind.None, Guid? account = null) =>
        new() { AccountId = account ?? Acct, FullName = fullName, DisplayName = display, ParentId = parentId, Kind = kind };

    // ── FolderRename.Map ─────────────────────────────────────────────────────

    [Fact]
    public void Map_TheFolderItself()
        => Assert.Equal("Work", Imap().Map("Projects"));

    [Fact]
    public void Map_FoldersInsideIt_KeepTheRestOfTheirPath()
    {
        var r = Imap();
        Assert.Equal("Work/2026", r.Map("Projects/2026"));
        Assert.Equal("Work/2026/Q1", r.Map("Projects/2026/Q1"));
    }

    [Fact]
    public void Map_ANestedFolder_KeepsItsParent()
    {
        var r = Imap("INBOX/Projects", "INBOX/Work");
        Assert.Equal("INBOX/Work", r.Map("INBOX/Projects"));
        Assert.Equal("INBOX/Work/2026", r.Map("INBOX/Projects/2026"));
        Assert.Null(r.Map("INBOX"));
    }

    [Theory]
    [InlineData("Projects old")]   // a sibling that only starts the same way
    [InlineData("Projects.old")]   // the other separator is just a character here
    [InlineData("ProjectsX/2026")]
    [InlineData("Other")]
    [InlineData("projects")]       // case matters: on IMAP that can be another folder
    [InlineData("")]
    [InlineData(null)]
    public void Map_LeavesEverythingElseAlone(string? reference)
        => Assert.Null(Imap().Map(reference));

    [Fact]
    public void Map_WithADotSeparator()
    {
        var r = Imap("INBOX.Projects", "INBOX.Work", '.');
        Assert.Equal("INBOX.Work.2026", r.Map("INBOX.Projects.2026"));
        Assert.Null(r.Map("INBOX.Projects/2026"));
    }

    [Fact]
    public void Map_WhereFoldersAreNamedById_ChangesNothing()
    {
        var r = ById();
        Assert.False(r.NamesChange);
        Assert.Null(r.Map("AQMkAD-id"));
        Assert.True(r.IsTheFolder("AQMkAD-id"));
    }

    // ── Apply: rules ─────────────────────────────────────────────────────────

    [Fact]
    public void Rules_MoveAndCopyTargets_AreRewritten_ForThisAccountOnly()
    {
        var move  = new MailRule { Name = "Move", AccountId = Acct, Action = RuleAction.MoveToFolder, TargetFolder = "Projects" };
        var copy  = new MailRule { Name = "Copy", AccountId = Acct, Action = RuleAction.CopyToFolder, CopyTargetFolder = "Projects/2026" };
        var both  = new MailRule { Name = "Both", AccountId = Acct, Actions = [RuleAction.CopyToFolder, RuleAction.MoveToFolder],
                                   TargetFolder = "Projects/2026", CopyTargetFolder = "Projects" };
        var other = new MailRule { Name = "Other account", AccountId = Other, Action = RuleAction.MoveToFolder, TargetFolder = "Projects" };
        var none  = new MailRule { Name = "Elsewhere", AccountId = Acct, Action = RuleAction.MoveToFolder, TargetFolder = "Archive" };

        var report = FolderRenameReferences.Apply(Imap(), [move, copy, both, other, none], [], new ConfigModel(), null);

        Assert.Equal("Work", move.TargetFolder);
        Assert.Equal("Work/2026", copy.CopyTargetFolder);
        Assert.Equal("Work/2026", both.TargetFolder);
        Assert.Equal("Work", both.CopyTargetFolder);
        Assert.Equal("Projects", other.TargetFolder);
        Assert.Equal("Archive", none.TargetFolder);
        Assert.Equal(["Move", "Copy", "Both"], report.UpdatedRules);
    }

    [Fact]
    public void Rules_ATargetKeptWhileTheActionIsOff_IsRewrittenToo()
    {
        // Turning Move back on later must still find the folder.
        var rule = new MailRule { Name = "Off", AccountId = Acct, Action = RuleAction.MarkAsRead, TargetFolder = "Projects" };
        FolderRenameReferences.Apply(Imap(), [rule], [], new ConfigModel(), null);
        Assert.Equal("Work", rule.TargetFolder);
    }

    [Fact]
    public void Rules_Unreadable_TheRestIsStillRewritten()
    {
        var cfg = new ConfigModel { StartupFolder = "Projects", StartupFolderAccount = Acct.ToString() };
        var report = FolderRenameReferences.Apply(Imap(), null, [], cfg, null);
        Assert.Empty(report.UpdatedRules);
        Assert.Equal("Work", cfg.StartupFolder);
    }

    [Fact]
    public void Rules_ById_AreLeftAlone()
    {
        var rule = new MailRule { Name = "Graph", AccountId = Acct, Action = RuleAction.MoveToFolder, TargetFolder = "AQMkAD-id" };
        var report = FolderRenameReferences.Apply(ById(), [rule], [], new ConfigModel(), null);
        Assert.Equal("AQMkAD-id", rule.TargetFolder);
        Assert.Empty(report.UpdatedRules);
        Assert.Equal(string.Empty, report.Summary());
    }

    // ── Apply: saved views ───────────────────────────────────────────────────

    [Fact]
    public void Views_FolderReferences_AndTheRenamedFoldersOwnName_AreRewritten()
    {
        var view = new SavedView
        {
            Name = "Projects view",
            Folders =
            [
                new ViewFolder { AccountId = Acct,  FolderFullName = "Projects",      FolderDisplayName = "Projects" },
                new ViewFolder { AccountId = Acct,  FolderFullName = "Projects/2026", FolderDisplayName = "2026" },
                new ViewFolder { AccountId = Other, FolderFullName = "Projects",      FolderDisplayName = "Projects" },
            ],
        };
        var untouched = new SavedView { Name = "Inbox only", Folders = [new ViewFolder { AccountId = Acct, FolderFullName = "INBOX" }] };
        var virtualView = new SavedView { Name = "All", VirtualFolderKey = "AllMail" };

        var report = FolderRenameReferences.Apply(Imap(), [], [view, untouched, virtualView], new ConfigModel(), null);

        Assert.Equal(("Work", "Work"), (view.Folders[0].FolderFullName, view.Folders[0].FolderDisplayName));
        Assert.Equal(("Work/2026", "2026"), (view.Folders[1].FolderFullName, view.Folders[1].FolderDisplayName));
        Assert.Equal(("Projects", "Projects"), (view.Folders[2].FolderFullName, view.Folders[2].FolderDisplayName));
        Assert.Equal(["Projects view"], report.UpdatedViews);
        Assert.True(report.ViewsChanged);
    }

    [Fact]
    public void Views_ById_OnlyTheShownNameChanges_AndItIsNotNews()
    {
        var view = new SavedView { Name = "V", Folders = [new ViewFolder { AccountId = Acct, FolderFullName = "AQMkAD-id", FolderDisplayName = "Projects" }] };
        var report = FolderRenameReferences.Apply(ById(), [], [view], new ConfigModel(), null);
        Assert.Equal("AQMkAD-id", view.Folders[0].FolderFullName);
        Assert.Equal("Work", view.Folders[0].FolderDisplayName);
        Assert.True(report.ViewsChanged);           // saved
        Assert.Empty(report.UpdatedViews);          // but not announced
    }

    // ── Apply: config.ini ────────────────────────────────────────────────────

    [Fact]
    public void StartupFolder_TheFolder_TakesItsNewNameAndLabel()
    {
        var cfg = new ConfigModel { StartupFolder = "Projects", StartupFolderAccount = Acct.ToString(), StartupFolderLabel = "Projects" };
        var report = FolderRenameReferences.Apply(Imap(), [], [], cfg, null);
        Assert.Equal(("Work", "Work"), (cfg.StartupFolder, cfg.StartupFolderLabel));
        Assert.True(report.StartupFolderUpdated);
        Assert.True(report.ConfigChanged);
    }

    [Fact]
    public void StartupFolder_InsideIt_KeepsItsOwnLabel()
    {
        var cfg = new ConfigModel { StartupFolder = "Projects/2026", StartupFolderAccount = Acct.ToString(), StartupFolderLabel = "2026" };
        FolderRenameReferences.Apply(Imap(), [], [], cfg, null);
        Assert.Equal(("Work/2026", "2026"), (cfg.StartupFolder, cfg.StartupFolderLabel));
    }

    [Fact]
    public void StartupFolder_OnAnotherAccount_OrAVirtualKey_IsLeftAlone()
    {
        var other = new ConfigModel { StartupFolder = "Projects", StartupFolderAccount = Other.ToString() };
        var aggregate = new ConfigModel { StartupFolder = "AllInboxes", StartupFolderAccount = string.Empty };
        Assert.False(FolderRenameReferences.Apply(Imap(), [], [], other, null).ConfigChanged);
        Assert.False(FolderRenameReferences.Apply(Imap(), [], [], aggregate, null).ConfigChanged);
        Assert.Equal("Projects", other.StartupFolder);
    }

    [Fact]
    public void RememberedDestinations_AreRewritten()
    {
        var cfg = new ConfigModel();
        cfg.Accounts[Acct]  = new AccountOverrideConfig { LastMoveFolder = "Projects/2026", LastCopyFolder = "Projects" };
        cfg.Accounts[Other] = new AccountOverrideConfig { LastMoveFolder = "Projects" };

        var report = FolderRenameReferences.Apply(Imap(), [], [], cfg, null);

        Assert.Equal("Work/2026", cfg.Accounts[Acct].LastMoveFolder);
        Assert.Equal("Work", cfg.Accounts[Acct].LastCopyFolder);
        Assert.Equal("Projects", cfg.Accounts[Other].LastMoveFolder);
        Assert.True(report.ConfigChanged);
        Assert.False(report.StartupFolderUpdated);
    }

    // ── Apply: the account ───────────────────────────────────────────────────

    [Fact]
    public void Account_HiddenFavoritesAndArchive_FollowTheRename()
    {
        var added = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        var account = new AccountModel
        {
            Id = Acct,
            HiddenFolders = ["Projects/2026", "Notes"],
            FavoriteFolders = [new FavoriteFolder { FullName = "Projects", AddedUtc = added, Order = 3 }],
            ArchiveFolderFullName = "Projects/Archive",
        };

        var report = FolderRenameReferences.Apply(Imap(), [], [], new ConfigModel(), account);

        Assert.Equal(["Work/2026", "Notes"], account.HiddenFolders);
        var favorite = Assert.Single(account.FavoriteFolders);
        Assert.Equal(("Work", added, (int?)3), (favorite.FullName, favorite.AddedUtc, favorite.Order));
        Assert.Equal("Work/Archive", account.ArchiveFolderFullName);
        Assert.True(report.AccountChanged);
        // Following along on the account is not news worth saying.
        Assert.Equal(string.Empty, report.Summary());
    }

    [Fact]
    public void Account_AnotherAccount_IsLeftAlone()
    {
        var account = new AccountModel { Id = Other, HiddenFolders = ["Projects"] };
        Assert.False(FolderRenameReferences.Apply(Imap(), [], [], new ConfigModel(), account).AccountChanged);
        Assert.Equal(["Projects"], account.HiddenFolders);
    }

    // ── Summary ──────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_CountsWhatWouldHaveBroken()
    {
        var report = new FolderRenameReferences.Report();
        report.UpdatedRules.AddRange(["a", "b"]);
        report.UpdatedViews.Add("v");
        report.StartupFolderUpdated = true;
        Assert.Equal("2 rules, 1 saved view and the startup folder updated.", report.Summary());

        var one = new FolderRenameReferences.Report();
        one.UpdatedRules.Add("a");
        Assert.Equal("1 rule updated.", one.Summary());
    }

    // ── ValidateName ─────────────────────────────────────────────────────────

    private static List<MailFolderModel> ImapFolders() =>
    [
        F("INBOX", "Inbox", kind: SpecialFolderKind.Inbox),
        F("Projects", "Projects"),
        F("Projects/2026", "2026"),
        F("Lists/Work", "Work"),        // "Lists" is only a path segment
        F("Notes", "Notes"),
    ];

    private static (string? Name, string? Error) Check(string folder, string? proposed, BackendKind backend = BackendKind.ImapSmtp,
                                                        List<MailFolderModel>? folders = null)
    {
        folders ??= ImapFolders();
        return FolderRenameReferences.ValidateName(folders.Single(f => f.FullName == folder), proposed, folders, backend);
    }

    [Fact]
    public void Validate_TrimsAndAccepts()
        => Assert.Equal(("Work items", (string?)null), Check("Projects", "  Work items  "));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_Empty(string? proposed)
        => Assert.Equal("Type a name for the folder.", Check("Projects", proposed).Error);

    [Fact]
    public void Validate_Unchanged()
        => Assert.Equal("Projects already has that name.", Check("Projects", "Projects").Error);

    [Fact]
    public void Validate_ACaseChange_IsAllowed()
        => Assert.Equal("projects", Check("Projects", "projects").Name);

    [Theory]
    [InlineData("A/B", "/")]
    [InlineData("A*", "*")]
    [InlineData("A%", "%")]
    public void Validate_ImapRefusesTheSeparatorAndWildcards(string proposed, string character)
        => Assert.Equal($"A folder name on this account cannot contain {character}.", Check("Projects", proposed).Error);

    [Fact]
    public void Validate_ADotIsFine_WhereTheSeparatorIsASlash()
        => Assert.Equal("v1.2", Check("Projects", "v1.2").Name);

    [Fact]
    public void Validate_ADotIsRefused_WhereTheSeparatorIsADot()
    {
        List<MailFolderModel> folders = [F("INBOX", "Inbox", kind: SpecialFolderKind.Inbox), F("INBOX.Projects", "Projects")];
        Assert.Equal("A folder name on this account cannot contain ..", Check("INBOX.Projects", "v1.2", folders: folders).Error);
    }

    [Fact]
    public void Validate_ControlCharacters()
        => Assert.Equal("A folder name cannot contain control characters.", Check("Projects", "A\tB").Error);

    [Fact]
    public void Validate_TooLong()
        => Assert.Equal("A folder name can be at most 255 characters.", Check("Projects", new string('a', 256)).Error);

    [Theory]
    [InlineData("Notes")]
    [InlineData("NOTES")]   // case does not make it another folder
    [InlineData("inbox")]   // Inbox's path is INBOX
    [InlineData("Lists")]   // a path segment holding other folders
    public void Validate_ANameAlreadyTakenBesideIt(string proposed)
        => Assert.Equal($"There is already a folder named {proposed} there.", Check("Projects", proposed).Error);

    [Fact]
    public void Validate_ANameTakenElsewhere_IsFine()
        // "Work" exists, but inside Lists, not beside Projects.
        => Assert.Equal("Work", Check("Projects", "Work").Name);

    [Fact]
    public void Validate_Nested_ComparesWithItsOwnSiblings()
    {
        List<MailFolderModel> folders = [.. ImapFolders(), F("Projects/2027", "2027")];
        Assert.Equal("There is already a folder named 2027 there.", Check("Projects/2026", "2027", folders: folders).Error);
        Assert.Equal("Notes", Check("Projects/2026", "Notes", folders: folders).Name);
    }

    [Fact]
    public void Validate_ByParentId_SiblingsShareAParent()
    {
        List<MailFolderModel> folders =
        [
            F("inbox-id", "Inbox", parentId: "root", kind: SpecialFolderKind.Inbox),
            F("p-id", "Projects", parentId: "root"),
            F("n-id", "Notes", parentId: "root"),
            F("c-id", "Child", parentId: "p-id"),
        ];
        Assert.Equal("There is already a folder named notes there.", Check("p-id", "notes", BackendKind.MicrosoftGraph, folders).Error);
        Assert.Equal("Child", Check("p-id", "Child", BackendKind.MicrosoftGraph, folders).Name);
        // Microsoft 365 takes a slash in a name; the path of an id-named folder is not built from it.
        Assert.Equal("A/B", Check("p-id", "A/B", BackendKind.MicrosoftGraph, folders).Name);
    }

    [Fact]
    public void Validate_Pop3_RefusesASlash_AndTheBuiltInNames()
    {
        List<MailFolderModel> folders =
        [
            F("Inbox", "Inbox", kind: SpecialFolderKind.Inbox),
            F("Sent", "Sent", kind: SpecialFolderKind.Sent),
            F("local:1", "Lists"),
        ];
        Assert.Equal("A folder name on this account cannot contain /.", Check("local:1", "A/B", BackendKind.Pop3Smtp, folders).Error);
        Assert.Equal("There is already a folder named sent there.", Check("local:1", "sent", BackendKind.Pop3Smtp, folders).Error);
    }

    // ── ImapSeparator ────────────────────────────────────────────────────────

    [Fact]
    public void Separator_FromTheFoldersOwnPath()
    {
        Assert.Equal('.', FolderRenameReferences.ImapSeparator(F("INBOX.Lists", "Lists"), []));
        Assert.Equal('/', FolderRenameReferences.ImapSeparator(F("A/B", "B"), []));
    }

    [Fact]
    public void Separator_TheServersOwnAnswer_BeatsAGuessFromTheNames()
    {
        // A '/' server with no subfolders and a top-level "Projects.old": guessed from the names, '.'
        // would make "Projects.old" look like a folder inside "Projects" and be renamed with it.
        var projects = F("Projects", "Projects");
        projects.HierarchySeparator = '/';
        List<MailFolderModel> folders = [F("INBOX", "Inbox", kind: SpecialFolderKind.Inbox), projects, F("Projects.old", "Projects.old")];

        Assert.Equal('/', FolderRenameReferences.ImapSeparator(projects, folders));
        Assert.Equal("v1.2", FolderRenameReferences.ValidateName(projects, "v1.2", folders, BackendKind.ImapSmtp).Name);

        // Recorded on any of the account's folders is as good.
        var bare = F("Notes", "Notes");
        Assert.Equal('/', FolderRenameReferences.ImapSeparator(bare, [bare, projects]));
    }

    [Fact]
    public void Separator_ForATopLevelFolder_FromTheOthers()
    {
        Assert.Equal('/', FolderRenameReferences.ImapSeparator(F("Projects", "Projects"), ImapFolders()));
        Assert.Equal('.', FolderRenameReferences.ImapSeparator(F("Projects", "Projects"),
            [F("Projects", "Projects"), F("INBOX.Sent", "Sent")]));
    }
}
