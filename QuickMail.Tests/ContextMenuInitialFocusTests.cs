// Issue #693: a context menu opened from the keyboard took focus twice, first on the menu itself
// and then on its first item, so a screen reader was cut off and a braille display flickered.
// ContextMenuInitialFocus delegates the menu's own focus request to the item, so focus changes once.
//
// The first group runs everywhere. It asserts through the menu's focus scope, which records the
// item the menu opened on whether or not the test process holds the foreground (see the long
// comment in AddressBookFilterMenuTests). The input-gated group asserts real keyboard focus, which
// needs an active, foreground window: that is the claim the issue is about, that the menu itself
// never takes keyboard focus on the way to the item.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ContextMenuInitialFocusTests
{
    [StaFact]
    public void FromTheKeyboard_TheMenuOpensOnItsFirstItem()
    {
        using var fx = new MenuFixture(fromKeyboard: true);
        fx.Open();

        Assert.Same(fx.Reply, FocusManager.GetFocusedElement(fx.Menu));
    }

    [StaFact]
    public void FromTheMouse_NoItemIsChosen()
    {
        using var fx = new MenuFixture(fromKeyboard: false);
        fx.Open();

        // WPF's own behaviour, kept for the mouse: the menu is open with nothing highlighted.
        Assert.IsNotType<MenuItem>(FocusManager.GetFocusedElement(fx.Menu));
    }

    [StaFact]
    public void ADisabledFirstItem_AndSeparators_AreSkipped()
    {
        using var fx = new MenuFixture(fromKeyboard: true, firstItemEnabled: false);
        fx.Open();

        // The item Down would have reached: past the disabled one and the separator.
        Assert.Same(fx.Forward, FocusManager.GetFocusedElement(fx.Menu));
    }

    [StaFact]
    public void AMenuCanChooseTheItemItOpensOn()
    {
        using var fx = new MenuFixture(fromKeyboard: true);
        ContextMenuInitialFocus.SetInitialItem(fx.Menu, _ => fx.Forward);
        fx.Open();

        Assert.Same(fx.Forward, FocusManager.GetFocusedElement(fx.Menu));
    }

    [StaFact]
    public void AChosenItemThatCannotTakeFocus_FallsBackToTheFirst()
    {
        using var fx = new MenuFixture(fromKeyboard: true);
        fx.Forward.IsEnabled = false;
        ContextMenuInitialFocus.SetInitialItem(fx.Menu, _ => fx.Forward);
        fx.Open();

        Assert.Same(fx.Reply, FocusManager.GetFocusedElement(fx.Menu));
    }

    [StaFact]
    public void AMenuWithNoItemThatCanTakeFocus_KeepsFocusItself()
    {
        using var fx = new MenuFixture(fromKeyboard: true);
        foreach (var item in fx.Menu.Items.OfType<MenuItem>())
            item.IsEnabled = false;
        fx.Open();

        Assert.IsNotType<MenuItem>(FocusManager.GetFocusedElement(fx.Menu));
    }

    /// <summary>
    /// The issue itself: with real keyboard focus, opening the menu moves focus once, from the
    /// list straight to the first item. The menu never holds keyboard focus on the way. Needs a
    /// foreground window, so it is an input test; in a background process keyboard focus cannot
    /// enter the popup at all and the test would prove nothing.
    /// </summary>
    [StaFact(Skip = InputTests.SkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void FromTheKeyboard_FocusMovesOnce_StraightToTheFirstItem()
    {
        using var fx = new MenuFixture(fromKeyboard: true);
        Keyboard.Focus(fx.Row);
        MenuFixture.DoEvents();
        Assert.Same(fx.Row, Keyboard.FocusedElement);   // setup: the window really has focus

        var gained = new List<object?>();
        void Record(object? s, ProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is KeyboardFocusChangedEventArgs k
                && k.RoutedEvent == Keyboard.GotKeyboardFocusEvent)
                gained.Add(k.NewFocus);
        }
        InputManager.Current.PostProcessInput += Record;
        try
        {
            fx.Open();
        }
        finally { InputManager.Current.PostProcessInput -= Record; }

        Assert.Same(fx.Reply, Keyboard.FocusedElement);
        Assert.Equal(new object?[] { fx.Reply }, gained);
    }

    /// <summary>
    /// Delegating the menu's focus request must not lose what the menu remembers to go back to:
    /// closing it returns focus to the row it was opened from, as before.
    /// </summary>
    [StaFact(Skip = InputTests.SkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ClosingTheMenu_ReturnsFocusToWhereItWasOpened()
    {
        using var fx = new MenuFixture(fromKeyboard: true);
        Keyboard.Focus(fx.Row);
        MenuFixture.DoEvents();
        Assert.Same(fx.Row, Keyboard.FocusedElement);

        fx.Open();
        Assert.Same(fx.Reply, Keyboard.FocusedElement);

        fx.Menu.IsOpen = false;
        Assert.True(MenuFixture.PumpUntil(() => ReferenceEquals(Keyboard.FocusedElement, fx.Row)),
            $"Focus did not return to the row; it is on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}.");
    }

    /// <summary>
    /// A window with one list row whose context menu reads Reply (optionally disabled), a separator,
    /// then Forward and Delete: the shape of the message menu's opening items.
    /// </summary>
    private sealed class MenuFixture : IDisposable
    {
        private readonly Func<bool> _savedPredicate;
        private readonly Window _window;

        public ContextMenu Menu { get; }
        public MenuItem Reply { get; }
        public MenuItem Forward { get; }
        public ListBoxItem Row { get; }

        public MenuFixture(bool fromKeyboard, bool firstItemEnabled = true)
        {
            WpfTestHost.EnsureApplication();
            ContextMenuInitialFocus.Install();
            _savedPredicate = ContextMenuInitialFocus.OpenedFromKeyboard;
            ContextMenuInitialFocus.OpenedFromKeyboard = () => fromKeyboard;

            Reply   = new MenuItem { Header = "_Reply", IsEnabled = firstItemEnabled };
            Forward = new MenuItem { Header = "_Forward" };
            Menu = new ContextMenu();
            Menu.Items.Add(Reply);
            Menu.Items.Add(new Separator());
            Menu.Items.Add(Forward);
            Menu.Items.Add(new MenuItem { Header = "_Delete" });

            Row = new ListBoxItem { Content = "A message" };
            var list = new ListBox { ContextMenu = Menu };
            list.Items.Add(Row);

            _window = new Window
            {
                Content = list, Width = 300, Height = 200, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            _window.Show();
            _window.Activate();
            _window.UpdateLayout();
            _ = PumpUntil(() => _window.IsActive);
        }

        public void Open()
        {
            Menu.PlacementTarget = Row;
            Menu.IsOpen = true;
            Assert.True(PumpUntil(() => Menu.IsOpen
                    && Menu.ItemContainerGenerator.Status
                       == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated),
                "The menu did not open with its items generated within 5 seconds.");
        }

        public void Dispose()
        {
            try
            {
                Menu.IsOpen = false;
                DoEvents();
                _window.Close();
            }
            finally { ContextMenuInitialFocus.OpenedFromKeyboard = _savedPredicate; }
        }

        public static bool PumpUntil(Func<bool> condition)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            DoEvents();
            while (!condition())
            {
                if (clock.Elapsed >= TimeSpan.FromSeconds(5)) return false;
                System.Threading.Thread.Sleep(10);
                DoEvents();
            }
            return true;
        }

        public static void DoEvents()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
}
