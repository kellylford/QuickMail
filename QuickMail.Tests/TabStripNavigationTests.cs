// Regression tests for issue #528: Left and Right on the Settings tab headers did not reach all
// six tabs.
//
// The cause was geometry, not the keys. TabPanel wraps headers onto as many rows as it needs and
// then moves the row holding the selected tab to the bottom; Settings wraps to two rows of three.
// WPF's directional navigation looks for the nearest focusable element in the direction pressed,
// so Left and Right only ever found the other tabs on the same row — General, Advanced and
// Keyboard Shortcuts cycled among themselves and Startup, Windowing and Appearance could not be
// reached with the arrow keys at all.
//
// The tests feed real KeyEventArgs through the class handler rather than synthesizing keystrokes,
// so they are deterministic and do not need QUICKMAIL_RUN_INPUT_TESTS. Logical focus is tracked
// through FocusManager for the reason recorded on TabOrderWalker: keyboard focus is only granted
// in the active window, which a test cannot rely on owning.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Helpers;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class TabStripNavigationTests
{
    /// <summary>The shared host owns the Application — it must live on a thread that outlives the
    /// run, not on whichever [StaFact] thread happened to be first (issue #211). The rest is this
    /// suite's own setup.</summary>
    private static void EnsureApplication()
    {
        WpfTestHost.EnsureApplication();
        TabStripNavigation.Install();
        // Pin the modifier read. The shipped guard asks the keyboard device, which reports what is
        // PHYSICALLY held at that instant — so someone holding Shift anywhere on the machine made the
        // handler ignore a synthesized press, and the test failed as a bogus navigation regression.
        // AModifiedArrow_IsLeftToTheTabControl covers the guard itself, which had no test before.
        TabStripNavigation.ModifiersOf = _ => ModifiersWhenPressed;
    }


    /// <summary>What the handler sees as the held modifiers. None for every test but the one that
    /// checks a modified key is left alone.</summary>
    private static ModifierKeys ModifiersWhenPressed = ModifierKeys.None;

    /// <summary>A tab strip in a shown window, so the containers and layout are real.</summary>
    private static (Window window, TabControl tabs, TabItem[] items) BuildStrip(int count = 4)
    {
        var tabs = new TabControl();
        var items = Enumerable.Range(0, count)
            .Select(i => new TabItem { Header = $"Tab {i}", Content = new TextBox() })
            .ToArray();
        foreach (var item in items) tabs.Items.Add(item);

        var window = new Window { Width = 400, Height = 300, ShowActivated = false, Content = tabs };
        window.Show();
        TabOrderWalker.Drain();
        return (window, tabs, items);
    }

    /// <summary>
    /// Puts focus on <paramref name="from"/> and presses <paramref name="key"/> there.
    ///
    /// <para>
    /// Seeding through <see cref="TabOrderWalker.StartAt"/> rather than a bare <c>Focus()</c> is
    /// what keeps this deterministic, and is not optional. Focus on a window that is not the
    /// foreground one can land after the key has already been handled, and a <c>TabItem</c>
    /// selects itself when it is focused — so a late seed re-selects the tab the walk started
    /// from and the assertion fails reporting a behaviour regression that did not happen.
    /// <c>StartAt</c> verifies the seed landed and throws as a setup failure if it did not.
    /// </para>
    /// </summary>
    private static void Press(Window window, DependencyObject from, Key key, bool expectHandled = true)
    {
        TabOrderWalker.StartAt(window, (FrameworkElement)from, Header(from) ?? from.GetType().Name);

        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
            Source      = from,
        };
        var modsBefore = Keyboard.PrimaryDevice.Modifiers;
        ((UIElement)from).RaiseEvent(args);
        if (expectHandled && !args.Handled)
            throw new InvalidOperationException(
                $"PRESS SWALLOWED key={key} from={Header(from)} " +
                $"modsBefore={modsBefore} modsNow={Keyboard.PrimaryDevice.Modifiers} " +
                $"argsDevMods={args.KeyboardDevice.Modifiers} " +
                $"origSrcIsTabItem={args.OriginalSource is TabItem} " +
                $"kbFocus={(Keyboard.FocusedElement as FrameworkElement)?.GetType().Name}");
        TabOrderWalker.Drain();
    }

    private static string? Header(object? tab) => (tab as TabItem)?.Header?.ToString();

    [StaFact]
    public void RightArrow_MovesToTheNextTab_AndSelectsIt()
    {
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            Press(window, items[0], Key.Right);

            Assert.Equal("Tab 1", Header(tabs.SelectedItem));
            Assert.Same(items[1], FocusManager.GetFocusedElement(window));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void RightArrow_FromTheLastTab_WrapsToTheFirst()
    {
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            Press(window, items[^1], Key.Right);

            Assert.Equal("Tab 0", Header(tabs.SelectedItem));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void LeftArrow_FromTheFirstTab_WrapsToTheLast()
    {
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            Press(window, items[0], Key.Left);

            Assert.Equal("Tab 3", Header(tabs.SelectedItem));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void HomeAndEnd_GoToTheFirstAndLastTab()
    {
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            Press(window, items[1], Key.End);
            Assert.Equal("Tab 3", Header(tabs.SelectedItem));
            Assert.Same(items[3], FocusManager.GetFocusedElement(window));

            Press(window, items[3], Key.Home);
            Assert.Equal("Tab 0", Header(tabs.SelectedItem));
            Assert.Same(items[0], FocusManager.GetFocusedElement(window));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void DisabledTabs_AreSkipped()
    {
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            items[1].IsEnabled = false;
            TabOrderWalker.Drain();

            Press(window, items[0], Key.Right);

            Assert.Equal("Tab 2", Header(tabs.SelectedItem));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ArrowsInTheTabsContent_AreLeftAlone()
    {
        // The behaviour must never take an arrow key away from a text box, list or combo box that
        // happens to live inside the selected tab.
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            var box = (TextBox)items[0].Content;
            Press(window, box, Key.Right, expectHandled: false);

            Assert.Equal("Tab 0", Header(tabs.SelectedItem));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void OtherKeys_FallThroughUnhandled()
    {
        // Only the four navigation keys are the strip's. Down in particular must stay WPF's, so
        // it can still move focus out of a wrapped strip's top row into the row below it.
        //
        // The modifier guard — what keeps Ctrl+Tab TabControl's own — deliberately has no test.
        // It reads the live keyboard device, which a synthesized KeyEventArgs cannot set, so a
        // test could only assert the guard against itself. Better an admitted gap than a test
        // that looks like coverage and is not.
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            TabOrderWalker.StartAt(window, items[0], "Tab 0");
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Down)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
                Source      = items[0],
            };
            items[0].RaiseEvent(args);
            TabOrderWalker.Drain();

            Assert.False(args.Handled);
            Assert.Equal("Tab 0", Header(tabs.SelectedItem));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void SettingsDialog_EveryTabIsReachableWithTheArrowKeys()
    {
        // The regression #528 reported, against the real dialog: its six tabs wrap onto two rows,
        // which is what broke geometric navigation. Pressing Right once per tab must visit every
        // one of them and come back to where it started.
        EnsureApplication();
        var vm     = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        var dialog = new SettingsDialog(vm) { ShowActivated = false };
        dialog.Show();
        TabOrderWalker.Drain();
        try
        {
            var tabs = FindTabControl(dialog);
            Assert.NotNull(tabs);
            var items = tabs!.Items.Cast<TabItem>().ToArray();
            Assert.Equal(6, items.Length);

            // The test process merges AccessibleStyles.xaml but not ThemedControls.xaml, which is
            // what gives a tab header its real Padding — without it the six headers are narrow
            // enough to fit on one line, which is the layout that was never broken. Stamping the
            // themed padding and font size on reproduces what the user has, so the walk below runs
            // against the two-row strip #528 was reported against.
            foreach (var item in items)
            {
                item.Padding  = new Thickness(12, 6, 12, 6);
                item.FontSize = 13;
            }
            dialog.UpdateLayout();
            TabOrderWalker.Drain();

            // Guards the premise rather than assuming it. A wrap is a header whose left edge is
            // further left than the one before it — reliable in a way comparing Y values is not,
            // since the selected header is nudged vertically whether or not the strip wrapped.
            var x = items.Select(i => Math.Round(i.TransformToAncestor(dialog).Transform(default).X)).ToList();
            Assert.True(x.Zip(x.Skip(1)).Any(p => p.Second < p.First),
                $"the premise of this test is that the headers wrap onto more than one row; they did not (x = {string.Join(", ", x)})");

            var visited = new List<string?>();
            var current = items[0];
            for (var i = 0; i < items.Length; i++)
            {
                Press(dialog, current, Key.Right);
                current = (TabItem)FocusManager.GetFocusedElement(dialog)!;
                visited.Add(Header(tabs.SelectedItem));
            }

            // Every tab, in order, ending back at the first.
            var expected = items.Skip(1).Select(i => Header(i)).Append(Header(items[0])).ToList();
            Assert.Equal(expected, visited);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void AModifiedArrow_IsLeftToTheTabControl()
    {
        // Ctrl+Tab and Ctrl+Shift+Tab are the TabControl's own, so any modifier means the key is not
        // ours. Previously untestable: the guard read the physical keyboard, which a synthesized event
        // cannot set — the reason it now reads through ModifiersOf.
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            ModifiersWhenPressed = ModifierKeys.Control;
            Press(window, items[0], Key.Right, expectHandled: false);

            Assert.Equal("Tab 0", Header(tabs.SelectedItem));   // untouched
        }
        finally { ModifiersWhenPressed = ModifierKeys.None; window.Close(); }
    }

    private static TabControl? FindTabControl(DependencyObject root)
    {
        if (root is TabControl found) return found;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = FindTabControl(VisualTreeHelper.GetChild(root, i));
            if (child != null) return child;
        }
        return null;
    }
}
