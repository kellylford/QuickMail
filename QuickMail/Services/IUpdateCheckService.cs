using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IUpdateCheckService
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the running copy updates itself (Velopack install) and CheckForUpdateAsync
    /// found an update. The UI should then offer restart-to-update rather than a download link.
    /// </summary>
    bool SelfUpdatePending { get; }

    /// <summary>
    /// Waits for the pending update download to finish (retrying the download once if the
    /// background attempt failed), then applies it and restarts the app (the call does not
    /// return on success — the process exits). Returns false when there is no pending
    /// self-update, the caller cancelled (e.g. the update dialog was dismissed), or applying
    /// failed; failures are logged.
    /// </summary>
    Task<bool> RestartToUpdateAsync(CancellationToken cancellationToken = default);
}
