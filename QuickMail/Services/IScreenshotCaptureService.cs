using System;
using System.Windows;

namespace QuickMail.Services;

/// <summary>
/// Debug-only screenshot capture (issue #175): saves a PNG of each new window
/// (and explicitly-captured surfaces like the rendered reading pane) so an
/// external AI can review the UI visually. Exists only when /debug is active;
/// the toggle is session-only and never persisted. See
/// docs/planning/debug-screenshot-capture-pm-dev-spec.md.
/// </summary>
public interface IScreenshotCaptureService : IDisposable
{
    /// <summary>
    /// Title-bar warning appended to every open window while capture is on —
    /// the standing signal that pixels are being written to disk.
    /// </summary>
    public const string TitleSuffix = " — ⚠ SCREENSHOTS ON";

    /// <summary>Session-only; every launch starts false. Never written to config.</summary>
    bool Enabled { get; set; }

    /// <summary>Raised on toggle so bound titles (MainViewModel.WindowTitle) can refresh.</summary>
    event EventHandler? EnabledChanged;

    /// <summary>The folder receiving this session's PNGs (created on first enable).</summary>
    string SessionFolder { get; }

    /// <summary>
    /// Central entry point for the Window.Loaded class handler: applies the
    /// title suffix and schedules an idle-priority capture of the window.
    /// </summary>
    void OnWindowLoaded(Window window);

    /// <summary>Captures one labeled frame of the window, if enabled. Silent; never moves focus.</summary>
    void Capture(Window window, string label);

    /// <summary>Opens the session folder in File Explorer.</summary>
    void OpenFolder();
}
