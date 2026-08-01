using System;
using System.Collections.Generic;
using System.Threading;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Watches connected accounts for new mail and connectivity changes, raising events the UI
/// subscribes to. Extracted from <see cref="IMailService"/> (PR 7) so each backend can supply its
/// own notification strategy: IMAP holds an IDLE connection per account
/// (<see cref="ImapMailService"/>), and Microsoft Graph polls a delta query (added in PR 7b). The
/// per-backend notifiers are aggregated behind one surface by <see cref="ChangeNotifierRouter"/>.
/// </summary>
public interface IChangeNotifier
{
    /// <summary>
    /// Raised on the ThreadPool when new mail is detected in the given account's INBOX. Handlers
    /// should marshal to the UI thread if needed.
    /// </summary>
    event Action<Guid>? InboxNewMailDetected;

    /// <summary>
    /// Raised on the ThreadPool when the watcher observes that messages were removed from the given
    /// account's INBOX (deleted or moved away by another client). The list holds the backend message
    /// ids no longer present. Currently only the Graph delta poll can see this cheaply (via
    /// <c>@removed</c> entries, #366); IMAP IDLE gives no removal signal, so its removals are caught by
    /// reconcile-on-open / the periodic reconcile instead. Handlers marshal to the UI thread.
    /// </summary>
    event Action<Guid, IReadOnlyList<string>>? InboxMessagesRemoved;

    /// <summary>
    /// Raised when an account's watcher loses (false) or regains (true) connectivity. Fired on the
    /// ThreadPool; handlers should marshal to the UI thread if needed.
    /// </summary>
    event Action<Guid, bool>? AccountReachabilityChanged;

    /// <summary>
    /// Starts (or restarts) one watcher per account. Call after all accounts are connected. Safe to
    /// call repeatedly — an existing watcher for the same account is stopped and replaced.
    /// </summary>
    void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default);

    /// <summary>Stops all watchers.</summary>
    void StopWatchers();
}
