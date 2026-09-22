using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The menu entry points for the two ordering features: the account list's reorder items and the
/// Calendar node's position items.
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
        // InputGestureText must match the registered default, or the menu advertises a key that
        // does nothing. And WPF cycles between duplicate access keys instead of invoking, so a
        // collision turns a menu item into a highlight that does nothing (#516).
        var window = MakeWindow();
        try
        {
            var items = AccountMenuItems(window);

            var gestures = items.ToDictionary(AutomationProperties.GetName, i => i.InputGestureText);
            Assert.Equal("Alt+Up",   gestures["Move Up"]);
            Assert.Equal("Alt+Down", gestures["Move Down"]);
            Assert.Equal("Alt+Home", gestures["Move to Start"]);
            Assert.Equal("Alt+End",  gestures["Move to End"]);

            var keys = items.Select(i => AccessKeyOf(i.Header as string)).Where(k => k != null).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheCalendarMenuOffersBothPositions_AsCheckableItems()
    {
        // Checkable so the platform reports the current choice on open. QuickMail announces nothing
        // extra for it: a menu item's own check state is already spoken.
        var window = MakeWindow();
        try
        {
            var menu = window.FindResource("CalendarContextMenu") as ContextMenu;
            Assert.NotNull(menu);
            var items = menu!.Items.OfType<MenuItem>()
                .Where(i => i.Tag as string is "calendarFirst" or "calendarLast").ToList();

            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.True(i.IsCheckable));
            Assert.Contains("Calendar at Top of Folder List", items.Select(AutomationProperties.GetName));
            Assert.Contains("Calendar at Bottom of Folder List", items.Select(AutomationProperties.GetName));
        }
        finally { window.Close(); }
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
    public void EveryMoveIsRegistered_InTheAccountCategory_OnItsAltGesture(string id, string key)
    {
        var source = MainWindowSource();

        Assert.Contains($"id: \"{id}\", category: \"Account\"", source, StringComparison.Ordinal);
        Assert.Contains($"defaultKey: {key}, defaultModifiers: ModifierKeys.Alt", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMoveKeysAreScopedToTheAccountList()
    {
        // The window's PreviewKeyDown sees every keystroke, so an unscoped Alt+Up would reorder
        // accounts while the user was reading a message.
        var source = MainWindowSource();

        var scoped = source.Split("isAvailable: () => AccountList.IsKeyboardFocusWithin").Length - 1;
        Assert.Equal(4, scoped);
    }

    /// <summary>
    /// Both moves land focus back where the user was standing. The account list is a plain
    /// virtualizing ListBox, so a row scrolled out of sight has no container and the lookup returns
    /// null — Move to End on a list taller than the pane would otherwise strand focus on the
    /// ListBox itself, which nothing redirects into a row. The Calendar node has the same problem
    /// for the opposite reason: moving it back to the top is a Move of that node, which regenerates
    /// its TreeViewItem while the user is standing on it.
    /// </summary>
    [Fact]
    public void BothMovesPutFocusBackOnWhatMoved()
    {
        var source = MainWindowSource();

        Assert.Contains("AccountList.ScrollIntoView(account);", source, StringComparison.Ordinal);
        Assert.Contains("FocusTreeItem(FolderList, calendar);", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("calendar.calendarAtTopOfFolderList")]
    [InlineData("calendar.calendarAtBottomOfFolderList")]
    public void BothCalendarPositionsAreRegistered_InTheCalendarCategory(string id)
    {
        var source = MainWindowSource();

        Assert.Contains($"id: \"{id}\", category: \"Calendar\"", source, StringComparison.Ordinal);
    }
}
