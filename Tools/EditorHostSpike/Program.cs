// Stage 1 of #1099: a compose-shaped WPF window whose message body is WinUI 3's RichEditBox,
// hosted the way QuickMail would host it (WinUI XAML Islands, DesktopWindowXamlSource). It tests
// the hard parts: focus in and out with Tab, Shift+Tab, F6 and Alt+keys; menus and Ctrl+Enter
// while the body has focus; Escape; focus coming back after a dialog; and packaging.
// Follows Microsoft's Islands sample (WindowsAppSDK-Samples, Samples/Islands/cs-winforms-unpackaged).
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.UI.Dispatching;

namespace EditorHostSpike;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Velopack.VelopackApp.Build().Run();

        // The Windows App SDK's UI stack needs a dispatcher queue on this thread, and a WinUI
        // Application object for its controls, resources and metadata.
        var controller = DispatcherQueueController.CreateOnCurrentThread();
        var xamlApp = new XamlApp();

        // The Windows App SDK must see every message before the host's loop does.
        ComponentDispatcher.ThreadFilterMessage += (ref MSG msg, ref bool handled) =>
        {
            if (handled) return;
            var m = new ContentMsg { hwnd = msg.hwnd, message = (uint)msg.message, wParam = msg.wParam, lParam = msg.lParam };
            if (ContentPreTranslateMessage(ref m)) handled = true;
        };

        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
        app.Run(new ComposeWindow());
        controller.ShutdownQueue();
        GC.KeepAlive(xamlApp);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ContentMsg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time, ptX, ptY;
    }

    [DllImport("Microsoft.UI.Windowing.Core.dll")]
    static extern bool ContentPreTranslateMessage(ref ContentMsg message);
}

/// <summary>The WinUI application object an island needs: control metadata and styles.</summary>
sealed class XamlApp : Microsoft.UI.Xaml.Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider _controls = new();
    readonly Microsoft.UI.Xaml.Hosting.WindowsXamlManager _manager;

    public XamlApp()
    {
        _manager = Microsoft.UI.Xaml.Hosting.WindowsXamlManager.InitializeForCurrentThread();
        Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
    }

    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => _controls.GetXamlType(type);
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string fullName) => _controls.GetXamlType(fullName);
    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => _controls.GetXmlnsDefinitions();
}
