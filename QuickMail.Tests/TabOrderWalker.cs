using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace QuickMail.Tests;

/// <summary>
/// Walks WPF's real Tab traversal for a window, deterministically.
///
/// The tab-order tests originally tracked <see cref="Keyboard.FocusedElement"/> and seeded it with
/// <c>Keyboard.Focus(...)</c>. That made them flaky — roughly one run in three or four — for a
/// reason worth recording: WPF grants <em>keyboard</em> focus only within the <em>active</em>
/// window, and these windows are shown with <c>ShowActivated = false</c>. Whether the request took
/// effect therefore depended on what else on the machine happened to hold the foreground at that
/// instant. When it didn't take, <c>Keyboard.FocusedElement</c> was some other element or null, the
/// walk started from the wrong place, and the test failed reporting a bogus tab order — which reads
/// like a real regression and sends you looking in the wrong place.
///
/// Tracking <em>logical</em> focus through <see cref="FocusManager"/> removes the dependency
/// entirely: logical focus is per focus scope and does not require the window to be active, while
/// <see cref="UIElement.MoveFocus"/> still performs the same real traversal WPF uses for Tab. That
/// is what these tests actually mean to measure — where Tab goes — not which window Windows
/// currently considers foreground.
/// </summary>
internal static class TabOrderWalker
{
    /// <summary>Lets the dispatcher settle so focus and layout changes are observable.</summary>
    public static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Puts logical focus on <paramref name="start"/> and verifies it landed, so a walk can never
    /// silently begin somewhere else.
    /// </summary>
    public static void StartAt(Window window, FrameworkElement start, string name)
    {
        FocusManager.SetFocusedElement(window, start);
        start.Focus();
        Drain();

        var actual = FocusManager.GetFocusedElement(window);
        if (!ReferenceEquals(actual, start))
        {
            throw new InvalidOperationException(
                $"Could not put focus on '{name}' to begin the walk; focus is on " +
                $"'{(actual as FrameworkElement)?.Name ?? actual?.GetType().Name ?? "nothing"}'. " +
                "The tab order was not measured, so this is a test-setup failure, not a tab-order regression.");
        }
    }

    /// <summary>
    /// Tabs forward from the current focus, recording each stop, until the order wraps or
    /// <paramref name="maxStops"/> is reached.
    /// </summary>
    public static List<string> Walk(Window window, int maxStops = 50)
    {
        var stops = new List<string>();
        var seen = new HashSet<FrameworkElement>();

        for (var i = 0; i < maxStops; i++)
        {
            if (FocusManager.GetFocusedElement(window) is not UIElement current) break;
            if (!current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) break;
            Drain();

            if (FocusManager.GetFocusedElement(window) is not FrameworkElement next) break;
            if (!seen.Add(next)) break;      // wrapped around
            stops.Add(Describe(next));
        }

        return stops;
    }

    /// <summary>Names a focus stop the way a reader of the assertion message would name it.</summary>
    public static string Describe(FrameworkElement fe)
    {
        // A list is entered at its ITEM, never at the container, so report an item stop under the
        // owning list's name — for tab order, that stop IS the list.
        if (ItemsControl.ItemsControlFromItemContainer(fe) is FrameworkElement owner
            && !string.IsNullOrEmpty(owner.Name))
            return owner.Name;
        if (!string.IsNullOrEmpty(fe.Name)) return fe.Name;
        var auto = AutomationProperties.GetName(fe);
        if (!string.IsNullOrEmpty(auto)) return auto;
        if (fe is ContentControl { Content: string s }) return s;
        return fe.GetType().Name;
    }
}
