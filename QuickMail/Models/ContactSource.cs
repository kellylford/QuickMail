namespace QuickMail.Models;

/// <summary>
/// Where a <see cref="ContactModel"/> came from. <see cref="Local"/> contacts are created
/// by the user (Add, Grab Addresses, compose upsert) and are never touched by a server sync.
/// The other values mark contacts pulled from a mail provider by the contact-sync feature
/// (issue #256); those are read-only in the address book and are replaced wholesale on each
/// re-sync of their owning account.
/// </summary>
public enum ContactSource
{
    /// <summary>User-owned contact stored only in QuickMail. The default for every existing entry.</summary>
    Local = 0,

    /// <summary>Synced from a Microsoft account via Graph (<c>/me/contacts</c> and <c>/me/people</c>).</summary>
    Microsoft = 1,

    /// <summary>Synced from a Google account via the People API (connections and other contacts).</summary>
    Google = 2,

    /// <summary>Synced from an iCloud account via CardDAV (the account's address book).</summary>
    ICloud = 3,
}
