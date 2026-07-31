using System;
using System.Windows;

namespace QuickMail.Services;

/// <summary>
/// Wired at the DI root when /debug is absent: capture is structurally
/// unreachable in normal builds — no folder, no toggle effect, no title
/// suffix, zero overhead.
/// </summary>
public sealed class NullScreenshotCaptureService : IScreenshotCaptureService
{
    public bool Enabled { get => false; set { } }

    public event EventHandler? EnabledChanged { add { } remove { } }

    public string SessionFolder => string.Empty;

    public void OnWindowLoaded(Window window) { }

    public void Capture(Window window, string label) { }

    public void OpenFolder() { }

    public bool CaptureToFile(Window window, string filePath) => false;

    public void Dispose() { }
}
