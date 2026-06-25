using System;
using System.IO;

namespace QuickMail.Services;

/// <summary>
/// Simple append-only file logger. Log is written to %AppData%\QuickMail\quickmail.log.
/// Pass /debug on the command line to also enable Debug() calls.
/// </summary>
public static class LogService
{
    private static string _logFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickMail", "quickmail.log");

    /// <summary>
    /// Redirects the log file to the given profile directory.
    /// Call this before any services are created when --profileDir is supplied.
    /// </summary>
    public static void Configure(string profileDir) =>
        _logFile = Path.Combine(profileDir, "quickmail.log");

    // File.AppendAllText opens-writes-closes; concurrent callers from background sync,
    // prefetch, and the UI thread collided on the file handle and lost lines through
    // the swallowed IOException — exactly the lines you want during a sync crash.
    private static readonly object _writeGate = new();

    /// <summary>Set to true by App.xaml.cs when /debug is on the command line.</summary>
    public static bool DebugMode { get; set; }

    /// <summary>
    /// Whether logging is enabled. Defaults to true. Set from config at startup and when
    /// settings are saved. When false, Log() is a no-op unless DebugMode is also true.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Controls log line layout. "actionFirst" (default) puts the message before the timestamp,
    /// which is easier to scan with a screen reader since logs are already chronological.
    /// "timeFirst" puts the timestamp first (the original format).
    /// Set this from ConfigService after loading config on startup.
    /// </summary>
    public static string Format { get; set; } = "actionFirst";

    public static void Log(string message)
    {
        if (!Enabled && !DebugMode) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            var ts   = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = Format == "timeFirst"
                ? $"{ts}  {message}{Environment.NewLine}"
                : $"{message}  [{ts}]{Environment.NewLine}";
            lock (_writeGate)
            {
                File.AppendAllText(_logFile, line);
            }
        }
        catch { /* never crash on logging */ }
    }

    /// <summary>
    /// Deletes the log file. Safe to call during a running session — subsequent Log() calls
    /// recreate the file. No-op if the file does not exist.
    /// </summary>
    public static void DeleteLog()
    {
        try
        {
            lock (_writeGate)
            {
                if (File.Exists(_logFile))
                    File.Delete(_logFile);
            }
        }
        catch { /* never crash on log management */ }
    }

    public static void Log(string context, Exception ex) =>
        Log($"[ERROR] {context}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Only writes when /debug was passed on the command line.</summary>
    public static void Debug(string message)
    {
        if (DebugMode) Log($"[DEBUG] {message}");
    }
}
