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
    private RegisteredWaitHandle? _waitRegistration;
    private bool _disposed;

    private SingleInstanceService(Mutex mutex, EventWaitHandle activateRequested)
    {
        _mutex = mutex;
        _activateRequested = activateRequested;
    }

    /// <summary>
    /// Tries to claim single-instance ownership for the profile selected by <paramref name="args"/>.
    /// Returns the guard on success. Returns null when another instance already owns the profile;
    /// in that case the running instance has been signaled to bring its window to the foreground
    /// and the caller should end the process immediately.
    /// </summary>
    public static SingleInstanceService? TryAcquire(string[] args)
    {
        var key = ProfileKey(args);
        var mutex = new Mutex(initiallyOwned: true, $@"Local\QuickMail-{key}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalExistingInstance(key);
            return null;
        }

        var activateRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName(key));
        return new SingleInstanceService(mutex, activateRequested);
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
