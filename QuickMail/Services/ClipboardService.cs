using System;
using System.Threading;
using System.Windows;

namespace QuickMail.Services;

/// <summary>
/// The real Windows clipboard, with the retry every Windows application needs.
///
/// Only one process may hold the clipboard at a time, so <c>Clipboard.SetText</c> throws
/// <c>COMException (CLIPBRD_E_CANT_OPEN)</c> whenever another application is mid-operation on it —
/// a clipboard manager, a remote desktop session, or another editor. That is a transient, expected
/// condition, not an error worth surfacing: retrying a few milliseconds later almost always
/// succeeds, which is exactly what the Windows shell itself does.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    /// <summary>Shared instance used when no clipboard service is injected.</summary>
    public static IClipboardService Default { get; } = new ClipboardService();

    private const int MaxAttempts = 5;
    private const int RetryDelayMs = 40;

    public bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == MaxAttempts)
                {
                    LogService.Log($"Clipboard: could not copy after {MaxAttempts} attempts — {ex.Message}");
                    return false;
                }
                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    }

    public string GetText()
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch (Exception ex)
            {
                if (attempt == MaxAttempts)
                {
                    LogService.Log($"Clipboard: could not read after {MaxAttempts} attempts — {ex.Message}");
                    return string.Empty;
                }
                Thread.Sleep(RetryDelayMs);
            }
        }

        return string.Empty;
    }
}
