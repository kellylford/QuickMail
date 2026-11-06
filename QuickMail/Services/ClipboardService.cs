using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// The private clipboard format that says "QuickMail put this here", holding the token of the
    /// copy it belongs to. Every other program ignores it.
    /// </summary>
    public const string MarkerFormat = "QuickMail.CopiedMessages";

    /// <summary>DROPEFFECT_COPY: files pasted in Explorer are copied, never moved, even after a cut.</summary>
    private const int DropEffectCopy = 1;

    public bool SetFiles(IReadOnlyList<string> paths, string marker)
    {
        if (paths.Count == 0 || string.IsNullOrEmpty(marker)) return false;
        return Retry("copy files", () =>
        {
            var files = new StringCollection();
            files.AddRange(paths.ToArray());
            var data = new DataObject();
            data.SetFileDropList(files);
            // Explorer reads this to choose between copy and move. A cut message is still only
            // copied out of QuickMail: the move happens when it is pasted into another folder.
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(DropEffectCopy)));
            data.SetData(MarkerFormat, marker);
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }, fallback: false);
    }

    public bool SetMarker(string marker)
    {
        if (string.IsNullOrEmpty(marker)) return false;
        return Retry("copy messages", () =>
        {
            var data = new DataObject();
            data.SetData(MarkerFormat, marker);
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }, fallback: false);
    }

    public string? GetMarker() =>
        Retry("read", () => Clipboard.GetDataObject() is { } data && data.GetDataPresent(MarkerFormat)
            ? data.GetData(MarkerFormat) as string
            : null, fallback: null);

    private static T Retry<T>(string what, Func<T> action, T fallback)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                if (attempt == MaxAttempts)
                {
                    LogService.Log($"Clipboard: could not {what} after {MaxAttempts} attempts — {ex.Message}");
                    return fallback;
                }
                Thread.Sleep(RetryDelayMs);
            }
        }
        return fallback;
    }
}
