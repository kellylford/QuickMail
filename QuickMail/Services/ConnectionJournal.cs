using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace QuickMail.Services;

/// <summary>
/// Category of a connection journal entry. Used for filtering and for the report summary.
/// </summary>
public enum ConnectionEventKind
{
    /// <summary>Socket connect / authenticate against the server.</summary>
    Connect,
    /// <summary>Connection pool rent, return, reuse, probe, discard.</summary>
    Pool,
    /// <summary>IDLE watcher lifecycle.</summary>
    Idle,
    /// <summary>A change to the account's reported reachability or displayed status.</summary>
    Status,
    /// <summary>An independent reachability probe and its verdict.</summary>
    Probe,
    /// <summary>A dropped connection that was transparently retried.</summary>
    Retry,
}

/// <summary>
/// One recorded connection event.
/// </summary>
/// <param name="Timestamp">When it happened (local time).</param>
/// <param name="Kind">Which subsystem produced it.</param>
/// <param name="Account">Account label, or "-" for events with no single owning account.</param>
/// <param name="Host">IMAP host the event concerns, or "-".</param>
/// <param name="Phase">Short verb-ish tag, e.g. "connect-failed", "reuse-hot", "verdict".</param>
/// <param name="Detail">Free text: error chains, counts, reasons.</param>
public sealed record ConnectionEvent(
    DateTime Timestamp,
    ConnectionEventKind Kind,
    string Account,
    string Host,
    string Phase,
    string Detail)
{
    /// <summary>
    /// One-line rendering, used for the log file, the report, and the diagnostics list.
    /// A <see cref="System.Windows.Controls.Primitives.Selector"/> reads an item's accessible
    /// name from ToString(), so this doubles as the screen-reader text for a journal row.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append("  ").Append(Kind.ToString().ToUpperInvariant().PadRight(6));
        sb.Append("  ").Append(Phase);
        if (Account != "-") sb.Append("  account=").Append(Account);
        if (Host    != "-") sb.Append("  host=").Append(Host);
        if (!string.IsNullOrEmpty(Detail)) sb.Append("  ").Append(Detail);
        return sb.ToString();
    }
}

/// <summary>
/// Bounded, opt-in journal of IMAP connection activity.
///
/// Separate from <see cref="LogService"/> and its <c>EnableLogging</c> setting: this records
/// connection lifecycle rather than general application events, and the two are useful at
/// different times. Off by default and driven by the <c>ConnectionDiagnostics</c> setting —
/// it is a troubleshooting tool, switched on when investigating a problem.
///
/// Holds the last <see cref="Capacity"/> events in memory to back the Connection Diagnostics
/// window, and appends every event to <c>connection.log</c> in the profile directory, rolling
/// over at 5 MB. For scale: a full session of normal use produced roughly 50 KB.
///
/// Recorded content is account labels (which are email addresses for some accounts), server host
/// names, IP addresses, counts and error text. Never passwords, tokens, or message content — but
/// a shared report does carry those addresses, which is why enabling it is the user's choice.
/// </summary>
public static class ConnectionJournal
{
    /// <summary>How many events the in-memory ring keeps for the diagnostics window.</summary>
    public const int Capacity = 2000;

    /// <summary>Roll the file over at this size so the journal stays bounded.</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly object _gate = new();
    // Separate from _gate: taken only around Enabled transitions, which call Record (and therefore
    // take _gate) while holding it. Two locks with a strict order — _enabledGate then _gate — and
    // never the reverse.
    private static readonly object _enabledGate = new();
    // Guards the log file only, so a disk write never blocks a UI-thread Snapshot().
    private static readonly object _fileGate = new();
    private static readonly Queue<ConnectionEvent> _ring = new(Capacity);
    private static string? _logFile;
    private static volatile bool _enabled;

    /// <summary>
    /// Whether the journal records anything. Off by default; driven by the
    /// <c>ConnectionDiagnostics</c> setting, and applied live when settings are saved.
    ///
    /// When off, <see cref="Record"/> returns on a volatile read before allocating. Call sites whose
    /// detail is expensive to build — anything calling into <see cref="HostConnectionCensus"/>,
    /// which resolves DNS — must use the <see cref="Func{T}"/> overload or check this property
    /// first, because arguments to the eager overload are evaluated before it can short-circuit.
    /// Turning it on begins recording immediately — no restart — so a problem already in progress is
    /// captured from the moment the user switches it on.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            // Serialised under its own lock, and the disable marker is written while still enabled
            // rather than by flipping the flag back and forth. The earlier check-then-act version
            // had a window where a concurrent enable could be silently overwritten by the disable
            // that was in flight, leaving recording off while the caller believed it was on.
            lock (_enabledGate)
            {
                if (_enabled == value) return;

                if (value)
                {
                    _enabled = true;
                    Record(ConnectionEventKind.Status, "-", "-", "diagnostics-enabled",
                        $"QuickMail {AppVersion()} pid={Environment.ProcessId}");
                }
                else
                {
                    // Say so in the file before going quiet, so a journal that simply stops is
                    // distinguishable from one the user turned off mid-investigation.
                    Record(ConnectionEventKind.Status, "-", "-", "diagnostics-disabled",
                        "recording stopped by the Connection Diagnostics setting");
                    _enabled = false;
                }
            }
        }
    }

    /// <summary>
    /// Raised after an event is recorded, so an open diagnostics window can live-update.
    /// Fires on whatever thread produced the event — subscribers must marshal to the UI thread.
    /// </summary>
    public static event Action<ConnectionEvent>? EventRecorded;

    /// <summary>
    /// Points the journal at the profile directory. Call once at startup, before services
    /// are constructed. Until this is called the journal is memory-only (nothing is lost —
    /// early events still land in the ring and appear in the report).
    /// </summary>
    public static void Configure(string profileDir)
    {
        lock (_gate)
        {
            _logFile = Path.Combine(profileDir, "connection.log");
        }
        // No "started" line here: recording is gated on Enabled, which the caller sets from config
        // immediately after. Enabling emits its own marker.
    }

    private static string AppVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetEntryAssembly()?
                .GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    /// <summary>Records one event. Never throws. No-op when <see cref="Enabled"/> is false.</summary>
    public static void Record(
        ConnectionEventKind kind, string account, string host, string phase, string detail = "")
    {
        if (!_enabled) return;

        ConnectionEvent evt;
        try
        {
            evt = new ConnectionEvent(
                DateTime.Now, kind,
                string.IsNullOrWhiteSpace(account) ? "-" : account,
                string.IsNullOrWhiteSpace(host) ? "-" : host,
                phase, detail ?? string.Empty);
        }
        catch { return; }

        string? file;
        lock (_gate)
        {
            _ring.Enqueue(evt);
            while (_ring.Count > Capacity) _ring.Dequeue();
            file = _logFile;
        }

        if (file != null) AppendToFile(file, evt);

        try { EventRecorded?.Invoke(evt); } catch { /* a bad subscriber must not break logging */ }
    }

    /// <summary>
    /// Records one event, building the detail string only if something is listening.
    ///
    /// The eager overload evaluates its arguments before the enabled check can run, and several
    /// call sites pass a host census that resolves DNS and walks every tracked host. Use this
    /// overload wherever the detail is more than a cheap literal, so the disabled path really is
    /// just a volatile read.
    /// </summary>
    public static void Record(
        ConnectionEventKind kind, string account, string host, string phase, Func<string> detail)
    {
        if (!_enabled) return;

        string text;
        try { text = detail(); }
        catch { text = "(detail unavailable)"; }

        Record(kind, account, host, phase, text);
    }

    /// <summary>Records an exception with its full inner chain, including socket error codes.</summary>
    public static void RecordError(
        ConnectionEventKind kind, string account, string host, string phase, Exception ex,
        string? extra = null)
    {
        // Check first: walking the inner-exception chain is the costly part, and it is pure waste
        // when nothing is recording.
        if (!_enabled) return;

        var detail = LogService.FormatException(ex);
        if (!string.IsNullOrEmpty(extra)) detail = $"{extra} — {detail}";
        Record(kind, account, host, phase, detail);
    }

    private static void AppendToFile(string file, ConnectionEvent evt)
    {
        // Its own lock, deliberately NOT _gate. Snapshot() and LogFilePath take _gate and are called
        // from the UI thread to populate the diagnostics window; holding _gate across an
        // open-write-close would stall the UI behind a background thread's disk I/O — on the very
        // screen someone opens while something is already going wrong.
        try
        {
            lock (_fileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                RollIfTooLarge(file);
                File.AppendAllText(
                    file,
                    $"{evt.Timestamp:yyyy-MM-dd} {evt}{Environment.NewLine}");
            }
        }
        catch { /* never crash on logging */ }
    }

    // Caller holds _fileGate.
    private static void RollIfTooLarge(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < MaxFileBytes) return;

            var previous = file + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(file, previous);
        }
        catch { /* if rolling fails, keep appending rather than lose the journal */ }
    }

    /// <summary>
    /// Deletes the journal file and its rolled-over predecessor, and clears the in-memory ring.
    /// Safe to call while recording — subsequent events recreate the file.
    /// </summary>
    public static void DeleteLog()
    {
        string? file;
        lock (_gate)
        {
            _ring.Clear();
            file = _logFile;
        }
        if (file == null) return;

        lock (_fileGate)
        {
            foreach (var path in new[] { file, file + ".1" })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { /* never crash on log management */ }
            }
        }
    }

    /// <summary>Snapshot of the in-memory ring, oldest first.</summary>
    public static IReadOnlyList<ConnectionEvent> Snapshot()
    {
        lock (_gate) { return _ring.ToArray(); }
    }

    /// <summary>Path of the journal file, or null if <see cref="Configure"/> has not run.</summary>
    public static string? LogFilePath
    {
        get { lock (_gate) { return _logFile; } }
    }

    /// <summary>
    /// Builds the self-contained text report the user sends back: environment, a summary of
    /// what the journal saw, then the full event list.
    /// </summary>
    public static string BuildReport(IEnumerable<string>? accountSummaries = null)
    {
        var events = Snapshot();
        var sb = new StringBuilder();

        sb.AppendLine("QuickMail connection diagnostics report");
        sb.AppendLine($"Generated:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Version:    {AppVersion()}");
        sb.AppendLine($"OS:         {Environment.OSVersion}");
        sb.AppendLine($"Journal:    {LogFilePath ?? "(memory only)"}");
        sb.AppendLine($"Events:     {events.Count}"
                      + (events.Count >= Capacity ? $" (ring full — oldest trimmed, see the log file)" : ""));
        sb.AppendLine();

        if (accountSummaries != null)
        {
            sb.AppendLine("Accounts");
            sb.AppendLine("--------");
            foreach (var line in accountSummaries) sb.AppendLine("  " + line);
            sb.AppendLine();
        }

        // The verdicts are the point of the whole exercise — surface them before the raw log
        // so whoever reads the report does not have to go hunting.
        var verdicts = events.Where(e => e.Kind == ConnectionEventKind.Probe
                                         && e.Phase == "verdict").ToList();
        sb.AppendLine("Probe verdicts");
        sb.AppendLine("--------------");
        if (verdicts.Count == 0)
        {
            sb.AppendLine("  (none recorded — no account was marked disconnected during this session)");
        }
        else
        {
            foreach (var v in verdicts) sb.AppendLine("  " + v);
        }
        sb.AppendLine();

        sb.AppendLine("Event counts by kind");
        sb.AppendLine("--------------------");
        foreach (var group in events.GroupBy(e => e.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
            sb.AppendLine($"  {group.Key,-8} {group.Count()}");
        sb.AppendLine();

        sb.AppendLine("Full event journal");
        sb.AppendLine("------------------");
        foreach (var e in events) sb.AppendLine(e.ToString());

        return sb.ToString();
    }

    /// <summary>Test hook: empties the in-memory ring and detaches the file.</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _ring.Clear();
            _logFile = null;
        }
        EventRecorded = null;
        _enabled = true;   // tests exercise recording; the disabled path has its own tests
    }
}
