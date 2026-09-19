using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Alt pressed inside a message body opened the system menu (Restore, Move, Size…) instead of the
/// menu bar: the key goes to the WebView2, so WPF never sees it and Windows answers the resulting
/// SC_KEYMENU itself. The windows now catch that message and enter WPF's own menu mode.
/// </summary>
[Collection("WpfTests")]
public class MenuBarAccessTests
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU    = 0xF100;

    [Theory]
    [InlineData(0,   true,  '\0')]   // a lone Alt
    [InlineData('f', true,  'f')]    // Alt+F
    [InlineData(' ', false, ' ')]    // Alt+Space: the system menu, which keeps working
    public void RecognizesTheKeyboardMenuKey(int lParam, bool expected, char letter)
    {
        Assert.Equal(expected, MenuBarAccess.IsKeyboardMenuKey(WM_SYSCOMMAND, (IntPtr)SC_KEYMENU, (IntPtr)lParam, out var got));
        if (expected) Assert.Equal(letter, got);
    }

    [Fact]
    public void IgnoresEveryOtherSystemCommand()
    {
        const int SC_CLOSE = 0xF060;
        Assert.False(MenuBarAccess.IsKeyboardMenuKey(WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero, out _));
        Assert.False(MenuBarAccess.IsKeyboardMenuKey(0x0100, (IntPtr)SC_KEYMENU, IntPtr.Zero, out _));
    }

    [Theory]
    [InlineData("_File", 'F')]
    [InlineData("S_ettings…", 'e')]
    [InlineData("Save _As…", 'A')]
    [InlineData("A__B _C", 'C')]
    [InlineData("None", '\0')]
    [InlineData(null, '\0')]
    public void ReadsTheAccessKeyFromAHeader(string? header, char expected) =>
        Assert.Equal(expected, MenuBarAccess.AccessKey(header));

    private static (Window Window, Menu Menu, MenuItem File, MenuItem View) BuildWindow()
    {
        WpfTestHost.EnsureApplication();
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "_Save" });
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(new MenuItem { Header = "_Zoom" });
        var menu = new Menu { IsMainMenu = true };
        menu.Items.Add(file);
        menu.Items.Add(view);
        var panel = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        panel.Children.Add(menu);
        panel.Children.Add(new TextBox());
        var window = new Window
        {
            Content = panel, Width = 300, Height = 200, Left = -10000, Top = -10000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.Activate();
        Drain();
        return (window, menu, file, view);
    }

    [StaFact]
    public void ALoneAlt_EntersTheMenuBar_OnFile()
    {
        var (window, menu, file, _) = BuildWindow();
        try
        {
            MenuBarAccess.EnterMenuMode(menu);
            Drain();
            Assert.True(menu.IsKeyboardFocusWithin, "The menu bar should have focus.");
            Assert.Same(file, Keyboard.FocusedElement);
            // WPF's own menu mode, not merely a focused item: that is what makes Escape leave the menu.
            var isMenuMode = typeof(System.Windows.Controls.Primitives.MenuBase)
                .GetProperty("IsMenuMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(menu);
            Assert.Equal(true, isMenuMode);
        }
        finally { LeaveMenuMode(menu); window.Close(); }
    }

    [StaFact]
    public void AltAndALetter_OpensThatMenu()
    {
        var (window, menu, _, view) = BuildWindow();
        try
        {
            MenuBarAccess.OpenByAccessKey(menu, 'v');
            Drain();
            Assert.True(view.IsSubmenuOpen, "Alt+V should open the View menu.");
        }
        finally { LeaveMenuMode(menu); window.Close(); }
    }

    /// <summary>
    /// Menu mode is tracked process-wide by WPF's input manager; a window closed while in it leaves the
    /// next test starting from a menu already "entered". Leave it the way Escape would.
    /// </summary>
    private static void LeaveMenuMode(Menu menu)
    {
        foreach (var item in menu.Items) if (item is MenuItem m) m.IsSubmenuOpen = false;
        typeof(System.Windows.Controls.Primitives.MenuBase)
            .GetProperty("IsMenuMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(menu, false);
        Drain();
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
