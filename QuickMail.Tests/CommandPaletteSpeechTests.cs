// Filter-as-you-type in the command palette, and how the active command reaches a screen
// reader. A search box was shipped here once before and removed (git 1816f96) because it moved
// keyboard focus onto the list on every keystroke and forwarded typed characters back to the
// box. These tests exist to keep that from coming back: the focus assertions below are the
// whole point of the file.
//
// What they cannot prove: AutomationPeer.ListenerExists is false with no UIA client attached,
// so nothing here shows that a screen reader actually SPEAKS. They pin the wiring — which
// control is focused, which peer controls which, what text would be announced. The ear test is
// the user's.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

// Shows a real Window on the STA thread, and AccessibilityHelper.AnnouncementObserver plus
// CommandPaletteWindow.Reporting are static — so this must never run beside another WPF test.
[Collection("WpfTests")]
public class CommandPaletteSpeechTests
{
    // ── Focus: the regression that removed this feature the first time ────────

    [StaFact]
    public void OnOpen_TheFilterBoxHasFocus()
    {
        var (window, box, list) = Open();
        try
        {
            Assert.True(box.IsKeyboardFocused, "The filter box must have focus when the palette opens.");
            Assert.False(list.IsKeyboardFocusWithin);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Typing_LeavesFocusInTheFilterBox()
    {
        var (window, box, list) = Open();
        try
        {
            box.Text = "fold";
            Drain();

            Assert.True(box.IsKeyboardFocused);
            Assert.False(list.IsKeyboardFocusWithin,
                "Focus must never reach the list: that is what broke the 2026-05 search box.");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Arrowing_MovesTheSelectionButNotFocus()
    {
        var (window, box, list) = Open();
        try
        {
            Press(window, Key.Down);
            Drain();

            Assert.Equal(1, list.SelectedIndex);
            Assert.True(box.IsKeyboardFocused, "Down must move the selection, not focus.");
            Assert.False(list.IsKeyboardFocusWithin);

            Press(window, Key.Up);
            Drain();

            Assert.Equal(0, list.SelectedIndex);
            Assert.True(box.IsKeyboardFocused);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheListAndItsRows_CannotTakeFocusAtAll()
    {
        // Belt and braces for the above: a mouse click on a row would otherwise pull focus out
        // of the filter box, breaking typing for anyone who reaches for the mouse.
        var (window, _, list) = Open();
        try
        {
            Assert.False(list.Focusable);
            Assert.False(list.IsTabStop);

            var row = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Assert.NotNull(row);
            Assert.False(row!.Focusable, "A row must not be focusable.");
            Assert.False(row.IsTabStop);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ClickingARow_SelectsItWithoutTakingFocus()
    {
        // Rows are not focusable, and WPF selects a row on click only if it can focus it first.
        // So the palette selects by mouse itself; without that, a double-click would run whatever
        // the keyboard had selected rather than the row pointed at.
        var (window, box, list) = Open();
        try
        {
            var row = list.ItemContainerGenerator.ContainerFromIndex(2) as ListBoxItem;
            Assert.NotNull(row);

            ClickDown(window, row!);
            Drain();

            Assert.Equal(2, list.SelectedIndex);
            Assert.True(box.IsKeyboardFocused, "A click must not move focus off the filter box.");
        }
        finally { window.Close(); }
    }

    // ── Filtering through the real window ────────────────────────────────────

    [StaFact]
    public void Typing_NarrowsTheListAndSelectsTheTopMatch()
    {
        var (window, box, list) = Open();
        try
        {
            box.Text = "arch";
            Drain();

            Assert.Single(list.Items);
            Assert.Equal(0, list.SelectedIndex);
            Assert.Equal("Move to Archive", ((CommandDefinition)list.SelectedItem).Title);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Escape_ClearsTheFilterBeforeItClosesTheWindow()
    {
        var (window, box, list) = Open();
        try
        {
            box.Text = "arch";
            Drain();

            Press(window, Key.Escape);
            Drain();

            Assert.Equal(string.Empty, box.Text);
            Assert.True(window.IsVisible, "The first Escape clears the filter; it does not close.");
            Assert.Equal(4, list.Items.Count);
        }
        finally { window.Close(); }
    }

    // ── The names a screen reader reads off the rows ──────────────────────────

    [StaFact]
    public void FilteredRows_SpeakTheirCommandTitles()
    {
        var (window, box, list) = Open();
        try
        {
            box.Text = "go to";
            Drain();

            var names = ItemPeerNames(list);

            Assert.NotEmpty(names);
            Assert.Contains(names, n => n.StartsWith("Go to", StringComparison.Ordinal));
            // The #644 failure mode: a row announced as its CLR type name.
            foreach (var name in names)
                Assert.DoesNotContain("QuickMail.", name, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheRowContainer_CarriesTheAccessibleName()
    {
        // Asserting only the spoken text passes when the composed name happens to equal
        // ToString(), and then silently stops catching the regression. The name has to be on
        // the container itself.
        var (window, _, list) = Open();
        try
        {
            var row = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Assert.NotNull(row);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(row!)),
                "The row's name must come from the ItemContainerStyle, not from inside the DataTemplate.");
        }
        finally { window.Close(); }
    }

    // ── The UIA mechanism: the filter box declares it drives the list ─────────

    [StaFact]
    public void TheFilterBox_DeclaresThatItControlsTheCommandList()
    {
        // This is what lets a screen reader report the active row while focus stays in the
        // edit box. Without it the palette is silent as you type, whatever else is right.
        var (window, box, list) = Open();
        try
        {
            var boxPeer = UIElementAutomationPeer.CreatePeerForElement(box);
            Assert.NotNull(boxPeer);

            var controlled = boxPeer.GetControlledPeers();
            Assert.NotNull(controlled);

            var listPeer = UIElementAutomationPeer.FromElement(list)
                           ?? UIElementAutomationPeer.CreatePeerForElement(list);
            Assert.Contains(controlled!, p => ReferenceEquals(p, listPeer));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void EveryFilteredRow_HasAnItemPeer_EvenWhileVirtualizing()
    {
        // The explicit-raise reporting mode targets the selected row's peer by index. If item
        // peers did not exist for every item, that mode would be a silent no-op.
        var (window, box, list) = Open();
        try
        {
            box.Text = "e";
            Drain();

            var peers = UIElementAutomationPeer.CreatePeerForElement(list).GetChildren();
            Assert.NotNull(peers);
            Assert.Equal(list.Items.Count, peers!.Count);
        }
        finally { window.Close(); }
    }

    // ── The announcement fallback ────────────────────────────────────────────

    [StaFact]
    public void InAnnounceMode_ArrowingSaysTheCommandAndItsPlace()
    {
        var heard = new List<(string Text, AnnouncementCategory Category)>();
        var previous = CommandPaletteWindow.Reporting;
        CommandPaletteWindow.Reporting = CommandPaletteWindow.ReportMode.Announce;
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));

        var (window, _, list) = Open();
        try
        {
            heard.Clear();

            Press(window, Key.Down);
            Drain();

            var expected = $"{((CommandDefinition)list.SelectedItem).Title}, 2 of 4";
            Assert.Contains(heard, h => h.Text == expected && h.Category == AnnouncementCategory.Result);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            CommandPaletteWindow.Reporting = previous;
            window.Close();
        }
    }

    [StaFact]
    public void InAnnounceMode_AnUnchangedTopMatchSaysNothingFurther()
    {
        // Typing that narrows the list without changing what Enter would run has nothing new to
        // report. Speaking on every keystroke is half of why this feature was removed in 2026-05.
        var heard = new List<(string Text, AnnouncementCategory Category)>();
        var previous = CommandPaletteWindow.Reporting;
        CommandPaletteWindow.Reporting = CommandPaletteWindow.ReportMode.Announce;
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));

        var (window, _, list) = Open();
        try
        {
            Press(window, Key.Down);   // report something, so there is a last-reported command
            Drain();
            heard.Clear();

            var top = list.SelectedItem;
            Press(window, Key.Up);     // back to the top
            Drain();
            var afterFirst = heard.Count;
            heard.Clear();

            Press(window, Key.Up);     // already at the top: nothing changes
            Drain();

            Assert.True(afterFirst > 0, "Moving to a different command must be reported.");
            Assert.Empty(heard);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            CommandPaletteWindow.Reporting = previous;
            window.Close();
        }
    }

    [StaFact]
    public void AnEmptyResultSet_IsAnnouncedInEveryMode()
    {
        // Nothing is selected, so neither UIA path has a row to report. Without this the
        // palette is indistinguishable from one that has stopped responding.
        var heard = new List<(string Text, AnnouncementCategory Category)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));

        var (window, box, _) = Open();
        try
        {
            Assert.Equal(CommandPaletteWindow.ReportMode.Automation, CommandPaletteWindow.Reporting);
            heard.Clear();

            box.Text = "zzzzz";
            PumpPast(TimeSpan.FromMilliseconds(400));   // the typing debounce

            Assert.Contains(heard, h => h.Text == "No matching commands"
                                     && h.Category == AnnouncementCategory.Result);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void InAutomationMode_NothingIsAnnouncedByHand()
    {
        // The default mode leaves the reporting to the platform. If an announcement leaks out
        // here it is speech on top of what the screen reader already says for the row.
        var heard = new List<(string Text, AnnouncementCategory Category)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));

        var (window, box, _) = Open();
        try
        {
            Assert.Equal(CommandPaletteWindow.ReportMode.Automation, CommandPaletteWindow.Reporting);
            heard.Clear();

            box.Text = "go to";
            PumpPast(TimeSpan.FromMilliseconds(400));
            Press(window, Key.Down);
            Drain();

            Assert.Empty(heard);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (CommandPaletteWindow window, TextBox box, ListBox list) Open()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

        var registry = new CommandRegistry();
        foreach (var (id, category, title) in new[]
                 {
                     ("mail.archive",  "Mail", "Move to Archive"),
                     ("view.goFolder", "View", "Go to Folder"),
                     ("view.goDate",   "View", "Go to Date"),
                     ("mail.reply",    "Mail", "Reply"),
                 })
        {
            registry.Register(new CommandDefinition(id, category, title, execute: () => { }));
        }

        var window = new CommandPaletteWindow(registry)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();
        Drain();

        var box = window.FindName("FilterBox") as TextBox;
        Assert.NotNull(box);
        var list = window.FindName("CommandList") as ListBox;
        Assert.NotNull(list);

        // ShowActivated = false means no real keyboard focus arrives from the window manager;
        // take it explicitly so the focus assertions read the state the user would have.
        box!.Focus();
        Keyboard.Focus(box);
        Drain();

        return (window, box, list!);
    }

    private static void Press(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window);
        if (source is null) return;

        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(args);
    }

    /// <summary>Raises a left-button-down at the given row, as the palette's own handler sees it.</summary>
    private static void ClickDown(Window window, ListBoxItem row)
    {
        var source = PresentationSource.FromVisual(window);
        if (source is null) return;

        // Raised on the row itself so OriginalSource is the row, as a real hit-test would make it
        // the TextBlock inside the row's template — either way the handler walks up to the container.
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
        };
        row.RaiseEvent(args);
    }

    private static List<string> ItemPeerNames(ListBox list)
    {
        list.UpdateLayout();
        Drain();

        return (UIElementAutomationPeer.CreatePeerForElement(list).GetChildren() ?? [])
            .Select(p => p.GetName())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    // Pumps the dispatcher until a DispatcherTimer set for less than this has had time to tick.
    private static void PumpPast(TimeSpan delay)
    {
        var deadline = DateTime.UtcNow + delay;
        while (DateTime.UtcNow < deadline)
        {
            Drain();
            System.Threading.Thread.Sleep(20);
        }
        Drain();
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
