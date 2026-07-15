using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Fetches an account's contacts and prior recipients from its mail provider, normalized to
/// <see cref="ContactModel"/> (issue #256). Each provider (Microsoft Graph, Google People) has one
/// implementation. The returned models carry <see cref="ContactModel.SourceId"/>,
/// <see cref="ContactModel.EmailAddress"/>, <see cref="ContactModel.DisplayName"/> and
/// <see cref="ContactModel.IsPriorRecipient"/>; <c>Source</c>/<c>OwnerAccountId</c>/<c>Id</c> are
/// assigned by <see cref="IContactService.ReplaceSyncedContactsAsync"/> during the merge.
/// </summary>
public interface IProviderContactSource
{
    /// <summary>Which <see cref="ContactSource"/> this source produces.</summary>
    ContactSource Source { get; }

    /// <summary>
    /// Fetches saved contacts and prior recipients for the account. Emails are de-duplicated within
    /// the provider result (a saved contact takes precedence over the same address as a prior
    /// recipient). Throws on transport/auth failure so the caller can surface a sync error.
    /// </summary>
    Task<IReadOnlyList<ContactModel>> FetchAsync(AccountModel account, CancellationToken ct = default);
}
