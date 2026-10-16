using System.Linq;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// The three ways a header's address list is written into the cache: in full for the detail view, as display
/// names for the message list, and as bare addresses for matching (#803). IMAP, POP3 and the sender-address
/// backfill all build them here, so the message list reads the same whichever backend fetched a row, and a
/// second copy is never free to drift. Graph keeps its own equivalent (<c>GraphEmailAddress.AsDisplayString</c>),
/// since its addresses arrive as JSON rather than an <see cref="InternetAddressList"/>.
/// </summary>
internal static class AddressListFormat
{
    /// <summary>Full addresses ("Kelly Ford &lt;kelly@example.com&gt;"), for the detail view.</summary>
    internal static string Full(InternetAddressList? list) =>
        list == null || list.Count == 0
            ? string.Empty
            : string.Join(", ", list.Select(a => a.ToString()));

    /// <summary>
    /// Display names where there is one, otherwise the address, for the message list's From column: a nameless
    /// sender is never shown or spoken as blank.
    /// </summary>
    internal static string Display(InternetAddressList? list) =>
        list == null || list.Count == 0
            ? string.Empty
            : string.Join(", ", list.Select(a =>
                a is MailboxAddress mb && !string.IsNullOrWhiteSpace(mb.Name)
                    ? mb.Name
                    : a.ToString()));

    /// <summary>
    /// Bare addresses alone ("kelly@example.com"), for <see cref="MailMessageSummary.FromEmail"/>: what rules,
    /// Find Mail From Contact and search match on, since <see cref="Display"/> drops the address whenever there
    /// is a name (#803). Never shown. A group's members are listed in place of the group.
    /// </summary>
    internal static string Emails(InternetAddressList? list) =>
        list == null || list.Count == 0
            ? string.Empty
            : string.Join(", ", list.Mailboxes
                .Select(mb => mb.Address)
                .Where(a => !string.IsNullOrWhiteSpace(a)));

    /// <summary>
    /// A cached row's sender name and bare address put back together as a From header, for a detail that has no
    /// From of its own: a reply addresses it and a saved file writes it, so it must parse back to the one mailbox.
    /// <see cref="MailMessageSummary.FromWithAddress"/> is for reading and matching and leaves the name as it is,
    /// so "Ford, Kelly &lt;kelly@example.com&gt;" would parse as two addresses; MimeKit quotes such a name
    /// (review of #806). With no address, the name alone; with several, the addresses alone, since the one name
    /// belongs to none of them in particular.
    /// </summary>
    internal static string Mailbox(string? name, string? address)
    {
        name ??= string.Empty;
        if (string.IsNullOrWhiteSpace(address)) return name;
        if (address.Contains(',', System.StringComparison.Ordinal)) return address;
        if (name.Length == 0 || string.Equals(name, address, System.StringComparison.OrdinalIgnoreCase)) return address;
        try { return new MailboxAddress(name, address).ToString(); }
        catch (ParseException) { return address; }
    }
}
