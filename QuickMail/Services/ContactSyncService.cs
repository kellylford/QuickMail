using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <inheritdoc cref="IContactSyncService"/>
public sealed class ContactSyncService : IContactSyncService
{
    private readonly IAccountService _accounts;
    private readonly IContactService _contacts;
    private readonly IProviderContactSource _microsoftSource;
    private readonly IProviderContactSource _googleSource;

    // Process-lifetime throttle for the automatic background trigger. UtcNow is only read here (not in
    // a resumable/replayable context), so it's safe.
    private readonly object _throttleLock = new();
    private DateTimeOffset _lastAutoSyncUtc = DateTimeOffset.MinValue;

    public ContactSyncService(
        IAccountService accounts,
        IContactService contacts,
        IProviderContactSource microsoftSource,
        IProviderContactSource googleSource)
    {
        _accounts        = accounts;
        _contacts        = contacts;
        _microsoftSource = microsoftSource;
        _googleSource    = googleSource;
    }

    public bool CanSync(AccountModel account) => SourceFor(account) is not null;

    private IProviderContactSource? SourceFor(AccountModel account) => account.AuthType switch
    {
        // Keyed by auth type, not backend: a Gmail account is IMAP + Google OAuth (not the Graph
        // backend), and an Outlook.com account can be IMAP + Microsoft OAuth. The contact API follows
        // the identity provider, so a valid OAuth token is what makes contact sync possible.
        AuthType.OAuth2Microsoft => _microsoftSource,
        AuthType.OAuth2Google    => _googleSource,
        _                        => null,
    };

    public async Task<ContactSyncResult> SyncAccountAsync(AccountModel account, CancellationToken ct = default)
    {
        var source = SourceFor(account);
        if (source is null) return ContactSyncResult.None;

        try
        {
            var fetched = await source.FetchAsync(account, ct);
            await _contacts.ReplaceSyncedContactsAsync(account.Id, source.Source, fetched);
            LogService.Log($"ContactSync: {account.AccountLabel} — {fetched.Count} contact(s) synced.");
            return new ContactSyncResult(1, fetched.Count, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InteractiveSignInRequiredException ex)
        {
            // The read-only contact grant has lapsed. We never open an interactive window from sync
            // (it could deadlock the modal address book), so tell the user how to restore it.
            LogService.Log($"ContactSync: sign-in required for {account.AccountLabel} — {ex.Message}");
            return new ContactSyncResult(0, 0,
                $"sign-in needed — turn contact sync off and on again for {account.AccountLabel} in account settings.");
        }
        catch (Exception ex)
        {
            // Best-effort: log and report, never throw. A contact-sync failure must not break the
            // mail-sync caller (spec §5.1, §13.1).
            LogService.Log($"ContactSync failed for {account.AccountLabel}", ex);
            return new ContactSyncResult(0, 0, ex.Message);
        }
    }

    public async Task<ContactSyncResult> SyncAllAsync(CancellationToken ct = default)
    {
        int accountsSynced = 0, contactsFetched = 0;
        string? error = null;

        foreach (var account in _accounts.LoadAccounts())
        {
            if (!account.SyncContacts || !CanSync(account)) continue;
            ct.ThrowIfCancellationRequested();

            var result = await SyncAccountAsync(account, ct);
            accountsSynced  += result.AccountsSynced;
            contactsFetched += result.ContactsFetched;
            if (result.Error is not null) error = result.Error; // last error wins; each is logged
        }

        return new ContactSyncResult(accountsSynced, contactsFetched, error);
    }

    public Task<ContactSyncResult> SyncAllDueAsync(TimeSpan minInterval, CancellationToken ct = default)
    {
        lock (_throttleLock)
        {
            if (DateTimeOffset.UtcNow - _lastAutoSyncUtc < minInterval)
                return Task.FromResult(ContactSyncResult.None);
            // Stamp on attempt (not on success): a transient failure shouldn't cause a full re-fetch
            // on the next reconnect/activation within the window. The manual command and the next
            // launch both provide a way to retry sooner.
            _lastAutoSyncUtc = DateTimeOffset.UtcNow;
        }
        return SyncAllAsync(ct);
    }

    public Task RemoveAccountContactsAsync(Guid accountId, CancellationToken ct = default)
        => _contacts.RemoveSyncedContactsAsync(accountId);
}
