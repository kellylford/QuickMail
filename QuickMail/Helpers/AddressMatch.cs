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

    // Characters that can appear inside an address (RFC 5322 atext plus '.' and '@'). Seeing one
    // immediately before or after a hit means the hit is only part of a longer address.
    private static bool IsAddressChar(char c) =>
        char.IsLetterOrDigit(c) ||
        c is '.' or '@' or '-' or '_' or '+' or '%' or '!' or '#' or '$' or '&' or '\''
          or '*' or '/' or '=' or '?' or '^' or '`' or '{' or '|' or '}' or '~';
}
