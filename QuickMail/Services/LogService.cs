using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

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
        Log($"[ERROR] {context}: {FormatException(ex)}");

    /// <summary>
    /// Walks the full inner-exception chain, appending each type, message, and — for
    /// SocketException — the socket error code and native error code.
    /// </summary>
    public static string FormatException(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        var depth = 0;
        while (current != null && depth < 6)
        {
            if (depth > 0) sb.Append(" → ");
            sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
            if (current is SocketException se)
                sb.Append($" (SocketError={se.SocketErrorCode}, NativeCode={se.NativeErrorCode})");
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    /// <summary>Only writes when /debug was passed on the command line.</summary>
    public static void Debug(string message)
    {
        if (DebugMode) Log($"[DEBUG] {message}");
    }

    /// <summary>
    /// Debug-only scoped tracer for hang diagnosis. On entry writes "[TRACE] &gt;&gt;&gt; label";
    /// on <see cref="IDisposable.Dispose"/> writes "[TRACE] &lt;&lt;&lt; label (Nms)". Because
    /// <see cref="Log"/> opens-writes-closes the file per line, each line is flushed to disk
    /// immediately, so a UI-thread freeze leaves the last unmatched "&gt;&gt;&gt; label" on disk —
    /// that names the blocking call. Returns <see langword="null"/> (a no-op) unless /debug is
    /// set, so there is zero overhead in normal runs. Intended use:
    /// <code>using var _ = LogService.Trace("SomeOperation");</code>
    /// </summary>
    public static IDisposable? Trace(string label)
    {
        if (!DebugMode) return null;
        return new TraceScope(label);
    }

    private sealed class TraceScope : IDisposable
    {
        private readonly string _label;
        private readonly int    _tid;
        private readonly long   _startTicks;

        public TraceScope(string label)
        {
            _label      = label;
            _tid        = Environment.CurrentManagedThreadId;
            _startTicks = Stopwatch.GetTimestamp();
            Log($"[TRACE] >>> {label} (tid={_tid})");
        }

        public void Dispose()
        {
            var ms = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
            Log($"[TRACE] <<< {_label} (tid={_tid}, {ms:F0}ms)");
        }
    }
}
