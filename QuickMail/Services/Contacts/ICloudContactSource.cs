using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Pulls contacts from an iCloud account over CardDAV (contacts.icloud.com), using the account's
/// own app-specific password — the same one IMAP uses, so there is no separate credential and no
/// OAuth. The CardDAV sibling of <see cref="GraphContactSource"/> / <see cref="GoogleContactSource"/>,
/// and the contact counterpart of the iCloud CalDAV calendar path. iCloud exposes no relevance-ranked
/// "prior recipients" API, so every returned contact is a saved address-book entry
/// (<see cref="ContactModel.IsPriorRecipient"/> = false).
/// </summary>
public sealed class ICloudContactSource : IProviderContactSource
{
    private const string ICloudCardDavUrl = "https://contacts.icloud.com";

    private readonly ICardDavContactClient _client;
    private readonly ICredentialService _credentials;

    // Resolved addressbook-collection URL per account (from discovery), so each account's home
    // addressbook is found once and reused. Cleared for an account on fetch failure so a stale
    // collection URL (e.g. addressbook recreated server-side) heals on the next pass — mirrors the
    // CalDAV calendar path's per-account discovery cache.
    private readonly ConcurrentDictionary<Guid, string> _addressbookUrlByAccount = new();

    public ICloudContactSource(ICardDavContactClient client, ICredentialService credentials)
    {
        _client      = client;
        _credentials = credentials;
    }

    public ContactSource Source => ContactSource.ICloud;

    public async Task<IReadOnlyList<ContactModel>> FetchAsync(AccountModel account, CancellationToken ct = default)
    {
        var password = _credentials.GetPassword(account.Id);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                $"no app-specific password saved for {account.Username} — re-enter it in Manage Accounts.");

        if (!_addressbookUrlByAccount.TryGetValue(account.Id, out var addressbookUrl))
        {
            var info = await _client.DiscoverAddressbookAsync(ICloudCardDavUrl, account.Username, password, ct);
            addressbookUrl = info.Url;
            _addressbookUrlByAccount[account.Id] = addressbookUrl;
        }

        List<string> vcards;
        try
        {
            vcards = await _client.FetchVCardsAsync(addressbookUrl, account.Username, password, ct);
        }
        catch
        {
            _addressbookUrlByAccount.TryRemove(account.Id, out _); // stale collection URL? re-discover next pass
            throw;
        }

        var results = new List<ContactModel>();
        // De-dup emails within the provider result (a card can repeat an address).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var body in vcards)
        {
            foreach (var card in VCardModel.ParseAll(body))
            {
                // Every returned contact MUST have a non-empty stable SourceId and email or the
                // store drops it, so skip cards missing either here.
                if (string.IsNullOrWhiteSpace(card.Uid) || string.IsNullOrWhiteSpace(card.Email)) continue;
                if (!seen.Add(card.Email)) continue;
                results.Add(new ContactModel
                {
                    SourceId         = card.Uid,
                    DisplayName      = card.DisplayName,
                    EmailAddress     = card.Email,
                    IsPriorRecipient = false,
                });
            }
        }

        return results;
    }
}
