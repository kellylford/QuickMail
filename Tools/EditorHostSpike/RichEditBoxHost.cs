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

    /// <summary>Puts focus in the editor, for Alt+Y and for returning after a dialog.</summary>
    public void FocusEditor() => Navigate(XamlSourceFocusNavigationReason.Programmatic);

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
