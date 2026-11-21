using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickMail.Helpers;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The menu entry points for ordering: the account list's reorder items, and the folder tree's
/// Reorder submenu (#1058), which replaced the Calendar node's position items.
/// </summary>
// Loads a real MainWindow, so it belongs in the collection that serializes window-loading tests:
// two of them constructing a MainWindow at once race inside XAML loading (#590).
[Collection("WpfTests")]
public class AccountOrderMenuTests
{
    private static MainWindow MakeWindow()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

        var imap     = new StubImapMailService();
        var accounts = new StubAccountService();
        var creds    = new StubCredentialService();
        var store    = new StubLocalStoreService();
        var config   = new StubConfigService();
        var registry = new StubCommandRegistry();

        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(),
            new StubSyncService(), config, registry, new StubViewService(), new StubRuleService(),
            new StubSmtpService(), calendarService: new StubCalendarService());

        return new MainWindow(vm, new StubSmtpService(), accounts, creds, imap,
            new StubOAuthService(), registry, new StubContactService(), config, store,
            new StubViewService(), new StubRuleService(), new StubTemplateService(),
            new StubFeatureGate());
    }

    private static List<MenuItem> AccountMenuItems(MainWindow window)
    {
        var list = window.FindName("AccountList") as ListBox;
        Assert.NotNull(list);
        Assert.NotNull(list!.ContextMenu);
        return [.. list.ContextMenu!.Items.OfType<MenuItem>()];
    }

    [StaFact]
    public void TheAccountListOffersAllFourMoves()
    {
        var window = MakeWindow();
        try
        {
            var names = AccountMenuItems(window).Select(AutomationProperties.GetName).ToList();

            Assert.Contains("Move Up", names);
            Assert.Contains("Move Down", names);
            Assert.Contains("Move to Start", names);
            Assert.Contains("Move to End", names);
            // The actions that were already there must survive the additions.
            Assert.Contains("Delete Account", names);
            Assert.Contains("Account Settings", names);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheMovesShowTheirKeysAndKeepTheirAccessKeysUnique()
    {
        // Each move names the command its key runs, which is where the shown and spoken key come
        // from (#849); ShortcutExposureTests checks the ids are registered. And WPF cycles between
        // duplicate access keys instead of invoking, so a collision turns a menu item into a
        // highlight that does nothing (#516).
        var window = MakeWindow();
        try
        {
            var items = AccountMenuItems(window);

            var ids = items.ToDictionary(AutomationProperties.GetName, i => CommandShortcut.GetId(i));
            Assert.Equal("account.moveUp",      ids["Move Up"]);
            Assert.Equal("account.moveDown",    ids["Move Down"]);
            Assert.Equal("account.moveToStart", ids["Move to Start"]);
            Assert.Equal("account.moveToEnd",   ids["Move to End"]);

            var keys = items.Select(i => AccessKeyOf(i.Header as string)).Where(k => k != null).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #1058: both folder-tree menus have one Reorder submenu with the four moves, and the Calendar
    /// position items it replaced are gone. Every access key in each menu, and in the submenu, is
    /// unique — WPF cycles between duplicates instead of invoking.
    /// </summary>
    [StaTheory]
    [InlineData("FolderContextMenu")]
    [InlineData("CalendarContextMenu")]
    public void BothTreeMenusHaveAReorderSubmenu_WithTheFourMoves(string menuKey)
    {
        var window = MakeWindow();
        try
        {
            var menu = window.FindResource(menuKey) as ContextMenu;
            Assert.NotNull(menu);
            var items = menu!.Items.OfType<MenuItem>().ToList();

            var reorder = Assert.Single(items, i => AutomationProperties.GetName(i) == "Reorder");
            Assert.Equal("o", AccessKeyOf(reorder.Header as string), ignoreCase: true);
            var moves = reorder.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["account.moveUp", "account.moveDown", "account.moveToStart", "account.moveToEnd"],
                         moves.Select(m => CommandShortcut.GetId(m)));
            Assert.Equal(["Move Up", "Move Down", "Move to Start", "Move to End"],
                         moves.Select(AutomationProperties.GetName));

            Assert.All(new[] { items, moves }, level =>
            {
                var keys = level.Select(i => AccessKeyOf(i.Header as string)).Where(k => k != null).ToList();
                Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            });

            Assert.DoesNotContain(items, i => (AutomationProperties.GetName(i) ?? "").StartsWith("Calendar at", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The Reorder items sit one level down, in a submenu, so the context-menu lookup has to walk up
    /// through it to the tree item the menu opened on.
    /// </summary>
    [StaFact]
    public void AReorderItemFindsTheTreeNodeItsMenuOpenedOn()
    {
        var node = new QuickMail.Models.FolderTreeNode { Label = "Views", IsHeader = true };
        var item = new TreeViewItem { DataContext = node };
        var inner = new MenuItem();
        var submenu = new MenuItem();
        submenu.Items.Add(inner);
        var menu = new ContextMenu { PlacementTarget = item };
        menu.Items.Add(submenu);

        Assert.Same(node, MainWindow.GetContextMenuFolderNode(inner));
        Assert.Same(node, MainWindow.GetContextMenuFolderNode(submenu));
        Assert.Null(MainWindow.GetContextMenuFolderNode(new MenuItem()));
    }

    /// <summary>The letter after the first single underscore, or null when the header has none.</summary>
    private static string? AccessKeyOf(string? header)
    {
        if (header == null) return null;
        for (var i = 0; i < header.Length - 1; i++)
        {
            if (header[i] != '_') continue;
            if (header[i + 1] == '_') { i++; continue; }
            return header[i + 1].ToString();
        }
        return null;
    }
}

/// <summary>
/// The commands behind those menu items. A context menu must never be the only way into an action —
/// that is the dead end #250 was filed about — so each item has a registered twin reachable from
/// the Command Palette and rebindable in keyboard customizations. Registration is also what makes
/// Alt+Up and Alt+Down work at all: they are dispatched by the registry, not by a hardcoded branch
/// in the account list's key handler, which is the rule the shortcut section of CLAUDE.md sets.
///
/// Read from source rather than from a live registry, because the window registers its commands in
/// OnLoaded and these tests construct a window without showing one. That makes the assertions
/// literal, so keep them to the parts that would be silently wrong if they changed: the ids, the
/// gestures the menu advertises, and the focus scope.
/// </summary>
public class AccountOrderCommandRegistrationTests
{
    private static string MainWindowSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "QuickMail", "Views", "MainWindow.xaml.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new InvalidOperationException("MainWindow.xaml.cs not found from " + AppContext.BaseDirectory + ".");
    }

    [Theory]
    [InlineData("account.moveUp", "Key.Up")]
    [InlineData("account.moveDown", "Key.Down")]
    [InlineData("account.moveToStart", "Key.Home")]
    [InlineData("account.moveToEnd", "Key.End")]
    public void EveryMoveIsRegistered_InTheViewCategory_OnItsAltGesture(string id, string key)
    {
        // #1058: View, since they now arrange the folder tree as well as the Accounts list.
        var source = MainWindowSource();

        Assert.Contains($"id: \"{id}\", category: \"View\"", source, StringComparison.Ordinal);
        Assert.Contains($"defaultKey: {key}, defaultModifiers: ModifierKeys.Alt", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("account.moveUp", "Move Up")]
    [InlineData("account.moveDown", "Move Down")]
    [InlineData("account.moveToStart", "Move to Start")]
    [InlineData("account.moveToEnd", "Move to End")]
    public void EveryMoveHasTheMenuItemsName(string id, string title)
    {
        var source = MainWindowSource();

        Assert.Contains($"id: \"{id}\", category: \"View\", title: \"{title}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMoveKeysAreScopedToTheAccountListAndTheFolderTree()
    {
        // The window's PreviewKeyDown sees every keystroke, so an unscoped Alt+Up would reorder
        // things while the user was reading a message.
        var source = MainWindowSource();

        Assert.Equal(4, source.Split("isAvailable: IsArrangeScope").Length - 1);
        Assert.Contains(
            "private bool IsArrangeScope() => AccountList.IsKeyboardFocusWithin || FolderList.IsKeyboardFocusWithin;",
            source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both moves land focus back where the user was standing. The account list is a plain
    /// virtualizing ListBox, so a row scrolled out of sight has no container and the lookup returns
    /// null — Move to End on a list taller than the pane would otherwise strand focus on the
    /// ListBox itself, which nothing redirects into a row. The folder tree has the same problem for
    /// the opposite reason: a move is a Move of that node, which regenerates its TreeViewItem while
    /// the user is standing on it.
    /// </summary>
    [Fact]
    public void BothMovesPutFocusBackOnWhatMoved()
    {
        var source = MainWindowSource();

        Assert.Contains("AccountList.ScrollIntoView(account);", source, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.InvokeAsync(() => FocusTreeItem(FolderList, node), DispatcherPriority.Input);",
                        source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("calendar.calendarAtTopOfFolderList")]
    [InlineData("calendar.calendarAtBottomOfFolderList")]
    public void TheCalendarPositionCommandsAreRetired(string id)
        => Assert.DoesNotContain($"\"{id}\"", MainWindowSource(), StringComparison.Ordinal);
}
