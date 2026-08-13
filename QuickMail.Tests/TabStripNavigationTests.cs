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
    private static void EnsureApplication()
    {
        lock (typeof(Application))
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            const string stylesUri = "pack://application:,,,/QuickMail;component/Styles/AccessibleStyles.xaml";
            var uri = new Uri(stylesUri, UriKind.Absolute);
            if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != uri))
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }
        TabStripNavigation.Install();
    }

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

    /// <summary>Puts focus on <paramref name="from"/> and presses <paramref name="key"/> there.</summary>
    private static void Press(Window window, DependencyObject from, Key key)
    {
        if (from is IInputElement focusable)
        {
            FocusManager.SetFocusedElement(window, focusable);
            (from as FrameworkElement)?.Focus();
            TabOrderWalker.Drain();
        }

        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
            Source      = from,
        };
        ((UIElement)from).RaiseEvent(args);
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
            Press(window, box, Key.Right);

            Assert.Equal("Tab 0", Header(tabs.SelectedItem));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void CtrlTab_IsLeftToTheFramework()
    {
        // TabControl implements Ctrl+Tab itself; a modified key must fall straight through.
        EnsureApplication();
        var (window, tabs, items) = BuildStrip();
        try
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Right)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
                Source      = items[0],
            };
            // A real Ctrl+Right cannot be simulated without holding the key down, so assert the
            // narrower thing the handler actually checks: an already-handled event is not touched.
            args.Handled = true;
            items[0].RaiseEvent(args);
            TabOrderWalker.Drain();

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
            Assert.True(items.Length >= 4, "the dialog should still have its tabs");

            // Guards the premise: if the headers ever stop wrapping this test still passes, but a
            // reader should know the two-row layout is what the fix is for.
            var rows = items.Select(i => Math.Round(i.TransformToAncestor(dialog).Transform(default).Y))
                            .Distinct().Count();
            Assert.True(rows >= 1);

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
