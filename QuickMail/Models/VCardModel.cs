using System;
using System.Collections.Generic;
using System.Text;
using QuickMail.Services;

namespace QuickMail.Models;

/// <summary>One contact parsed out of a vCard body: the fields QuickMail's address book needs.</summary>
public sealed record ParsedVCard(string Uid, string DisplayName, string Email);

/// <summary>
/// Minimal RFC 6350 vCard reader for CardDAV contact sync (read-down v1). Extracts UID, a display
/// name (FN, falling back to the structured N), and the first EMAIL. Mirrors
/// <see cref="IcsModel"/>'s parsing style: identical line-unfolding, a BEGIN/END block scan, and
/// the same text unescaping (<c>\, \; \n \\</c>). Never throws — malformed input yields no cards.
/// </summary>
public static class VCardModel
{
    /// <summary>
    /// Parses every VCARD block in a body. Cards without a usable EMAIL are still returned here
    /// (the caller drops them); a card without a UID gets a stable synthetic id from FN|EMAIL so
    /// it keeps its identity across re-syncs.
    /// </summary>
    public static IEnumerable<ParsedVCard> ParseAll(string body)
    {
        var result = new List<ParsedVCard>();
        if (string.IsNullOrWhiteSpace(body)) return result;

        try
        {
            var lines = UnfoldLines(body);
            string? uid = null, fn = null, structuredName = null, email = null;
            var inCard = false;

            void Flush()
            {
                var displayName = !string.IsNullOrWhiteSpace(fn) ? fn! : structuredName ?? string.Empty;
                var mail = email ?? string.Empty;
                var id = !string.IsNullOrWhiteSpace(uid)
                    ? uid!
                    : CardDavContactClient.SyntheticUid($"{displayName}|{mail}");
                result.Add(new ParsedVCard(id, displayName.Trim(), mail.Trim()));
                uid = fn = structuredName = email = null;
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
                {
                    uid = fn = structuredName = email = null; // defensive: unterminated previous card
                    inCard = true;
                    continue;
                }
                if (line.StartsWith("END:VCARD", StringComparison.OrdinalIgnoreCase))
                {
                    if (inCard) Flush();
                    inCard = false;
                    continue;
                }
                if (!inCard) continue;

                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0) continue;

                var prop = line[..colonIdx];
                var value = line[(colonIdx + 1)..];

                // Strip property parameters, e.g. "EMAIL;TYPE=INTERNET,HOME" → "EMAIL".
                var semicolonIdx = prop.IndexOf(';');
                var propName = (semicolonIdx >= 0 ? prop[..semicolonIdx] : prop);

                // Strip a vCard group prefix (RFC 6350 §3.3), e.g. Apple/iCloud emit custom-labelled
                // emails as "item1.EMAIL;type=pref:…". Without this the property name reads as
                // "ITEM1.EMAIL", no case matches, and the contact's email — and thus the whole
                // contact — is silently dropped. Groups apply to any property, so strip before the dot.
                var dotIdx = propName.LastIndexOf('.');
                if (dotIdx >= 0) propName = propName[(dotIdx + 1)..];

                propName = propName.ToUpperInvariant();

                switch (propName)
                {
                    case "UID":
                        uid = UnescapeText(value).Trim();
                        break;
                    case "FN":
                        fn = UnescapeText(value);
                        break;
                    case "N":
                        // Structured name "Family;Given;Additional;Prefix;Suffix" — only a fallback
                        // when FN is absent. Presented as "Given Family", then any remaining parts.
                        structuredName ??= JoinStructuredName(value);
                        break;
                    case "EMAIL":
                        // First non-empty EMAIL wins (a card may list several).
                        var e = UnescapeText(value).Trim();
                        if (email is null && e.Length > 0) email = e;
                        break;
                }
            }
            if (inCard) Flush(); // defensive: truncated input missing END:VCARD

            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Builds a display name from a structured N value ("Family;Given;Add;Prefix;Suffix").</summary>
    private static string JoinStructuredName(string value)
    {
        var parts = value.Split(';');
        var family = parts.Length > 0 ? UnescapeText(parts[0]).Trim() : string.Empty;
        var given  = parts.Length > 1 ? UnescapeText(parts[1]).Trim() : string.Empty;

        var ordered = new List<string>();
        if (given.Length > 0)  ordered.Add(given);
        if (family.Length > 0) ordered.Add(family);
        // Additional name / prefix / suffix, if the common two were empty, so at least something shows.
        for (var i = 2; i < parts.Length; i++)
        {
            var p = UnescapeText(parts[i]).Trim();
            if (p.Length > 0) ordered.Add(p);
        }
        return string.Join(' ', ordered);
    }

    /// <summary>vCard content lines fold identically to iCal (continuation begins with space/tab).</summary>
    private static List<string> UnfoldLines(string content)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0 && (trimmed[0] == ' ' || trimmed[0] == '\t'))
            {
                current.Append(trimmed[1..]);
            }
            else
            {
                if (current.Length > 0)
                    result.Add(current.ToString());
                current.Clear();
                current.Append(trimmed);
            }
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static string UnescapeText(string text)
    {
        return text
            .Replace("\\n", "\n")
            .Replace("\\N", "\n")
            .Replace("\\,", ",")
            .Replace("\\;", ";")
            .Replace("\\\\", "\\");
    }
}
