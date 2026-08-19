// Expanding and collapsing folders from a menu — issue #590.
//
// Reported as: "I was in the folder tree and wanted to collapse all folders. There was no option to
// do so." Right and Left arrow on a tree item were the only expansion controls in the app, so a
// deeply nested account could only be folded away one item at a time, and there was no entry point
// on the context menu, on a menu, or in the Command Palette.
//
// Four things are pinned here, because each fails silently on its own:
//   * the branch semantics — these commands are not a duplicate of the arrow keys,
//   * that Collapse All reaches account headers, which is the whole point of collapsing all,
//   * that a rebuild of the tree does not quietly undo it (it did: expansion state was restored
//     additively, so a collapsed account header sprang back open on the next folder refresh),
//   * and that all three entry points the issue asks for actually exist.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class FolderExpansionTests
{
    private static readonly Guid AccountA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // Inbox / Projects / 2026 — three levels, so "one level" and "the whole branch" are
    // distinguishable outcomes.
    private static FolderTreeNode Branch()
    {
        var leaf   = new FolderTreeNode { Label = "2026" };
        var middle = new FolderTreeNode { Label = "Projects" };
        var root   = new FolderTreeNode { Label = "Inbox" };
        middle.Children.Add(leaf);
        root.Children.Add(middle);
        return root;
    }

    private static IEnumerable<FolderTreeNode> Flatten(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    // ── Branch semantics ─────────────────────────────────────────────────────

    [Fact]
    public void ExpandingAFolder_OpensTheWholeBranch_NotJustOneLevel()
    {
        var root = Branch();

        MainViewModel.SetFolderBranchExpanded(root, true);

        Assert.All(Flatten([root]), n => Assert.True(n.IsExpanded, n.Label + " is still collapsed."));
    }

    [Fact]
    public void CollapsingAFolder_ClosesTheWholeBranch()
    {
        var root = Branch();
        MainViewModel.SetFolderBranchExpanded(root, true);

        MainViewModel.SetFolderBranchExpanded(root, false);

        Assert.All(Flatten([root]), n => Assert.False(n.IsExpanded, n.Label + " is still expanded."));
    }

    [Fact]
    public void ANullNodeIsIgnored()
    {
        // The context menu is attached to every tree item, so the handler can be reached with no
        // resolvable node. Throwing there would take the app down from a menu activation.
        MainViewModel.SetFolderBranchExpanded(null, true);
    }

    // ── Whole tree ───────────────────────────────────────────────────────────

    private static async Task<MainViewModel> MakeVmAsync()
    {
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [AccountA] =
            [
                new MailFolderModel { AccountId = AccountA, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox },
                new MailFolderModel { AccountId = AccountA, FullName = "INBOX/Projects", DisplayName = "Projects" },
                new MailFolderModel { AccountId = AccountA, FullName = "INBOX/Projects/2026", DisplayName = "2026" },
            ],
        };

        var vm = new MainViewModel(
            new FolderedMailService(folders, []), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

        vm.Accounts.Add(new AccountModel
        {
            Id = AccountA, AccountName = "Work", Username = "work@example.com",
            AuthType = AuthType.OAuth2Microsoft,
        });
        await vm.ConnectAllAccountsAsync();
        return vm;
    }

    [Fact]
    public async Task CollapseAll_ReachesAccountHeaders()
    {
        // The header is what has to close: with it left open, "collapse all folders" still leaves
        // the account's whole first level on screen, which is not what was asked for.
        var vm = await MakeVmAsync();
        Assert.Contains(Flatten(vm.FolderTree), n => n.IsHeader && n.IsExpanded);

        vm.SetAllFoldersExpanded(false);

        Assert.All(Flatten(vm.FolderTree), n => Assert.False(n.IsExpanded, n.Label + " is still expanded."));
    }

    [Fact]
    public async Task ExpandAll_OpensEveryNode()
    {
        var vm = await MakeVmAsync();

        vm.SetAllFoldersExpanded(true);

        Assert.All(Flatten(vm.FolderTree), n => Assert.True(n.IsExpanded, n.Label + " is still collapsed."));
    }

    [Fact]
    public async Task HasExpandableFolders_IsTrueOnceTheTreeNests_AndFalseBeforeIt()
    {
        var empty = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        Assert.False(empty.HasExpandableFolders);

        Assert.True((await MakeVmAsync()).HasExpandableFolders);
    }

    // ── The tree rebuild must not undo it ────────────────────────────────────

    [Fact]
    public async Task ARebuiltTree_KeepsACollapsedAccountHeaderCollapsed()
    {
        // Regression. BuildFolderTree used to capture only the expanded nodes and re-apply them
        // additively, so a header — built expanded by default — re-opened on the next folder
        // refresh. Collapse All then looked like it had silently failed a few seconds later.
        var vm = await MakeVmAsync();
        vm.SetAllFoldersExpanded(false);

        await vm.ConnectAllAccountsAsync();   // reloads folders, rebuilding the tree

        Assert.All(Flatten(vm.FolderTree).Where(n => n.IsHeader),
                   n => Assert.False(n.IsExpanded, "header '" + n.Label + "' re-expanded on rebuild."));
    }

    [Fact]
    public async Task ARebuiltTree_KeepsAnExpandedFolderExpanded()
    {
        // The other direction, which the additive restore did get right — kept so a fix to the
        // above cannot regress it.
        var vm = await MakeVmAsync();
        vm.SetAllFoldersExpanded(true);

        await vm.ConnectAllAccountsAsync();

        Assert.All(Flatten(vm.FolderTree), n => Assert.True(n.IsExpanded, n.Label + " collapsed on rebuild."));
    }

    // ── Entry points: the Folder menu and the Command Palette ────────────────
    //
    // Both are read from the source. The menu bar's items and the command registrations live behind
    // a shown window (registrations run in OnLoaded), which is the opt-in input-test territory these
    // tests deliberately stay out of. Renaming or deleting either entry point fails here.

    [Theory]
    [InlineData("MenuExpandFolder_Click")]
    [InlineData("MenuCollapseFolder_Click")]
    [InlineData("MenuExpandAllFolders_Click")]
    [InlineData("MenuCollapseAllFolders_Click")]
    public void TheFolderMenuCarriesEachAction(string handler)
    {
        Assert.Contains(handler, FolderMenuBody(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolderMenusAccessKeysStayUnique()
    {
        // Same collision rule as the context menus, read from source because the menu bar is not a
        // resource that can be resolved without showing the window.
        var keys = Regex.Matches(FolderMenuBody(), "Header=\"(?<h>[^\"]*)\"")
            .Select(m => AccessKeyOf(m.Groups["h"].Value))
            .Where(k => k != null)
            .ToList();

        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // The access key a menu header declares, e.g. "Colla_pse Folder" -> "p". Null when it has none.
    internal static string? AccessKeyOf(string? header)
    {
        if (header == null) return null;
        var m = Regex.Match(header, "_(?<k>[A-Za-z0-9])");
        return m.Success ? m.Groups["k"].Value : null;
    }

    // The body of the menu bar's Folder menu, from its own header to the View menu that follows it.
    private static string FolderMenuBody()
    {
        var match = Regex.Match(
            Source("Views/MainWindow.xaml"),
            "<MenuItem Header=\"Fol_der\">(?<body>.*?)</MenuItem>\\s*\\r?\\n\\s*<!-- View -->",
            RegexOptions.Singleline);
        Assert.True(match.Success, "the Folder menu is gone from MainWindow.xaml.");
        return match.Groups["body"].Value;
    }

    [Theory]
    [InlineData("folder.expand")]
    [InlineData("folder.collapse")]
    [InlineData("folder.expandAll")]
    [InlineData("folder.collapseAll")]
    public void EachActionIsRegisteredAsACommand(string commandId)
    {
        // Registration is what puts an action in the Command Palette and in keyboard
        // customizations — the third entry point #590 asks for.
        Assert.Contains("id: \"" + commandId + "\"", Source("Views/MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    private static string Source(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), path + " not found.");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }
}

/// <summary>
/// The window-loading half of the #590 tests. Split from <see cref="FolderExpansionTests"/> and put
/// in the WpfTests collection: constructing a MainWindow in parallel with another test doing the
/// same races inside XAML loading, which is what that collection exists to serialize. The plain
/// tests stay out of it — a non-STA test scheduled inside the collection disturbs it in turn.
/// </summary>
[Collection("WpfTests")]
public class FolderExpansionMenuTests
{
    private static readonly string[] WholeTreeActions = ["Expand All Folders", "Collapse All Folders"];

    // ── Entry points: the two context menus ──────────────────────────────────

    private static List<MenuItem> MenuItemsOf(System.Windows.FrameworkElement owner, string resourceKey)
    {
        var menu = owner.FindResource(resourceKey) as ContextMenu;
        Assert.NotNull(menu);
        return [.. menu!.Items.OfType<MenuItem>()];
    }

    [StaFact]
    public void TheFolderContextMenu_OffersAllFourActions()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var window = MakeWindow();
        try
        {
            var names = MenuItemsOf(window, "FolderContextMenu").Select(AutomationProperties.GetName).ToList();
            Assert.Contains("Expand Folder", names);
            Assert.Contains("Collapse Folder", names);
            Assert.All(WholeTreeActions, a => Assert.Contains(a, names));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheCalendarContextMenu_OffersThemToo_WordedForACalendar()
    {
        // Calendar nodes nest in the same tree but get a different menu (#497), so they would
        // otherwise be the one branch of the folder tree with no way to fold itself away.
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var window = MakeWindow();
        try
        {
            var names = MenuItemsOf(window, "CalendarContextMenu").Select(AutomationProperties.GetName).ToList();
            Assert.Contains("Expand Calendar", names);
            Assert.Contains("Collapse Calendar", names);
            Assert.All(WholeTreeActions, a => Assert.Contains(a, names));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void EachMenusAccessKeysStayUnique()
    {
        // WPF cycles between duplicate access keys instead of invoking, so a collision turns a
        // menu item into a highlight that does nothing — the failure #516 called out.
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var window = MakeWindow();
        try
        {
            foreach (var key in new[] { "FolderContextMenu", "CalendarContextMenu" })
            {
                var keys = MenuItemsOf(window, key)
                    .Select(i => FolderExpansionTests.AccessKeyOf(i.Header as string))
                    .Where(k => k != null)
                    .ToList();
                Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }
        finally { window.Close(); }
    }

    private static QuickMail.Views.MainWindow MakeWindow()
    {
        var imap     = new StubImapMailService();
        var accounts = new StubAccountService();
        var creds    = new StubCredentialService();
        var store    = new StubLocalStoreService();
        var config   = new StubConfigService();
        var registry = new StubCommandRegistry();

        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(),
            new StubSyncService(), config, registry, new StubViewService(), new StubRuleService(),
            new StubSmtpService());

        return new QuickMail.Views.MainWindow(vm, new StubSmtpService(), accounts, creds, imap,
            new StubOAuthService(), registry, new StubContactService(), config, store,
            new StubViewService(), new StubRuleService(), new StubTemplateService(),
            new StubFeatureGate());
    }


}
