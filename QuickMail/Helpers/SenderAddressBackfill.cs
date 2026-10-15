using System;
using MimeKit;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Works out a cached list row's sender address from what is already on disk, for the one-time backfill
/// that follows the from_email column (#803). Pure, so every shape a row can be in is testable without a
/// database. Rows these rules cannot answer are IMAP rows with no cached body — even one whose display text
/// looks like an address, since a sender can make their name look like one; the sync fetches those from the
/// server.
/// </summary>
public static class SenderAddressBackfill
{
    /// <summary>What a row becomes: its new display text (null to leave it) and its address.</summary>
    public readonly record struct Result(string? From, string FromEmail);

    /// <summary>
    /// The row's display text and address, or null when nothing on disk says what the address is.
    /// </summary>
    /// <param name="fromDisp">The row's from_disp.</param>
    /// <param name="detailFromAddr">The cached body's full sender (MessageDetail.from_addr), or empty.</param>
    /// <param name="isGraphRow">
    /// Whether the row belongs to a Microsoft Graph account, whose from_disp held "Name &lt;address&gt;"
    /// until #801. Only those rows are split, and only those rows may take their display text as the address:
    /// see the comment at that rule.
    /// </param>
    public static Result? For(string fromDisp, string? detailFromAddr, bool isGraphRow)
    {
        if (string.IsNullOrWhiteSpace(fromDisp)) return null;

        // An IMAP or POP3 display name is the sender's own text, and a name that happens to contain angle
        // brackets must not be cut apart. Splitting at the LAST " <" keeps a forged "Tom <ceo@bank.example>" whole as
        // the name and takes the real address that follows it.
        if (isGraphRow && TrySplitGraphHeader(fromDisp, out var name, out var address))
            return new Result(name, address);

        if (!string.IsNullOrWhiteSpace(detailFromAddr)
            && InternetAddressList.TryParse(detailFromAddr, out var list)
            && ImapMailService.FormatAddressListEmails(list) is { Length: > 0 } fromBody)
            return new Result(null, fromBody);

        // A Graph row with no name: AsHeaderString wrote the address alone only when the name was empty or was the
        // address itself, so on a Graph row this text IS the address. Never on an IMAP row: from_disp there is the
        // display name alone, and "boss@company.com" can be a forger's name in front of another address. With no
        // body to say otherwise, only the server knows, so the sync fetches it (review of #806).
        return isGraphRow && AsBareAddress(fromDisp) is { } bare ? new Result(null, bare) : null;
    }

    /// <summary>
    /// Splits the "Name &lt;address&gt;" form <c>GraphEmailAddress.AsHeaderString</c> wrote into its parts.
    /// That form is never quoted or encoded, so it is taken apart literally rather than parsed as a header —
    /// an unquoted "Ford, Kelly &lt;kelly@example.com&gt;" would otherwise read as two addresses.
    /// </summary>
    internal static bool TrySplitGraphHeader(string text, out string name, out string address)
    {
        name = address = string.Empty;
        var trimmed = text.Trim();
        if (!trimmed.EndsWith('>')) return false;
        var open = trimmed.LastIndexOf(" <", StringComparison.Ordinal);
        if (open <= 0) return false;

        var candidate = trimmed[(open + 2)..^1].Trim();
        if (AsBareAddress(candidate) is not { } bare) return false;

        name = trimmed[..open].Trim();
        address = bare;
        return name.Length > 0;
    }

    /// <summary>The text itself when it is one bare email address and nothing else; otherwise null.</summary>
    internal static string? AsBareAddress(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || !trimmed.Contains('@', StringComparison.Ordinal)) return null;
        foreach (var c in trimmed)
            if (char.IsWhiteSpace(c) || c is '<' or '>' or ',' or ';' or '"') return null;
        return MailboxAddress.TryParse(trimmed, out var mailbox) && string.IsNullOrEmpty(mailbox.Name)
            ? mailbox.Address
            : null;
    }
}
