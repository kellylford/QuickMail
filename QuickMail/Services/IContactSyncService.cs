using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Outcome of a contact sync. <see cref="Error"/> is non-null when at least one account failed;
/// individual failures are also logged. One-way only — QuickMail never writes contacts back.
/// </summary>
public record ContactSyncResult(int AccountsSynced, int ContactsFetched, string? Error)
{
    public static ContactSyncResult None => new(0, 0, null);
}

/// <summary>
/// Orchestrates one-way (server → local) contact sync for the address book (issue #256). Picks a
/// provider source per account by <see cref="AccountModel.AuthType"/>, fetches contacts + prior
/// recipients, and hands them to <see cref="IContactService.ReplaceSyncedContactsAsync"/>. Best-effort:
/// a provider/auth failure is caught and reported, never thrown to a mail-sync caller.
/// </summary>
public interface IContactSyncService
{
    /// <summary>True if the account's auth type exposes a contact API (Microsoft or Google OAuth).</summary>
    bool CanSync(AccountModel account);

    /// <summary>
    /// Syncs one account regardless of its <see cref="AccountModel.SyncContacts"/> flag (used right
    /// after the user opts in). No-op for accounts without a contact source.
    /// </summary>
    Task<ContactSyncResult> SyncAccountAsync(AccountModel account, CancellationToken ct = default);

    /// <summary>Syncs every account with <see cref="AccountModel.SyncContacts"/> enabled and a contact source.</summary>
    Task<ContactSyncResult> SyncAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="SyncAllAsync"/> but throttled: does nothing if a sync ran less than
    /// <paramref name="minInterval"/> ago. Used by the automatic background trigger, which fires on
    /// every startup/reconnect/account-activation — without this it would re-fetch every account's
    /// full contact list each time. The manual "Sync Contacts Now" path bypasses this and always runs.
    /// </summary>
    Task<ContactSyncResult> SyncAllDueAsync(TimeSpan minInterval, CancellationToken ct = default);

    /// <summary>Removes an account's synced contacts (on disable or account deletion).</summary>
    Task RemoveAccountContactsAsync(Guid accountId, CancellationToken ct = default);
}
