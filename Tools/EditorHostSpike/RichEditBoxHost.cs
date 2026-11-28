using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace EditorHostSpike;

/// <summary>
/// A WPF element whose content is a WinUI 3 RichEditBox in a XAML island. The island's window is a
/// child of a plain container window this HwndHost owns. Focus is handed across both ways: into the
/// island with NavigateFocus (Tab, Shift+Tab, or WPF giving this element focus, as Alt+Y does), and
/// out of it on TakeFocusRequested (Tab or Shift+Tab from the editor).
/// </summary>
sealed class RichEditBoxHost : HwndHost
{
    IntPtr _container;
    DesktopWindowXamlSource? _source;

    public RichEditBox Editor { get; }

    public RichEditBoxHost(string accessibleName)
    {
        Editor = new RichEditBox { AcceptsReturn = true, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        AutomationProperties.SetName(Editor, accessibleName);
        Focusable = true;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _source = new DesktopWindowXamlSource();
        _source.Initialize(new WindowId((ulong)_container));
        _source.Content = Editor;
        _source.TakeFocusRequested += (_, e) =>
        {
            var direction = e.Request.Reason == XamlSourceFocusNavigationReason.Last
                ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
            Dispatcher.BeginInvoke(() => MoveFocus(new TraversalRequest(direction)));
        };
        Resize();
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _source?.Dispose();
        _source = null;
        DestroyWindow(hwnd.Handle);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Resize();
    }

    void Resize()
    {
        if (_source == null) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _source.SiteBridge.MoveAndResize(new Windows.Graphics.RectInt32(0, 0,
            Math.Max(1, (int)(ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(ActualHeight * dpi.DpiScaleY))));
    }

    /// <summary>Puts focus in the editor, for Alt+Y, F6 and for returning after a dialog.</summary>
    /// <remarks>
    /// Deferred, so it runs after WPF has finished moving its own focus to this element (which
    /// puts Win32 focus on the container window). NavigateFocus(Programmatic) alone left focus on
    /// the island's pane, not the editor, so the editor is focused by name.
    /// </remarks>
    public void FocusEditor() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
    {
        if (_source == null) return;
        // Win32 focus first: after a dialog closes, WinUI still counts the editor as focused, so
        // Editor.Focus alone does nothing while Win32 focus sits on the container window.
        SetFocus(Win32Interop.GetWindowFromWindowId(_source.SiteBridge.WindowId));
        if (!Editor.Focus(Microsoft.UI.Xaml.FocusState.Keyboard))
            Navigate(XamlSourceFocusNavigationReason.First);
    });

    /// <summary>
    /// Whenever Win32 focus arrives on the container (WPF restoring focus after a dialog or when
    /// the window is activated again), pass it on to the editor. The container itself never
    /// should hold focus: a screen reader would be left on a nameless pane.
    /// </summary>
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETFOCUS && hwnd == _container) FocusEditor();
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    const int WM_SETFOCUS = 0x0007;

    [DllImport("user32")]
    static extern IntPtr SetFocus(IntPtr hwnd);

    /// <summary>
    /// Typed characters belong to the editor. Without this, WPF offers every WM_CHAR to its access
    /// keys first, so typing "t" in the body moved focus to the To field ("_To:"). Alt+letter
    /// (WM_SYSCHAR) is left alone, so access keys and menus still work from the body.
    /// </summary>
    protected override bool TranslateCharCore(ref MSG msg, ModifierKeys modifiers)
    {
        if (msg.message is WM_CHAR or WM_DEADCHAR)
        {
            DispatchMessage(ref msg);
            return true;
        }
        return base.TranslateCharCore(ref msg, modifiers);
    }

    /// <summary>
    /// The caret keys belong to the editor, with any modifier. WPF raises its own key events
    /// before the hosted window gets the message, and its keyboard navigation takes Ctrl+arrow as
    /// "move focus that way": Ctrl+Down in the body moved focus to the Send button, and the next
    /// one opened the menu. By the time the event bubbles to here the window's PreviewKeyDown
    /// (QuickMail's command keys) has had its turn, so the message goes straight to the editor.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool caretKey = key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.PageUp or Key.PageDown;
        if (caretKey && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            var msg = ComponentDispatcher.CurrentKeyboardMessage;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    [DllImport("user32")]
    static extern bool TranslateMessage(ref MSG msg);

    const int WM_CHAR = 0x0102, WM_DEADCHAR = 0x0103;

    [DllImport("user32")]
    static extern IntPtr DispatchMessage(ref MSG msg);

    void Navigate(XamlSourceFocusNavigationReason reason)
    {
        if (_source == null) return;
        _source.NavigateFocus(new XamlSourceFocusNavigationRequest(reason));
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        Navigate(request.FocusNavigationDirection == FocusNavigationDirection.Previous
            ? XamlSourceFocusNavigationReason.Last : XamlSourceFocusNavigationReason.First);
        return true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (e.NewFocus == this) FocusEditor();
    }

    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;

    [DllImport("user32", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32")]
    static extern bool DestroyWindow(IntPtr hwnd);
}
