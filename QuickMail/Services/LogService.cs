using System;
using System.IO;

namespace QuickMail.Services;

/// <summary>
/// Simple append-only file logger. Log is written to %AppData%\QuickMail\quickmail.log.
/// Pass /debug on the command line to also enable Debug() calls.
/// </summary>
public static class LogService
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickMail", "quickmail.log");

    // File.AppendAllText opens-writes-closes; concurrent callers from background sync,
    // prefetch, and the UI thread collided on the file handle and lost lines through
    // the swallowed IOException — exactly the lines you want during a sync crash.
    private static readonly object _writeGate = new();

    /// <summary>Set to true by App.xaml.cs when /debug is on the command line.</summary>
    public static bool DebugMode { get; set; }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
            lock (_writeGate)
            {
                File.AppendAllText(LogFile, line);
            }
        }
        catch { /* never crash on logging */ }
    }

    public static void Log(string context, Exception ex) =>
        Log($"[ERROR] {context}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Only writes when /debug was passed on the command line.</summary>
    public static void Debug(string message)
    {
        if (DebugMode) Log($"[DEBUG] {message}");
    }
}
