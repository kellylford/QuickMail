using System;

namespace QuickMail.Helpers;

/// <summary>
/// Finding an address, or a domain, in a header or address list as a whole address rather than as part of a longer
/// one. Find Mail From Contact and client rules both need it (review of #806): a plain substring test reports
/// "bob@example.com" in "notbob@example.com" and in "bob@example.com.evil.example", and the last is any sender's
/// for the price of a subdomain.
/// </summary>
internal static class AddressMatch
{
    /// <summary>
    /// True when <paramref name="header"/> contains <paramref name="address"/> on address boundaries: anything but an
    /// address character on either side, which is what the surrounding "&lt;", "&gt;", quotes, commas and spaces in a
    /// header supply. A value starting with "@" is a domain, so only its end needs a boundary: "@example.com" is in
    /// "bob@example.com" but not in "bob@example.com.evil.example".
    /// </summary>
    internal static bool Names(string? header, string? address)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(address)) return false;
        var isDomain = address[0] == '@';

        var i = header.IndexOf(address, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var end = i + address.Length;
            if ((isDomain || i == 0 || !IsAddressChar(header[i - 1])) &&
                (end >= header.Length || !IsAddressChar(header[end])))
                return true;
            if (i + 1 >= header.Length) break;
            i = header.IndexOf(address, i + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// True when a sender value is matched as a whole address or domain rather than as text: a "Name &lt;address&gt;",
    /// or text with a dot somewhere after its '@' ("service@paypal.com", "@paypal.com"). A fragment such as "@acme" or
    /// "john.smith@", and anything without an '@', is a substring match. <c>RuleService.SenderMatches</c> and the
    /// Rules Manager's row wording both ask this, so what a row says can't drift from what the rule does.
    /// </summary>
    internal static bool IsWhole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Contains('<', StringComparison.Ordinal))
            return MimeKit.MailboxAddress.TryParse(trimmed, out var mailbox) && !string.IsNullOrEmpty(mailbox.Address);
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 && trimmed.IndexOf('.', at + 1) > at;
    }

    // Characters that can appear inside an address (RFC 5322 atext plus '.' and '@'). Seeing one
    // immediately before or after a hit means the hit is only part of a longer address.
    private static bool IsAddressChar(char c) =>
        char.IsLetterOrDigit(c) ||
        c is '.' or '@' or '-' or '_' or '+' or '%' or '!' or '#' or '$' or '&' or '\''
          or '*' or '/' or '=' or '?' or '^' or '`' or '{' or '|' or '}' or '~';
}
