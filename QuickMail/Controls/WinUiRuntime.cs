using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.UI.Dispatching;

namespace QuickMail.Controls;

/// <summary>
/// Starts the WinUI side of the process the first time a <see cref="WinUiEditorHost"/> is made
/// (#1099): a dispatcher queue on the UI thread, a WinUI <c>Application</c> for its controls'
/// styles and metadata, and a message filter that shows the Windows App SDK every message before
/// WPF handles it. Nothing here runs while the new editor is turned off, and no Windows App SDK
/// code is loaded: the SDK's own module initializer is switched off in Directory.Build.props, and
/// what it did is done here instead (<see cref="LoadWindowsAppRuntime"/>).
/// Follows Microsoft's Islands sample (WindowsAppSDK-Samples, Samples/Islands).
/// </summary>
internal static class WinUiRuntime
{
    private static DispatcherQueueController? _controller;
    private static XamlApp? _app;
    private static bool _runtimeLoaded;

    /// <summary>
    /// True once <see cref="EnsureStarted"/> has run. App.OnExit asks this before calling
    /// <see cref="Shutdown"/>, so that with the new editor off not even Shutdown is compiled,
    /// which would load the Windows App SDK's assemblies to resolve what it calls.
    /// </summary>
    public static bool IsStarted { get; private set; }

    /// <summary>Call on the UI thread before creating any WinUI object.</summary>
    public static void EnsureStarted()
    {
        if (_app != null) return;
        LoadWindowsAppRuntime();
        _controller ??= DispatcherQueueController.CreateOnCurrentThread();
        _app = new XamlApp();
        ComponentDispatcher.ThreadFilterMessage += FilterMessage;
        IsStarted = true;
    }

    /// <summary>
    /// What the Windows App SDK's module initializer does before Main in a self-contained app
    /// (its UndockedRegFreeWinRT-AutoInitializer.cs), done on first use instead. The base
    /// directory is where the SDK's DLLs are (the unpack folder of a single-file exe), read by
    /// the app manifest's redirections; the process id beside it keeps child processes, which
    /// inherit the variable, from honoring it. Loading Microsoft.WindowsAppRuntime.dll is what
    /// turns on registration-free activation of the SDK's WinRT classes.
    /// </summary>
    private static void LoadWindowsAppRuntime()
    {
        if (_runtimeLoaded) return;
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // The function does nothing; calling it is what loads the DLL.
        _ = WindowsAppRuntime_EnsureIsLoaded();
        _runtimeLoaded = true;
    }

    [DllImport("Microsoft.WindowsAppRuntime.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    /// <summary>At app exit, when <see cref="IsStarted"/>.</summary>
    public static void Shutdown()
    {
        if (_app == null) return;
        ComponentDispatcher.ThreadFilterMessage -= FilterMessage;
#pragma warning disable CA1031 // the process is ending: whatever this throws must not stop the rest of App.OnExit
        try { _controller?.ShutdownQueue(); }
        catch (Exception ex) { Services.LogService.Log("Shutting down the WinUI dispatcher queue failed", ex); }
#pragma warning restore CA1031
        _controller = null;
        _app = null;
    }

    private static void FilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled) return;
        // A key in a hosted editor is QuickMail's first: commands, Tab and editing combinations
        // are carried out at once, with the modifier keys as they are at this instant.
        if (WinUiEditorHost.TakeKey(ref msg))
        {
            handled = true;
            return;
        }
        var m = new ContentMsg
        {
            hwnd = msg.hwnd, message = (uint)msg.message, wParam = msg.wParam, lParam = msg.lParam,
            time = msg.time, ptX = msg.pt_x, ptY = msg.pt_y,
        };
        if (ContentPreTranslateMessage(ref m))
        {
            handled = true;
            return;
        }
        // Any other key for a hosted editor goes straight to it, never through WPF's key routing.
        if (WinUiEditorHost.DeliverKey(ref msg)) handled = true;
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
