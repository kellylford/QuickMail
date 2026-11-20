using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
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

    [StaFact]
    public void ALetterThatNamesNoMenu_DoesNothing()
    {
        var (window, menu, file, view) = BuildWindow();
        try
        {
            Assert.False(MenuBarAccess.OpenByAccessKey(menu, 'q'));
            Drain();
            Assert.False(file.IsSubmenuOpen);
            Assert.False(view.IsSubmenuOpen);
            Assert.False(menu.IsKeyboardFocusWithin);
        }
        finally { LeaveMenuMode(menu); window.Close(); }
    }

    // ── The real menu bars ────────────────────────────────────────────────────

    /// <summary>The top-level menu headers of a window's main menu, read from its XAML.</summary>
    private static List<string> TopLevelMenus(string xamlFile)
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", xamlFile));
        var start = xaml.IndexOf("IsMainMenu=\"True\"", StringComparison.Ordinal);
        if (start < 0) start = xaml.IndexOf("x:Name=\"MainMenuBar\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{xamlFile}: no main menu found.");
        var menuStart = xaml.LastIndexOf("<Menu", start, StringComparison.Ordinal);
        var menuEnd = xaml.IndexOf("</Menu>", start, StringComparison.Ordinal);
        var menu = xaml[menuStart..menuEnd];
        // Top-level items are the menu's direct children: the shallowest MenuItem indentation.
        var items = Regex.Matches(menu, @"\n(?<indent>[ ]+)<MenuItem\s+Header=""(?<h>[^""]+)""")
            .Select(m => (Indent: m.Groups["indent"].Value.Length, Header: m.Groups["h"].Value)).ToList();
        var top = items.Min(i => i.Indent);
        return items.Where(i => i.Indent == top).Select(i => i.Header).ToList();
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("MessageWindow.xaml")]
    public void EveryTopLevelMenu_HasAUniqueAccessKey_ThatAltCanReachFromAMessage(string xamlFile)
    {
        var headers = TopLevelMenus(xamlFile);
        Assert.NotEmpty(headers);
        var keys = headers.Select(h => (Header: h, Key: char.ToUpperInvariant(MenuBarAccess.AccessKey(h)))).ToList();

        Assert.All(keys, k => Assert.True(k.Key != '\0', $"{xamlFile}: menu \"{k.Header}\" has no access key."));
        var dupes = keys.GroupBy(k => k.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"{xamlFile}: access keys used twice: {string.Join(", ", dupes)}");
        // Alt+A is the attachment list, from inside a message; a menu on A could never be opened from there.
        Assert.DoesNotContain(keys, k => k.Key == 'A');
        // Every one is a key the relay carries: a letter or digit.
        Assert.All(keys, k => Assert.True(char.IsLetterOrDigit(k.Key), $"{xamlFile}: \"{k.Header}\" uses '{k.Key}', which the relay does not carry."));
    }

    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void EveryMessageBody_RelaysAltAndALetter_AndHandlesIt(string file)
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", file));
        Assert.Contains("MenuBarAccess.AltKeyRelayScript", code);
        Assert.Contains("MenuBarAccess.AltKeyMessagePrefix", code);
        Assert.Contains("OnAltKeyFromBody", code);
        Assert.Contains("AddHook(OnWmKeyMenu)", code);   // the lone Alt
    }

    /// <summary>
    /// The step that failed for real: a real WebView2 must hand Alt+F to the relay script, and the relay
    /// must leave alone what is not a menu key — Alt+A (the attachment list's own relay), AltGr, Alt+Shift.
    /// </summary>
    [StaFact]
    public void ARealWebView2_RelaysAltAndALetter()
    {
        WpfTestHost.EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailAlt-{Guid.NewGuid():N}");
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 300, Height = 200, Left = -10000, Top = -10000,
        };
        window.Show();
        CoreWebView2Controller? controller = null;
        var got = new List<string>();
        try
        {
            RunAsync(async () =>
            {
                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).Handle);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 300, 200);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(MenuBarAccess.AltKeyRelayScript);
                core.WebMessageReceived += (_, e) => { lock (got) got.Add(e.TryGetWebMessageAsString()); };
                var done = new TaskCompletionSource<bool>();
                core.NavigationCompleted += (_, _) => done.TrySetResult(true);
                core.NavigateToString("<p>A message body</p>");
                await done.Task;
                controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

                // CDP modifiers: 1 Alt, 2 Ctrl, 8 Shift.
                async Task Press(string key, int vk, int modifiers)
                {
                    var code = char.IsDigit(key[0]) ? "Digit" + key : "Key" + key.ToUpperInvariant();
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                        $"{{\"type\":\"rawKeyDown\",\"modifiers\":{modifiers},\"key\":\"{key}\",\"code\":\"{code}\",\"windowsVirtualKeyCode\":{vk}}}");
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                        $"{{\"type\":\"keyUp\",\"modifiers\":{modifiers},\"key\":\"{key}\",\"code\":\"{code}\",\"windowsVirtualKeyCode\":{vk}}}");
                }
                await Press("f", 70, 1);       // Alt+F: relayed
                await Press("m", 77, 1);       // Alt+M: relayed
                await Press("a", 65, 1);       // Alt+A: not this relay's
                await Press("f", 70, 1 | 2);   // Ctrl+Alt+F (AltGr): not relayed
                await Press("f", 70, 1 | 8);   // Alt+Shift+F: not relayed
                await Press("f", 70, 0);       // plain F: not relayed
                var until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until) { lock (got) { if (got.Count >= 2) break; } await Task.Delay(50); }
                await Task.Delay(300);         // anything wrongly relayed has time to arrive too
            }, TimeSpan.FromSeconds(60));

            lock (got) Assert.Equal(new[] { "alt-key:f", "alt-key:m" }, got);
        }
        finally
        {
            controller?.Close();
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* WebView2 may still hold it */ }
        }
    }

    private static void RunAsync(Func<Task> operation, TimeSpan timeout)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = operation();
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            var timer = new DispatcherTimer(timeout, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Assert.True(task.IsCompleted, "Timed out driving the WebView2.");
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "QuickMail", "Views"))) return d.FullName;
        throw new InvalidOperationException("Repo source tree not found.");
    }

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
    public void AltAndALetter_OpensThatMenu_OnItsFirstItem()
    {
        var (window, menu, _, view) = BuildWindow();
        try
        {
            MenuBarAccess.OpenByAccessKey(menu, 'v');
            Drain();
            Assert.True(view.IsSubmenuOpen, "Alt+V should open the View menu.");
            // As Alt+V does everywhere in Windows: focus on the menu's first item, not on its header.
            var first = view.ItemContainerGenerator.ContainerFromIndex(0) as MenuItem;
            Assert.NotNull(first);
            Assert.Same(first, Keyboard.FocusedElement);
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
