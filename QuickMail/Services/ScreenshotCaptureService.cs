using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickMail.Services;

/// <summary>
/// Real capture engine (issue #175), constructed only when /debug is active.
///
/// Pixel path: Win32 PrintWindow with PW_RENDERFULLCONTENT so the out-of-process
/// WebView2 reading pane is included — RenderTargetBitmap renders it blank, and
/// WebView2 fidelity is the feature's reason to exist. If PrintWindow returns a
/// single-color frame (some GPU-composited surfaces), fall back to BitBlt from
/// the screen at the window rect: a freshly shown window is on top, so occlusion
/// is rare at capture time. Encoding/saving happens off the UI thread; only the
/// fast pixel grab runs on it. Deliberately no System.Drawing dependency — GDI
/// interop plus WPF's PngBitmapEncoder covers everything.
/// </summary>
public class ScreenshotCaptureService : IScreenshotCaptureService
{
    private const int MaxImagesPerSession = 500;
    private const long DebounceMs = 750;

    private readonly string _rootFolder;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastCaptureByLabel = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _pendingSaves = new();
    private string? _sessionFolder;
    private int _counter;
    private bool _capReported;
    private bool _enabled;
    private bool _disposed;

    /// <summary>Test seam: replaces the Win32 pixel grab so tests never need a real HWND.</summary>
    internal Func<Window, BitmapSource?>? PixelGrabber { get; set; }

    public ScreenshotCaptureService(ProfileContext profile)
    {
        _rootFolder = Path.Combine(profile.ProfileDir, "debug-screenshots");
    }

    public event EventHandler? EnabledChanged;

    public string SessionFolder
    {
        get
        {
            lock (_gate)
            {
                _sessionFolder ??= Path.Combine(_rootFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                return _sessionFolder;
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value || _disposed) return;
            if (value)
            {
                try
                {
                    Directory.CreateDirectory(SessionFolder);
                }
                catch (Exception ex)
                {
                    LogService.Log($"Screenshot capture not enabled — folder creation failed: {ex.Message}");
                    return;
                }
            }
            _enabled = value;
            UpdateAllTitleSuffixes(value);
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void OnWindowLoaded(Window window)
    {
        if (!_enabled) return;
        ApplyTitleSuffix(window, enabled: true);
        // ContentRendered cannot be class-handled; idle-after-Loaded is the
        // central equivalent that lets layout and first paint complete.
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => Capture(window, window.GetType().Name));
    }

    public void Capture(Window window, string label)
    {
        if (!_enabled || _disposed) return;

        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_lastCaptureByLabel.TryGetValue(label, out var last) && now - last < DebounceMs)
                return;
            _lastCaptureByLabel[label] = now;
            if (IsAtSessionCap()) return;
        }

        BitmapSource? frame = null;
        try
        {
            frame = (PixelGrabber ?? GrabWindowPixels)(window);
        }
        catch (Exception ex)
        {
            LogService.Debug($"Screenshot grab failed for {label}: {ex.Message}");
        }
        if (frame is null) return;
        frame.Freeze();

        // The counter is consumed only by a successful grab, so failed grabs
        // neither burn the session cap nor leave gaps in the numbering.
        int frameNumber;
        lock (_gate)
        {
            if (IsAtSessionCap()) return;
            frameNumber = ++_counter;
        }

        var path = Path.Combine(SessionFolder, $"{frameNumber:D4}-{Slug(label)}.png");
        var save = Task.Run(() => SavePng(frame, path));
        lock (_gate)
        {
            _pendingSaves.RemoveAll(t => t.IsCompleted);
            _pendingSaves.Add(save);
        }
    }

    /// <summary>Callers must hold <see cref="_gate"/>.</summary>
    private bool IsAtSessionCap()
    {
        if (_counter < MaxImagesPerSession) return false;
        if (!_capReported)
        {
            LogService.Debug($"Screenshot session cap ({MaxImagesPerSession}) reached; capture stopped for this session.");
            _capReported = true;
        }
        return true;
    }

    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(SessionFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SessionFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Log($"Could not open screenshots folder: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        foreach (var window in _titleKeepers.Keys.ToList())
            DetachTitleKeeper(window, refreshFromBinding: false);
        Task[] pending;
        lock (_gate) pending = _pendingSaves.ToArray();
        try
        {
            // Best-effort flush so a capture taken just before exit isn't truncated.
            Task.WaitAll(pending, TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Individual save failures already logged in SavePng.
        }
        GC.SuppressFinalize(this);
    }

    // ── Title suffix ──────────────────────────────────────────────────────────

    private static readonly System.ComponentModel.DependencyPropertyDescriptor TitleDescriptor =
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(Window));

    private readonly Dictionary<Window, EventHandler> _titleKeepers = new();

    private void UpdateAllTitleSuffixes(bool enabled)
    {
        if (Application.Current is null) return;
        foreach (Window w in Application.Current.Windows)
            ApplyTitleSuffix(w, enabled);
    }

    internal void ApplyTitleSuffix(Window window, bool enabled)
    {
        if (BindingOperations.GetBindingExpression(window, Window.TitleProperty) is null)
        {
            // Static title: direct, idempotent assignment.
            var title = window.Title ?? string.Empty;
            var has = title.EndsWith(IScreenshotCaptureService.TitleSuffix, StringComparison.Ordinal);
            if (enabled && !has)
                window.Title = title + IScreenshotCaptureService.TitleSuffix;
            else if (!enabled && has)
                window.Title = title[..^IScreenshotCaptureService.TitleSuffix.Length];
            return;
        }

        // Data-bound title (MainWindow, Compose, MessageWindow, …): assignment
        // would detach the binding, so overlay via SetCurrentValue and re-apply
        // whenever the binding pushes a fresh base title.
        if (enabled) AttachTitleKeeper(window);
        else DetachTitleKeeper(window, refreshFromBinding: true);
    }

    private void AttachTitleKeeper(Window window)
    {
        if (_titleKeepers.ContainsKey(window)) return;
        EventHandler onTitleChanged = (_, _) => EnsureBoundTitleSuffix(window);
        TitleDescriptor.AddValueChanged(window, onTitleChanged);
        _titleKeepers[window] = onTitleChanged;
        window.Closed += OnKeeperWindowClosed;   // paired -= in DetachTitleKeeper
        EnsureBoundTitleSuffix(window);
    }

    private void EnsureBoundTitleSuffix(Window window)
    {
        if (!_enabled) return;
        var title = window.Title ?? string.Empty;
        // Re-entrancy guard: our own SetCurrentValue fires the change handler once
        // more with the suffix already present.
        if (title.EndsWith(IScreenshotCaptureService.TitleSuffix, StringComparison.Ordinal)) return;
        window.SetCurrentValue(Window.TitleProperty, title + IScreenshotCaptureService.TitleSuffix);
    }

    private void DetachTitleKeeper(Window window, bool refreshFromBinding)
    {
        if (!_titleKeepers.Remove(window, out var onTitleChanged)) return;
        TitleDescriptor.RemoveValueChanged(window, onTitleChanged);
        window.Closed -= OnKeeperWindowClosed;
        if (refreshFromBinding)
            BindingOperations.GetBindingExpression(window, Window.TitleProperty)?.UpdateTarget();
    }

    private void OnKeeperWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
            DetachTitleKeeper(window, refreshFromBinding: false);
    }

    // ── Filename slug ─────────────────────────────────────────────────────────

    internal static string Slug(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "window";
        Span<char> buffer = stackalloc char[Math.Min(label.Length, 60)];
        var n = 0;
        foreach (var c in label)
        {
            if (n == buffer.Length) break;
            buffer[n++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-';
        }
        var slug = new string(buffer[..n]).Trim('-');
        return slug.Length == 0 ? "window" : slug;
    }

    // ── Pixel grab (UI thread) ────────────────────────────────────────────────

    private static BitmapSource? GrabWindowPixels(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;
        if (!GetWindowRect(hwnd, out var rect)) return null;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memDc, hBitmap);
        try
        {
            var printed = PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);
            SelectObject(memDc, previous);          // deselect before reading pixels
            var source = printed ? FromHBitmap(hBitmap) : null;

            if (source is null || IsSingleColor(source))
            {
                SelectObject(memDc, hBitmap);
                BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SRCCOPY | CAPTUREBLT);
                SelectObject(memDc, previous);
                source = FromHBitmap(hBitmap);
            }
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc); // 0 return = DC wasn't released; nothing actionable at teardown
        }
    }

    private static BitmapSource? FromHBitmap(IntPtr hBitmap)
    {
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Samples an 8×8 grid; a frame that is one flat color is treated as a
    /// failed PrintWindow (typical for GPU surfaces) and triggers the BitBlt
    /// fallback. Internal for tests.
    /// </summary>
    internal static bool IsSingleColor(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        if (w == 0 || h == 0) return true;

        var pixel = new byte[4];
        uint? first = null;
        for (var gy = 0; gy < 8; gy++)
        {
            for (var gx = 0; gx < 8; gx++)
            {
                int x = Math.Min(w - 1, gx * w / 8), y = Math.Min(h - 1, gy * h / 8);
                converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                var value = BitConverter.ToUInt32(pixel, 0);
                first ??= value;
                if (value != first) return false;
            }
        }
        return true;
    }

    private static void SavePng(BitmapSource frame, string path)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            LogService.Debug($"Screenshot save failed for {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // ── Win32 interop ─────────────────────────────────────────────────────────

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, uint rop);
}
