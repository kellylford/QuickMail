// Issue #451: when a row that is already on screen is given its source-folder label, the row's
// accessible name must change with it. Before #451 FolderDisplayName raised no change notification,
// so the RowSpeech MultiBinding kept the name it composed when the row was realized.
//
// A bare ListView carrying the real RowSpeech.Kind setter rather than MainWindow, for the reason
// SelectorItemAccessibilityTests gives: showing MainWindow brings up WebView2, the tray icon and
// timers on the test's STA thread (#211). MessageRowStyles_AllCarryTheRowSpeechSetter there already
// checks that every MainWindow message-row style carries this setter.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class FolderLabelRowSpeechTests
{
    /// <summary>Stands in for MainViewModel as the window DataContext RowSpeech binds through.</summary>
    private sealed class RowSpeechHost
    {
        public RowSpeechSettings RowSpeech { get; } = RowSpeechSettings.Default;
    }

    [StaFact]
    public void RealizedRow_AccessibleName_PicksUpALateFolderLabel()
    {
        var msg = new MailMessageSummary
        {
            From = "Chris Lee", Subject = "Budget review", Date = DateTimeOffset.Now, IsRead = true,
        };

        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(RowSpeech.KindProperty, RowKind.Message));
        var list = new ListView { ItemsSource = new[] { msg }, ItemContainerStyle = style };
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
            Drain();
            var container = list.ItemContainerGenerator.ContainerFromItem(msg) as ListViewItem;
            Assert.NotNull(container);

            var before = AutomationProperties.GetName(container!);
            Assert.False(string.IsNullOrWhiteSpace(before), "RowSpeech did not name the row container.");
            Assert.DoesNotContain("Inbox", before, StringComparison.Ordinal);

            // What RestampDisplayedFolderLabels does to a row already on screen once folders arrive.
            msg.FolderDisplayName = "Work -- Inbox";
            Drain();

            var after = AutomationProperties.GetName(container!);
            Assert.EndsWith("Work -- Inbox.", after, StringComparison.Ordinal);
            Assert.StartsWith(before.TrimEnd('.'), after, StringComparison.Ordinal);

            // And the item peer a screen reader actually reads says the same.
            var peerName = (UIElementAutomationPeer.CreatePeerForElement(list).GetChildren() ?? [])
                .Select(p => p.GetName())
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            Assert.Equal(after, peerName);
        }
        finally { window.Close(); }
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
