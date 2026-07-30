using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Drives the app to one or more named UI surfaces, captures each as a PNG, and
/// exits the process (#180 Decision E). Constructed only in --ui-probe mode,
/// after InitialLoadAsync has populated the cache-first UI. Every surface is
/// opened through the SAME command/handler the user's menu would invoke — the
/// probe exercises real paths, never a parallel one.
///
/// Modal surfaces (Settings, View Manager, command palette) are opened with a
/// fire-and-forget dispatcher invoke: their nested message loops keep pumping
/// the dispatcher queue, so this driver's timer-based polling and captures run
/// inside the modal loop, and closing the window unblocks it.
/// </summary>
internal sealed class UiProbeDriver
{
    private static readonly TimeSpan SurfaceTimeout = TimeSpan.FromSeconds(15);

    private readonly MainWindow _window;
    private readonly MainViewModel _vm;
    private readonly ICommandRegistry _registry;
    private readonly UiProbeOptions _options;
    private readonly IScreenshotCaptureService _capture;

    public UiProbeDriver(MainWindow window, MainViewModel vm, ICommandRegistry registry,
        UiProbeOptions options, IScreenshotCaptureService capture)
    {
        _window = window;
        _vm = vm;
        _registry = registry;
        _options = options;
        _capture = capture;
    }

    public async Task RunAsync()
    {
        var exitCode = 0;
        try
        {
            if (IsSessionLocked())
            {
                // DWM does not composite new windows on the secure desktop; every
                // capture would be a white frame. Exit distinctly so the
                // orchestrator can tell "environment unusable" from "surface broke".
                LogService.Log("ui-probe: desktop session is locked; captures would be blank. Exiting 4.");
                _window.ForceExit(4);
                return;
            }
            Directory.CreateDirectory(_options.CaptureDir);
            var index = 0;
            foreach (var surface in _options.Surfaces)
            {
                index++;
                var ok = await RunSurfaceAsync(surface, index);
                if (!ok)
                {
                    LogService.Log($"ui-probe: surface \"{surface}\" FAILED.");
                    exitCode = 2;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Log("ui-probe", ex);
            exitCode = 3;
        }
        _window.ForceExit(exitCode);
    }

    private async Task<bool> RunSurfaceAsync(string surface, int index)
    {
        // With multiple surfaces, a bare --capture-tag would make every shot
        // overwrite the last; suffix the index so each keeps a distinct file.
        var tag = _options.CaptureTag is { } custom
            ? _options.Surfaces.Count > 1 ? $"{custom}-{index:D2}" : custom
            : $"{index:D2}-{surface}";
        var path = Path.Combine(_options.CaptureDir, tag + ".png");
        LogService.Debug($"ui-probe: {surface} -> {path}");

        switch (surface)
        {
            case "inbox":
                await IdleAsync();
                await IdleAsync();
                return await CaptureSettledAsync(_window, path);

            case "reading-pane":
                return await CaptureReadingPaneAsync(path);

            case "calendar":
                ExecuteCommand("view.calendar");
                await IdleAsync();
                await IdleAsync();
                return await CaptureSettledAsync(_window, path);

            case "compose":
                return await CaptureChildWindowAsync(() => ExecuteCommand("mail.new"),
                    w => w is ComposeWindow, path);

            case "theme-manager":
                return await CaptureChildWindowAsync(() => ExecuteCommand("theme.manager.open"),
                    w => w is ThemeManagerWindow, path);

            case "address-book":
                return await CaptureChildWindowAsync(() => ExecuteCommand("contacts.openAddressBook"),
                    w => w is AddressBookWindow, path);

            case "rules":
                return await CaptureChildWindowAsync(() => ExecuteCommand("mail.rules"),
                    w => w is RulesManagerWindow or UnifiedRulesWindow, path);

            case "saved-views":
                return await CaptureChildWindowAsync(() => _vm.ManageViewsCommand.Execute(null),
                    w => w is ViewManagerWindow, path);

            case "settings-appearance":
                return await CaptureChildWindowAsync(() => _window.ShowSettingsDialogForProbe(),
                    w => w is SettingsDialog, path,
                    prepare: w => SelectTabByHeader((SettingsDialog)w, "ppearance"));

            case "command-palette":
                return await CaptureChildWindowAsync(() => _window.OpenCommandPaletteForProbe(),
                    w => w is CommandPaletteWindow, path);

            case "folder-picker":
                return await CaptureChildWindowAsync(() => ExecuteCommand("view.folderPicker"),
                    w => w is FolderPickerWindow, path);

            default:
                LogService.Log($"ui-probe: unknown surface \"{surface}\". Known: inbox, reading-pane, calendar, compose, theme-manager, address-book, rules, saved-views, settings-appearance, command-palette, folder-picker.");
                return false;
        }
    }

    private void ExecuteCommand(string commandId)
    {
        var command = _registry.FindById(commandId)
            ?? throw new InvalidOperationException($"ui-probe: command \"{commandId}\" is not registered.");
        command.Execute();
    }

    /// <summary>
    /// Selects the fixture HTML message and awaits the window's render-complete
    /// signal (WebView2 NavigationCompleted + an idle turn) — the strongest
    /// settle signal available, per spec Decision F.
    /// </summary>
    private async Task<bool> CaptureReadingPaneAsync(string path)
    {
        var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendered() => rendered.TrySetResult(true);
        _window.MessageBodyRendered += OnRendered;
        try
        {
            // Through the window's open path (the notification-activation route):
            // the VM command alone loads the detail but never renders the body.
            _ = _window.OpenMessageForProbeAsync(
                UiProbeFixture.AccountId, UiProbeFixture.InboxFolder, UiProbeFixture.HtmlMessageId);

            var done = await Task.WhenAny(rendered.Task, Task.Delay(SurfaceTimeout)) == rendered.Task;
            if (!done)
            {
                LogService.Log("ui-probe: reading pane never reported render-complete.");
                return false;
            }
            await IdleAsync();
            return await CaptureSettledAsync(_window, path);
        }
        finally
        {
            _window.MessageBodyRendered -= OnRendered;
        }
    }

    /// <summary>
    /// Opens a surface that lives in its own window (modal or modeless), waits
    /// for the window to appear and settle, captures it, and closes it.
    /// </summary>
    private async Task<bool> CaptureChildWindowAsync(Action open, Func<Window, bool> match,
        string path, Action<Window>? prepare = null)
    {
        // Modal ShowDialog would block this method if called directly — fire it
        // through the dispatcher and poll from within whatever loop is pumping.
        _ = _window.Dispatcher.BeginInvoke(open);

        Window? child = null;
        var deadline = Environment.TickCount64 + (long)SurfaceTimeout.TotalMilliseconds;
        while (child is null && Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            child = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => match(w) && w.IsLoaded && w.IsVisible);
        }
        if (child is null)
        {
            LogService.Log($"ui-probe: expected window for {Path.GetFileNameWithoutExtension(path)} never appeared.");
            return false;
        }

        try
        {
            await IdleAsync();
            if (prepare != null)
            {
                prepare(child);
                await IdleAsync();
            }
            return await CaptureSettledAsync(child, path);
        }
        finally
        {
            // Always close: a leaked modal child would leave its nested message
            // loop running while RunAsync tries to shut the app down.
            try { child.Close(); } catch (Exception ex) { LogService.Debug($"ui-probe: child close failed: {ex.Message}"); }
            await IdleAsync();
        }
    }

    /// <summary>Selects the TabItem whose header contains the fragment (e.g. Appearance).</summary>
    private static void SelectTabByHeader(Window window, string headerFragment)
    {
        var tabs = FindDescendant<TabControl>(window);
        if (tabs is null) return;
        for (var i = 0; i < tabs.Items.Count; i++)
        {
            if (tabs.Items[i] is TabItem item &&
                item.Header?.ToString()?.Contains(headerFragment, StringComparison.OrdinalIgnoreCase) == true)
            {
                tabs.SelectedIndex = i;
                return;
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private async Task IdleAsync() =>
        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    // ── Settled capture ───────────────────────────────────────────────────────
    // Dispatcher idle is not "pixels on screen": a freshly shown WPF window can
    // reach ApplicationIdle before DWM has composited its first frame, and
    // PrintWindow then returns a white client area under a painted title bar
    // (which defeats the single-color fallback). So each capture waits for a
    // CompositionTarget.Rendering frame plus DwmFlush, and retries while the
    // client area still reads as one flat color. A window that is STILL blank
    // after the retries is captured as-is — a genuinely blank surface is
    // exactly what the AI review exists to flag, so the probe must not hide it.

    private async Task<bool> CaptureSettledAsync(Window window, string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await IdleAsync();
            await NextCompositionFrameAsync();
            try { _ = DwmFlush(); } catch (DllNotFoundException) { }
            if (_capture.CaptureToFile(window, path) && !ClientAreaLooksBlank(path))
                return true;
            await Task.Delay(300);
        }
        return File.Exists(path);
    }

    private static async Task NextCompositionFrameAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            tcs.TrySetResult(true);
        };
        CompositionTarget.Rendering += handler;
        // Rendering only fires while WPF has something to draw; don't hang if idle.
        if (await Task.WhenAny(tcs.Task, Task.Delay(1000)) != tcs.Task)
            CompositionTarget.Rendering -= handler;
    }

    /// <summary>True when the image below the title-bar strip is one flat color.</summary>
    private static bool ClientAreaLooksBlank(string path)
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            // Inset past the title bar AND the window border: a 1px dark frame
            // around a white void otherwise defeats the single-color check.
            var top = Math.Min(frame.PixelHeight - 2, Math.Max(40, frame.PixelHeight / 10));
            var margin = Math.Max(8, frame.PixelWidth / 50);
            var width = frame.PixelWidth - 2 * margin;
            var height = frame.PixelHeight - top - margin;
            if (width <= 0 || height <= 0) return false;
            var client = new CroppedBitmap(frame, new Int32Rect(margin, top, width, height));
            return ScreenshotCaptureService.IsSingleColor(client);
        }
        catch (Exception)
        {
            return false; // unreadable file is a different failure; don't loop on it
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>True when the interactive desktop is not available (locked/secure desktop).</summary>
    private static bool IsSessionLocked()
    {
        const uint DESKTOP_READOBJECTS = 0x0001;
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return true;
        _ = CloseDesktop(desktop);
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);
}
