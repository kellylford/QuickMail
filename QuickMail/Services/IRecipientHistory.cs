using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// The people messages have been sent to, remembered so compose can suggest them (#1037). Kept apart
/// from the address book on purpose: everyone ever written to would otherwise crowd out the contacts
/// the user saved. Whether anything is recorded or suggested is the caller's decision
/// (<see cref="ConfigModel.RememberRecipients"/>); this store only keeps what it is given.
/// </summary>
public interface IRecipientHistory
{
    /// <summary>
    /// Records one sent message's recipients: adds the new ones and marks every one as just used.
    /// A name given here replaces a remembered one; an address sent with no name keeps the name it had.
    /// </summary>
    Task RecordAsync(IReadOnlyList<(string DisplayName, string EmailAddress)> recipients);

    /// <summary>
    /// Remembered people whose name or address contains <paramref name="text"/> (case-insensitive),
    /// most recently used first, at most <paramref name="max"/>.
    /// </summary>
    Task<List<ContactModel>> SearchAsync(string text, int max, CancellationToken ct = default);

    /// <summary>How many people are remembered.</summary>
    Task<int> CountAsync();

    /// <summary>Forgets everyone.</summary>
    Task ClearAsync();
}
