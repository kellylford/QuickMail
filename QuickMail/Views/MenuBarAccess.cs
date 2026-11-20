using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Alt (and Alt+letter) pressed while focus is inside a message body. The keystroke goes to the
/// WebView2's own window, never to WPF, so WPF never enters menu mode; Windows turns the unhandled
/// Alt into SC_KEYMENU on the top-level window, and a WPF window has no Win32 menu for it to open —
/// so the system menu (Restore, Move, Size…) came up instead of the menu bar. The windows intercept
/// that SC_KEYMENU and call <see cref="EnterMenuMode"/> or <see cref="OpenByAccessKey"/> instead.
/// </summary>
internal static class MenuBarAccess
{
    /// <summary>
    /// Host script for both message bodies: Alt+letter or Alt+digit pressed inside the body, posted to
    /// the window as <c>alt-key:&lt;letter&gt;</c>. The browser engine keeps these keys — Alt+F opened
    /// nothing at all — so the window never saw them; relayed, they open the menu with that access key
    /// as they do from anywhere else. Alt+A is left to the relay that already takes it (the attachment
    /// list), and AltGr (Ctrl+Alt) is left alone, since on many keyboards it types characters.
    /// </summary>
    public const string AltKeyRelayScript =
        "window.addEventListener('keydown',function(e){" +
        "if(e.altKey&&!e.ctrlKey&&!e.metaKey&&!e.shiftKey&&e.key&&e.key.length===1&&/^[a-z0-9]$/i.test(e.key)" +
        "&&e.key.toLowerCase()!=='a'){window.chrome.webview.postMessage('alt-key:'+e.key.toLowerCase());e.preventDefault();}" +
        "});";

    /// <summary>The web message the script sends, before the letter.</summary>
    public const string AltKeyMessagePrefix = "alt-key:";

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU    = 0xF100;

    /// <summary>
    /// WPF's own handler for a lone Alt: the one the keyboard runs, so the menu bar behaves exactly as
    /// it does when Alt is pressed from any other control — File selected, arrows between menus, Escape
    /// to leave. It is private, hence the reflection; if a future WPF renames it, the fallback still
    /// puts focus on the first menu.
    /// </summary>
    private static readonly MethodInfo? OnEnterMenuMode =
        typeof(Menu).GetMethod("OnEnterMenuMode", BindingFlags.Instance | BindingFlags.NonPublic,
                               binder: null, types: [typeof(object), typeof(EventArgs)], modifiers: null);

    /// <summary>
    /// If this window message is the keyboard's menu key (Alt, or Alt+letter) rather than Alt+Space
    /// (the system menu, which keeps working as everywhere), returns true with the letter — '\0' for
    /// a lone Alt.
    /// </summary>
    public static bool IsKeyboardMenuKey(int msg, IntPtr wParam, IntPtr lParam, out char letter)
    {
        letter = '\0';
        if (msg != WM_SYSCOMMAND || (wParam.ToInt64() & 0xFFF0) != SC_KEYMENU) return false;
        letter = (char)(lParam.ToInt64() & 0xFFFF);
        return letter != ' ';
    }

    /// <summary>Enters menu mode on <paramref name="menu"/>, as a lone Alt does.</summary>
    public static void EnterMenuMode(Menu menu)
    {
        try
        {
            var source = PresentationSource.FromVisual(menu);
            if (OnEnterMenuMode is not null && source is not null
                && OnEnterMenuMode.Invoke(menu, [source, EventArgs.Empty]) is not false
                && menu.IsKeyboardFocusWithin)
                return;
        }
        catch (Exception ex)
        {
            LogService.Log($"MenuBarAccess: WPF menu mode could not be entered: {ex.GetType().Name}");
        }
        // Fallback: the first menu, focused.
        (menu.Items.OfType<MenuItem>().FirstOrDefault())?.Focus();
    }

    /// <summary>
    /// Alt+letter: opens the top-level menu whose access key is <paramref name="letter"/>, as it does
    /// from any other control. A letter that names no menu does nothing, as elsewhere. Returns whether
    /// a menu opened.
    /// </summary>
    public static bool OpenByAccessKey(Menu menu, char letter)
    {
        var item = FindByAccessKey(menu, letter);
        if (item is null) return false;
        EnterMenuMode(menu);
        item.Focus();
        OpenWithKeyboard(item);
        return true;
    }

    /// <summary>
    /// WPF's own "open this menu from the keyboard": opens the submenu AND puts focus on its first item,
    /// which is what Alt+F does everywhere in Windows. Setting IsSubmenuOpen alone opens the menu with
    /// focus left on its header, so the user hears "File" and has to press Down to reach anything.
    /// </summary>
    private static readonly MethodInfo? OpenSubmenuWithKeyboard =
        typeof(MenuItem).GetMethod("OpenSubmenuWithKeyboard", BindingFlags.Instance | BindingFlags.NonPublic,
                                   binder: null, types: Type.EmptyTypes, modifiers: null);

    private static void OpenWithKeyboard(MenuItem item)
    {
        try
        {
            if (OpenSubmenuWithKeyboard is not null)
            {
                OpenSubmenuWithKeyboard.Invoke(item, null);
                return;
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"MenuBarAccess: keyboard open failed: {ex.GetType().Name}");
        }
        // Fallback: open it, then focus the first item that can take focus once the popup has laid out.
        item.IsSubmenuOpen = true;
        item.Dispatcher.InvokeAsync(() =>
        {
            for (var i = 0; i < item.Items.Count; i++)
                if (item.ItemContainerGenerator.ContainerFromIndex(i) is MenuItem { IsEnabled: true, Focusable: true } first)
                {
                    first.Focus();
                    return;
                }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The top-level menu whose access key is <paramref name="letter"/>, or null.</summary>
    internal static MenuItem? FindByAccessKey(Menu menu, char letter) =>
        menu.Items.OfType<MenuItem>()
            .FirstOrDefault(m => char.ToUpperInvariant(AccessKey(m.Header as string)) == char.ToUpperInvariant(letter));

    /// <summary>The letter after the first single underscore in a header — "_File" is F, "S_ettings" is E.</summary>
    internal static char AccessKey(string? header)
    {
        if (string.IsNullOrEmpty(header)) return '\0';
        for (var i = 0; i < header.Length - 1; i++)
        {
            if (header[i] != '_') continue;
            if (header[i + 1] == '_') { i++; continue; }   // "__" is a literal underscore
            return header[i + 1];
        }
        return '\0';
    }
}
