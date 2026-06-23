using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Aggregates per-backend <see cref="IChangeNotifier"/> implementations behind one surface, the way
/// <see cref="MailServiceRouter"/> does for <see cref="IMailService"/>. <see cref="StartWatchers"/>
/// partitions accounts by backend so each notifier only watches the accounts it owns; events from
/// every backend are forwarded to a single set of subscribers.
///
/// PR 7a wires only the IMAP notifier (a held IDLE connection per account, implemented by
/// <see cref="ImapMailService"/> itself — IMAP's IDLE watcher is bound to the IMAP connection
/// lifecycle, so it is not a separate object). PR 7b adds a Graph delta-poll notifier and routes
/// Microsoft Graph accounts to it; IMAP accounts continue to go to the IMAP notifier.
/// </summary>
public sealed class ChangeNotifierRouter : IChangeNotifier, IDisposable
{
    private readonly IChangeNotifier _imap;

    public ChangeNotifierRouter(IChangeNotifier imapNotifier)
    {
        _imap = imapNotifier;
        _imap.InboxNewMailDetected += OnInboxNewMail;
        _imap.AccountReachabilityChanged += OnReachabilityChanged;
    }

    public event Action<Guid>? InboxNewMailDetected;
    public event Action<Guid, bool>? AccountReachabilityChanged;

    private void OnInboxNewMail(Guid accountId) => InboxNewMailDetected?.Invoke(accountId);
    private void OnReachabilityChanged(Guid accountId, bool isReachable) => AccountReachabilityChanged?.Invoke(accountId, isReachable);

    public void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default)
    {
        // IMAP IDLE watches only IMAP accounts. Graph accounts get no watcher until PR 7b adds the
        // delta-poll notifier; routing them here would attempt a doomed IMAP connection.
        var imapAccounts = accounts.Where(a => a.BackendKind == BackendKind.ImapSmtp).ToList();
        _imap.StartWatchers(imapAccounts, ct);
    }

    public void StopWatchers() => _imap.StopWatchers();

    public void Dispose()
    {
        _imap.InboxNewMailDetected -= OnInboxNewMail;
        _imap.AccountReachabilityChanged -= OnReachabilityChanged;
    }
}
