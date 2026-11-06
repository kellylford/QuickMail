using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace QuickMail.Services;

/// <summary>
/// Enforces one QuickMail process per profile directory. The first instance holds a named
/// mutex and listens on a named event; any later launch for the same profile signals that
/// event — so the running instance can restore its window from the tray — and exits.
/// Different --profileDir values produce different object names, so deliberate
/// multi-profile use keeps working.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateRequested;
    private readonly string _key;
    private RegisteredWaitHandle? _waitRegistration;
    private bool _disposed;

    private SingleInstanceService(Mutex mutex, EventWaitHandle activateRequested, string key)
    {
        _mutex = mutex;
        _activateRequested = activateRequested;
        _key = key;
    }

    /// <summary>
    /// Tries to claim single-instance ownership for the profile selected by <paramref name="args"/>.
    /// Returns the guard on success. Returns null when another instance already owns the profile;
    /// in that case the running instance has been signaled to bring its window to the foreground
    /// and the caller should end the process immediately — unless <paramref name="signalExisting"/>
    /// is false, as for a launch by Windows at sign-in (#770), which must not pull an already-open
    /// window to the front.
    /// </summary>
    public static SingleInstanceService? TryAcquire(string[] args, bool signalExisting = true)
    {
        var key = ProfileKey(args);
        var mutex = new Mutex(initiallyOwned: true, $@"Local\QuickMail-{key}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            if (signalExisting)
            {
                // A .eml file to open (#939): left for the running instance before it is signalled,
                // which then takes it up along with coming to the front.
                if (EmailFileFrom(args) is { } path) LeaveOpenRequest(key, path);
                SignalExistingInstance(key);
            }
            return null;
        }

        var activateRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName(key));
        return new SingleInstanceService(mutex, activateRequested, key);
    }

    /// <summary>
    /// Starts listening for activation signals from later launches. The callback fires on a
    /// thread-pool thread; the caller is responsible for marshaling to the UI thread.
    /// </summary>
    public void ListenForActivation(Action onActivateRequested)
    {
        _waitRegistration ??= ThreadPool.RegisterWaitForSingleObject(
            _activateRequested,
            (_, _) => onActivateRequested(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    // Design note (issue #253): the activation signal is a bare auto-reset event that carries no
    // payload — it tells the running instance "come to the foreground", nothing more. A second
    // launch that arrives with toast-activation arguments (open a specific message) therefore
    // brings the window forward but drops the account/folder/message deep-link. This is a
    // deliberately accepted trade-off, not a bug: the case only occurs when the running instance's
    // in-process toast COM registration has failed AND a stale toast survives to COM-launch a
    // second exe — otherwise activation is delivered in-process and never reaches this path.
    // Forwarding the payload would require real inter-process data transfer (e.g. a named pipe
    // carrying the command line) on the single-instance/startup path, which this app has a history
    // of hangs and zombie processes on; the low likelihood does not justify that added surface.
    // If this is ever revisited, this method (and ActivateEventName) is the seam to extend.
    private static void SignalExistingInstance(string key)
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ActivateEventName(key));
            evt.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The owning instance is mid-startup (mutex created, event not yet) or mid-exit.
            // There is nothing to activate; this launch just ends.
        }
    }

    private static string ActivateEventName(string key) => $@"Local\QuickMail-{key}-activate";

    // ── Files to open (#939) ─────────────────────────────────────────────────
    //
    // The activation signal carries nothing (see SignalExistingInstance), and the startup path has a
    // history of hangs that rules out a pipe. A .eml file opened from Explorer while QuickMail is
    // running is therefore handed over as a small file: the new launch writes the path into a folder
    // named for the profile, then signals, and the running instance takes whatever is waiting there.

    /// <summary>
    /// The .eml file QuickMail was started to open, as Explorer's Open with passes it: a plain path
    /// argument, not an option. Null when there is none.
    /// </summary>
    public static string? EmailFileFrom(string[] args) =>
        args.FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('/')
                                 && string.Equals(Path.GetExtension(a), ".eml", StringComparison.OrdinalIgnoreCase));

    internal static string OpenRequestFolder(string key) =>
        Path.Combine(Path.GetTempPath(), "QuickMail", "Open", key);

    internal static void LeaveOpenRequest(string key, string path)
    {
        try
        {
            var folder = OpenRequestFolder(key);
            Directory.CreateDirectory(folder);
            // Written under a temporary name and renamed, so the running instance never reads half a path.
            var name = $"{DateTime.UtcNow.Ticks:D20}-{Guid.NewGuid():N}";
            var writing = Path.Combine(folder, name + ".tmp");
            File.WriteAllText(writing, Path.GetFullPath(path));
            File.Move(writing, Path.Combine(folder, name + ".req"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Nothing to open with, but the running window still comes to the front.
            LogService.Log("SingleInstance: could not hand over a file to open", ex);
        }
    }

    /// <summary>
    /// The files later launches asked this instance to open, oldest first, each taken only once.
    /// </summary>
    public IReadOnlyList<string> TakeOpenRequests() => TakeOpenRequests(_key);

    internal static IReadOnlyList<string> TakeOpenRequests(string key)
    {
        var folder = OpenRequestFolder(key);
        var paths = new List<string>();
        try
        {
            if (!Directory.Exists(folder)) return paths;
            foreach (var request in Directory.GetFiles(folder, "*.req").Order(StringComparer.Ordinal))
            {
                try
                {
                    var path = File.ReadAllText(request).Trim();
                    File.Delete(request);
                    if (path.Length > 0) paths.Add(path);
                }
                catch (IOException) { /* being written or taken by someone else: next signal */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("SingleInstance: could not read files to open", ex);
        }
        return paths;
    }
    /// <summary>
    /// Derives a fixed-length identity for the profile directory chosen by the command line,
    /// so the kernel object names stay valid regardless of path length or characters.
    /// Letter case and a trailing separator do not change the identity.
    /// </summary>
    internal static string ProfileKey(string[] args)
    {
        var raw = ProfileContext.ParseProfileDir(args);
        string dir;
        if (raw is null)
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        }
        else
        {
            // An unparseable path still yields a stable key; startup rejects it with an
            // error dialog before any second instance could matter.
            try { dir = Path.GetFullPath(raw); }
            catch (Exception) { dir = raw; }
        }

        dir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dir));
        return Convert.ToHexString(hash, 0, 16);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _waitRegistration?.Unregister(null);
        _activateRequested.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException)
        {
            // Not owned by this thread — closing the handle below still frees the
            // kernel object once the process exits.
        }
        _mutex.Dispose();
    }
}
