using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.UI.Dispatching;

namespace QuickMail.Controls;

/// <summary>
/// Starts the WinUI side of the process the first time a <see cref="WinUiEditorHost"/> is made
/// (#1099): a dispatcher queue on the UI thread, a WinUI <c>Application</c> for its controls'
/// styles and metadata, and a message filter that shows the Windows App SDK every message before
/// WPF handles it. Nothing here runs while the new editor is turned off.
/// Follows Microsoft's Islands sample (WindowsAppSDK-Samples, Samples/Islands).
/// </summary>
internal static class WinUiRuntime
{
    private static DispatcherQueueController? _controller;
    private static XamlApp? _app;

    /// <summary>Call on the UI thread before creating any WinUI object.</summary>
    public static void EnsureStarted()
    {
        if (_app != null) return;
        _controller = DispatcherQueueController.CreateOnCurrentThread();
        _app = new XamlApp();
        ComponentDispatcher.ThreadFilterMessage += FilterMessage;
    }

    /// <summary>At app exit. Safe to call when it was never started.</summary>
    public static void Shutdown()
    {
        if (_app == null) return;
        ComponentDispatcher.ThreadFilterMessage -= FilterMessage;
        try { _controller?.ShutdownQueue(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* the process is ending */ }
        _controller = null;
        _app = null;
    }

    private static void FilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled) return;
        var m = new ContentMsg { hwnd = msg.hwnd, message = (uint)msg.message, wParam = msg.wParam, lParam = msg.lParam };
        if (ContentPreTranslateMessage(ref m)) handled = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ContentMsg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time, ptX, ptY;
    }

    [DllImport("Microsoft.UI.Windowing.Core.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ContentPreTranslateMessage(ref ContentMsg message);

    /// <summary>The WinUI application object an island needs: control metadata and styles.</summary>
    private sealed class XamlApp : Microsoft.UI.Xaml.Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
    {
        private readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider _controls = new();
#pragma warning disable IDE0052 // kept alive for the life of the process: it is what holds WinUI's XAML framework up on this thread
        private readonly Microsoft.UI.Xaml.Hosting.WindowsXamlManager _manager;
#pragma warning restore IDE0052

        public XamlApp()
        {
            _manager = Microsoft.UI.Xaml.Hosting.WindowsXamlManager.InitializeForCurrentThread();
            Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        }

        public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => _controls.GetXamlType(type);
        public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string fullName) => _controls.GetXamlType(fullName);
        public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => _controls.GetXmlnsDefinitions();
    }
}
