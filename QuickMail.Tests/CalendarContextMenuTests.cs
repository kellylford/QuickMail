using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The folder tree hands calendar nodes a different context menu from mail folders (issue #497).
///
/// This is worth a test because the failure is silent in both directions: the trigger binding is a
/// string, so a typo leaves calendar nodes on the mail folder menu — the exact state this change
/// fixes, where New Folder / Move / Delete are offered on a calendar and every one of them does
/// nothing — and an over-broad trigger would strip real folders of the menu that is their only
/// mouse path to Move, Copy, and Set as Archive.
/// </summary>
public class CalendarContextMenuTests
{
    /// <summary>The shared host owns the Application (issue #211); this suite additionally needs
    /// ThemedControls, or the XAML fails to parse with "Cannot find resource named
    /// 'System.Windows.Controls.ListViewItem'".</summary>
    private static void EnsureThemedApplication() =>
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

    private static MainWindow MakeWindow()
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

        return new MainWindow(vm, new StubSmtpService(), accounts, creds, imap,
            new StubOAuthService(), registry, new StubContactService(), config, store,
            new StubViewService(), new StubRuleService(), new StubTemplateService(),
            new StubFeatureGate());
    }

    /// <summary>
    /// Resolves the ContextMenu a tree item would actually get for the given node, by applying the
    /// live ItemContainerStyle to a container the way the TreeView does. DataTriggers evaluate off
    /// DataContext, so no visual parent is needed.
    /// </summary>
    private static ContextMenu? MenuFor(TreeView tree, FolderTreeNode node)
    {
        var item = new TreeViewItem();
        item.BeginInit();
        item.DataContext = node;
        item.Style = tree.ItemContainerStyle;
        item.EndInit();   // style triggers are evaluated at initialization, not at assignment
        return item.ContextMenu;
    }

    [StaFact]
    public void CalendarNodesGetCalendarActions_AndMailFoldersKeepFolderActions()
    {
        EnsureThemedApplication();
        var window = MakeWindow();
        try
        {
            var tree = window.FindName("FolderList") as TreeView;
            Assert.NotNull(tree);

            var folderMenu   = window.FindResource("FolderContextMenu")   as ContextMenu;
            var calendarMenu = window.FindResource("CalendarContextMenu") as ContextMenu;
            Assert.NotNull(folderMenu);
            Assert.NotNull(calendarMenu);
            Assert.NotSame(folderMenu, calendarMenu);

            var calendarNode = new FolderTreeNode
            {
                Folder = new MailFolderModel
                {
                    FullName = MainViewModel.CalendarSourcePrefix + "local",
                    DisplayName = "Local Calendar",
                },
                Label = "Local Calendar",
                IsCalendarNode = true,
            };
            var mailNode = new FolderTreeNode
            {
                Folder = new MailFolderModel
                {
                    AccountId = Guid.NewGuid(), FullName = "INBOX", DisplayName = "Inbox",
                },
                Label = "Inbox",
            };

            Assert.Same(calendarMenu, MenuFor(tree!, calendarNode));
            Assert.Same(folderMenu,   MenuFor(tree!, mailNode));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Both calendar actions must be on the menu — the context menu is the primary entry point, and
    /// a user who set a default with no way to unset it is worse off than before.
    /// </summary>
    [StaFact]
    public void CalendarMenuOffersSetAndClear_WithShortAccessibleNames()
    {
        EnsureThemedApplication();
        var window = MakeWindow();
        try
        {
            var menu = window.FindResource("CalendarContextMenu") as ContextMenu;
            Assert.NotNull(menu);

            var names = menu!.Items.OfType<MenuItem>()
                .Select(AutomationProperties.GetName)
                .ToList();

            Assert.Contains("Use as Default Calendar for New Appointments", names);
            Assert.Contains("Clear Default Calendar", names);
            // No role words, no shortcut text, no sentences — the accessibility checklist's rule.
            Assert.All(names, n => Assert.DoesNotContain(".", n));
        }
        finally { window.Close(); }
    }
}
