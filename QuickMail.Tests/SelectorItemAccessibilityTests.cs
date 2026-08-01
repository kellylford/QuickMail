using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Regression guard for a whole class of accessibility bug: WPF reads a data-bound
/// ComboBox/ListBox/ListView item's UIA Name from <c>item.ToString()</c> — NOT from
/// <c>DisplayMemberPath</c>, which only drives the visual. So an item type bound into
/// a Selector must override <c>ToString()</c> to its display text, or a screen reader
/// reads the fully-qualified type name (plain class) or record punctuation
/// (<c>"ThemeOption { Id = ..., Name = ... }"</c>). The theme ComboBox shipped with
/// exactly that record bug; these tests lock the fix and cover every item type the app
/// binds into a Selector so a new one can't silently reintroduce it.
/// </summary>
// Part of the WpfTests collection so it never runs concurrently with another WPF/STA test.
// This class creates and Show()s a real Window on its own STA thread; running it in parallel
// with the other STA tests means two STA threads can own live WPF windows at once, which is
// the concurrency that produces the intermittent HwndSubclass teardown crash and focus-
// navigation flakes (issue #211).
[Collection("WpfTests")]
public class SelectorItemAccessibilityTests
{
    /// <summary>
    /// MainWindow's item container styles use <c>BasedOn="{StaticResource {x:Type ListViewItem}}"</c>,
    /// which resolves against the implicit styles in ThemedControls — merged at runtime by
    /// ThemeService before the first window is created. Without them the XAML fails to parse with
    /// "Cannot find resource named 'System.Windows.Controls.ListViewItem'".
    /// </summary>
    private static void EnsureThemedApplication()
    {
        lock (typeof(Application))
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        var app = Application.Current!;
        foreach (var style in new[] { "AccessibleStyles", "ThemedControls" })
        {
            var uri = new Uri($"pack://application:,,,/QuickMail;component/Styles/{style}.xaml",
                UriKind.Absolute);
            if (app.Resources.MergedDictionaries.All(d => d.Source != uri))
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }
    }

    // End-to-end: the actual reported control. Reads the real UIA item peer names a
    // screen reader would speak — this is what the reviews never checked.
    [StaFact]
    public void ThemeComboBox_ItemPeerNames_AreCleanDisplayNames()
    {
        var combo = new ComboBox
        {
            DisplayMemberPath = "Name",
            ItemsSource = new List<SettingsViewModel.ThemeOption>
            {
                new("system", "System"),
                new("parchment", "Parchment"),
                new("dark", "Parchment Dark"),
            },
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 200, Height = 100, Content = combo,
        };
        window.Show();
        try
        {
            combo.IsDropDownOpen = true;
            combo.UpdateLayout();
            DrainDispatcher(); // let item containers / peers realize before reading names

            // GetChildren() also surfaces the ComboBox's editable/selection chrome peers (e.g. a
            // TextBoxAutomationPeer with no name), and whether those are realized is load-dependent —
            // in isolation only the item peers appear, but under CPU pressure from the parallel suite
            // an empty-named TextBox peer is realized at index 0 too. Filter to the item peers (the
            // elements a screen reader announces when navigating the list) so the assertion is stable
            // and still catches a leaked type-name / record-dump ToString on any item.
            var names = (UIElementAutomationPeer.CreatePeerForElement(combo).GetChildren() ?? new List<AutomationPeer>())
                .OfType<ListBoxItemAutomationPeer>()
                .Select(p => p.GetName())
                .ToList();
            Assert.Equal(new[] { "System", "Parchment", "Parchment Dark" }, names);
        }
        finally { window.Close(); }
    }

    // Pumps the STA dispatcher until all queued work down to SystemIdle priority (layout,
    // container generation, peer realization) has run.
    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // Cheap, fast guard on the item types themselves: their ToString() must be display
    // text — never the default type name or record "{ ... }" punctuation. If a new
    // Selector-bound type is added without a ToString override, add it here.
    [Fact]
    public void BoundItemTypes_ToString_IsDisplayText()
    {
        Assert.Equal("Quill", new SettingsViewModel.ThemeOption("quill", "Quill").ToString());
        Assert.Equal("My View", new SavedView { Name = "My View" }.ToString());
        Assert.Equal("Greeting", new MessageTemplate { Title = "Greeting" }.ToString());
        Assert.Equal("Spam rule", new MailRule { Name = "Spam rule" }.ToString());
        Assert.Equal("Work", new AccountModel { AccountName = "Work" }.ToString());

        // Unified rules manager: the account picker (AccountCombo) and the merged rules list
        // (RulesListBox) — neither has a DisplayMemberPath/ItemTemplate, so ToString() drives the name.
        Assert.Equal("Work", new AccountOption { DisplayName = "Work" }.ToString());
        var unifiedRow = UnifiedRuleRow.ForClient(new MailRule { Name = "Newsletter", AccountId = System.Guid.NewGuid() });
        Assert.Equal(unifiedRow.RowText, unifiedRow.ToString());
        Assert.Contains("Newsletter", unifiedRow.ToString());

        // Provider combo in the Add Account dialog.
        Assert.Equal("Gmail", new ProviderCatalog().ById(ProviderCatalog.GmailId)!.ToString());

        // Month grid day cells (MonthGrid ListBox): spoken name = full day context.
        var cell = new MonthCell(new System.DateTime(2026, 7, 21), displayedMonth: 7, dayEvents:
            [new CalendarEvent { Summary = "A" }, new CalendarEvent { Summary = "B" }]);
        Assert.Equal(cell.AccessibleName, cell.ToString());
        Assert.Contains("Tuesday July 21", cell.ToString());
        Assert.Contains("2 events", cell.ToString());

        var group = new GroupModel { Name = "Team" };
        Assert.Equal(group.Display, group.ToString());

        // Message List Fields chooser: the row-type ComboBox and the field ListBox. The field
        // rows render a CheckBox, which drives only the visual — the item's name is ToString().
        Assert.Equal("Messages", new RowKindOption(RowKind.Message, "Messages").ToString());
        var fieldRow = new RowFieldRow(
            RowFieldCatalog.Find(RowKind.Message, "subject")!,
            new RowFieldSetting { Id = "subject", Enabled = true },
            onChanged: () => { });
        Assert.Equal("Subject", fieldRow.ToString());

        // Connection Diagnostics lists. The account row must speak the disagreement, not just the
        // label — "shown as disconnected but the server answered" is the entire point of that list.
        var row = new ConnectionAccountRow
        {
            Id = Guid.NewGuid(), Label = "Kelly", Host = "mail.example.com",
            StatusText = "disconnected", VerdictText = "Server answered normally.",
        };
        Assert.Equal("Kelly, disconnected. Server answered normally.", row.ToString());

        // Journal rows are read one line at a time, so the line itself is the accessible name.
        var evt = new ConnectionEvent(
            new System.DateTime(2026, 7, 28, 14, 3, 9), ConnectionEventKind.Connect,
            "Kelly", "mail.example.com", "connect-failed", "SocketError=TimedOut");
        Assert.Contains("connect-failed", evt.ToString());
        Assert.Contains("account=Kelly", evt.ToString());
        Assert.Contains("SocketError=TimedOut", evt.ToString());
    }

    // End-to-end against the REAL RulesManagerWindow: what a screen reader actually speaks for a
    // rule row must be "name, account" (issue: account-in-row not announced). Reads the live
    // ListBoxItemAutomationPeer name — the ground truth, not the eyeballed template.
    [StaFact]
    public void RulesManagerWindow_RuleRow_ItemPeerName_IncludesAccount()
    {
        var acctId = Guid.NewGuid();
        var accounts = new[] { new AccountModel { Id = acctId, AccountName = "IdeaPlace" } };
        var stub = new StubRuleService { LoadedRules = [new MailRule { Name = "Newsletters", AccountId = acctId }] };
        var vm = new RulesManagerViewModel(stub, accounts);

        var window = new RulesManagerWindow(vm, accounts, new Dictionary<Guid, List<MailFolderModel>>())
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var list = (ListBox)window.FindName("RuleListBox");
            var names = (UIElementAutomationPeer.CreatePeerForElement(list).GetChildren() ?? new List<AutomationPeer>())
                .OfType<ListBoxItemAutomationPeer>()
                .Select(p => p.GetName())
                .ToList();

            Assert.Contains("Newsletters, IdeaPlace", names);
        }
        finally { window.Close(); }
    }

    // The composed spoken string must land on the row CONTAINER — the element a screen reader lands
    // on when arrowing the list. RowSpeech.Kind installs that binding from a Style setter; if it
    // were ever moved to a DataTemplate the name would attach to the wrong element and every row
    // would go silent, which looks completely fine on screen. Reads the live item peer name.
    //
    // Deliberately a bare ListView carrying the same setter rather than the real MainWindow:
    // showing MainWindow brings up WebView2, the tray icon and timers on the test's STA thread,
    // which is the HwndSubclass teardown crash this class's header warns about (#211). What
    // MainWindow contributes — that the setter is present on all six row styles — is covered
    // without a window by MessageRowStyles_AllCarryTheRowSpeechSetter below.
    [StaFact]
    public void RowSpeech_PutsTheComposedStringOnTheRowContainer()
    {
        var msg = new MailMessageSummary
        {
            From = "Chris Lee", Subject = "Budget review", To = "Sales Team",
            Date = DateTimeOffset.Now, IsRead = false, Preview = "Lunch tomorrow?",
            HasAttachments = true, FolderDisplayName = "Work — Archive",
        };

        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(RowSpeech.KindProperty, RowKind.Message));

        var list = new ListView { ItemsSource = new[] { msg }, ItemContainerStyle = style };

        // RowSpeech binds the layout via RelativeSource=Window, so the list needs a Window ancestor
        // whose DataContext exposes RowSpeech — exactly as MainWindow's does.
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 600, Height = 200, Content = list,
            DataContext = new RowSpeechHost(),
        };
        window.Show();
        try
        {
            list.UpdateLayout();
            DrainDispatcher();

            // ListView's item peer type varies with GridView, so read every child peer's name
            // rather than filtering on a concrete peer type.
            var name = (UIElementAutomationPeer.CreatePeerForElement(list).GetChildren() ?? [])
                .Select(p => p.GetName())
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            Assert.False(string.IsNullOrWhiteSpace(name),
                "The message row has no accessible name — RowSpeech did not bind the container.");

            // The shipped default order, including #423's source folder last. The date comes from
            // the message itself — recomputing "now" here races the clock across a minute boundary.
            Assert.Equal(
                "unread. attachments. Chris Lee. Budget review. Lunch tomorrow?. " +
                $"{msg.DateDisplay}. Work — Archive.",
                name);

            // The row must NOT be announced as its type name — the failure mode this class exists for.
            Assert.DoesNotContain("QuickMail.", name!, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    /// <summary>Stands in for MainViewModel as the window DataContext RowSpeech binds through.</summary>
    private sealed class RowSpeechHost
    {
        public RowSpeechSettings RowSpeech { get; } = RowSpeechSettings.Default;
    }

    // Every message-list row style in MainWindow must carry the RowSpeech.Kind setter, with the
    // right kind. Constructing MainWindow (never showing it) is the same safe pattern XamlParseTests
    // uses; this is what catches a row style that was added, or edited, without its spoken name.
    [StaFact]
    public void MessageRowStyles_AllCarryTheRowSpeechSetter()
    {
        EnsureThemedApplication();

        var imap     = new StubImapMailService();
        var accounts = new StubAccountService();
        var creds    = new StubCredentialService();
        var store    = new StubLocalStoreService();
        var config   = new StubConfigService();
        var registry = new StubCommandRegistry();

        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(),
            new StubSyncService(), config, registry, new StubViewService(), new StubRuleService(),
            new StubSmtpService(), rowLayoutService: new StubRowLayoutService());

        var window = new MainWindow(vm, new StubSmtpService(), accounts, creds, imap,
            new StubOAuthService(), registry, new StubContactService(), config, store,
            new StubViewService(), new StubRuleService(), new StubTemplateService(),
            new StubFeatureGate());
        try
        {
            // Flat message list.
            var flat = (window.FindName("MessageList") as ListView)?.ItemContainerStyle;
            Assert.NotNull(flat);
            Assert.Equal(RowKind.Message, KindOf(flat!));

            // Each tree contributes two styles: its own ItemContainerStyle names the group header
            // rows, and a keyed style in its Resources names the level-2 message rows. The To tree
            // reuses the SenderGroup layout, since its rows are SenderGroup objects.
            foreach (var (treeName, styleKey, groupKind) in new[]
            {
                ("ConversationTree",  "MessageTreeItemStyle",       RowKind.Conversation),
                ("SenderGroupTree",   "SenderMessageTreeItemStyle", RowKind.SenderGroup),
                ("ToGroupTree",       "ToMessageTreeItemStyle",     RowKind.SenderGroup),
            })
            {
                var tree = window.FindName(treeName) as TreeView;
                Assert.NotNull(tree);

                Assert.NotNull(tree!.ItemContainerStyle);
                Assert.Equal(groupKind, KindOf(tree.ItemContainerStyle));

                // Declared inside <TreeView.Resources>, so it is not reachable from the window.
                var messageStyle = tree.Resources[styleKey] as Style;
                Assert.NotNull(messageStyle);
                Assert.Equal(RowKind.Message, KindOf(messageStyle!));
            }
        }
        finally { window.Close(); }

        static RowKind? KindOf(Style style) => style.Setters
            .OfType<Setter>()
            .Where(s => s.Property == RowSpeech.KindProperty)
            .Select(s => s.Value as RowKind?)
            .FirstOrDefault();
    }

    [Fact]
    public void BoundItemTypes_ToString_NeverLooksLikeTypeNameOrRecordDump()
    {
        var strings = new[]
        {
            new SettingsViewModel.ThemeOption("a", "Alpha").ToString(),
            new SavedView { Name = "Alpha" }.ToString(),
            new MessageTemplate { Title = "Alpha" }.ToString(),
            new MailRule { Name = "Alpha" }.ToString(),
            new GroupModel { Name = "Alpha" }.ToString(),
            new ProviderCatalog().Other.ToString(),
            new ConnectionAccountRow
            {
                Id = Guid.NewGuid(), Label = "Alpha", Host = "h",
                StatusText = "connected", VerdictText = "Confirmed reachable.",
            }.ToString(),
        };
        foreach (var s in strings)
        {
            Assert.DoesNotContain("{", s);                 // record dump
            Assert.DoesNotContain("QuickMail.", s);        // fully-qualified type name
        }
    }
}
