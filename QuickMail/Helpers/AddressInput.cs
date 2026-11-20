using System.Collections.Generic;
using System.Linq;
using System.Text;
using MimeKit;

namespace QuickMail.Helpers;

/// <summary>
/// Reads and writes the address text of To, Cc and Bcc: what is typed or pasted into the address box, the
/// box's own serialization, and what the send path turns into recipients (#979, #980).
///
/// <para>
/// MimeKit's <see cref="InternetAddressList.TryParse(string, out InternetAddressList)"/> reads a semicolon as the
/// end of an RFC 5322 group, so a list such as <c>ann@example.com; ben@example.com</c> does not parse. That is the
/// separator Outlook copies recipients with, and the one QuickMail itself writes, so the address box fell back to
/// making one chip of the whole string. Here the text is first cut at semicolons, line breaks and tabs, never
/// inside a quoted name, a comment in parentheses or angle brackets, and each piece is then given to MimeKit,
/// which still handles commas, including an unquoted <c>Smith, John &lt;john@example.com&gt;</c>.
/// </para>
/// </summary>
internal static class AddressInput
{
    /// <summary>An address found in the text: the display name (possibly empty) and the address.</summary>
    internal readonly record struct Mailbox(string Name, string Address);

    /// <summary>The addresses found, in order, and any text that held no address, for the user to correct.</summary>
    internal readonly record struct Result(IReadOnlyList<Mailbox> Mailboxes, string Leftover);

    /// <summary>
    /// Cuts <paramref name="text"/> at semicolons, line breaks and tabs that are outside double quotes,
    /// parenthesised comments and angle brackets. Empty pieces are dropped; the rest are trimmed.
    /// </summary>
    internal static IReadOnlyList<string> SplitSegments(string? text)
    {
        var segments = new List<string>();
        if (string.IsNullOrEmpty(text)) return segments;

        var current = new StringBuilder();
        var inQuotes = false;
        var commentDepth = 0;
        var angleDepth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes || commentDepth > 0)
            {
                if (c == '\\' && i + 1 < text.Length) { current.Append(c).Append(text[++i]); continue; }
                if (inQuotes && c == '"') inQuotes = false;
                else if (!inQuotes && c == '(') commentDepth++;
                else if (!inQuotes && c == ')') commentDepth--;
                current.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case '(': commentDepth = 1; break;
                case '<': angleDepth++; break;
                case '>': if (angleDepth > 0) angleDepth--; break;
                case ';' or '\r' or '\n' or '\t' when angleDepth == 0:
                    Flush();
                    continue;
            }
            current.Append(c);
        }
        Flush();
        return segments;

        void Flush()
        {
            var s = current.ToString().Trim();
            if (s.Length > 0) segments.Add(s);
            current.Clear();
        }
    }

    /// <summary>
    /// Reads every address in <paramref name="text"/>. A piece MimeKit cannot read but that holds an <c>@</c> is
    /// kept whole as an address, as the address box always has, so validation can flag it rather than lose it. A
    /// piece with no address at all is returned in <see cref="Result.Leftover"/>, except a group with no members
    /// (<c>undisclosed-recipients:;</c>), which stands for nobody and is dropped.
    /// </summary>
    internal static Result Parse(string? text)
    {
        var mailboxes = new List<Mailbox>();
        var leftovers = new List<string>();
        foreach (var segment in SplitSegments(text))
        {
            var raw = segment.TrimEnd(',', ';').Trim();
            if (raw.Length == 0) continue;

            if (InternetAddressList.TryParse(raw, out var list))
            {
                var added = false;
                // Mailboxes walks into groups too, so "Team: a@x, b@x;" yields its members.
                foreach (var mb in list.Mailboxes)
                {
                    if (string.IsNullOrWhiteSpace(mb.Address) || !mb.Address.Contains('@')) continue;
                    mailboxes.Add(new Mailbox(mb.Name ?? string.Empty, mb.Address));
                    added = true;
                }
                if (added || list.OfType<GroupAddress>().Any()) continue;
            }

            if (raw.Contains('@')) mailboxes.Add(new Mailbox(string.Empty, raw));
            else leftovers.Add(raw);
        }
        return new Result(mailboxes, string.Join("; ", leftovers));
    }

    /// <summary>
    /// Adds every valid address in <paramref name="text"/> to <paramref name="list"/>, for building a message's
    /// headers. Pieces are cut as <see cref="SplitSegments"/> does, so a quoted "Smith, John" stays one person.
    /// A piece MimeKit cannot read as a whole is tried comma by comma, as all of it used to be, so one bad entry
    /// does not cost its neighbours; what still does not parse is left out.
    /// </summary>
    internal static void AddTo(InternetAddressList list, string? text)
    {
        foreach (var segment in SplitSegments(text))
        {
            if (InternetAddressList.TryParse(segment, out var parsed))
            {
                foreach (var mb in parsed.Mailboxes)
                    if (!string.IsNullOrWhiteSpace(mb.Address)) list.Add(mb);
                continue;
            }
            foreach (var part in segment.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0 && MailboxAddress.TryParse(trimmed, out var addr))
                    list.Add(addr);
            }
        }
    }

    /// <summary>
    /// One address as text that <see cref="Parse"/> and <see cref="AddTo"/> read back as the same person: the name
    /// is quoted when it holds a comma, semicolon or another RFC 5322 special.
    /// </summary>
    internal static string Format(string? name, string address)
    {
        if (string.IsNullOrWhiteSpace(name)) return address;
        var shown = name.AsSpan().IndexOfAny(HeaderSpecials) >= 0 ? MimeKit.Utils.MimeUtils.Quote(name) : name;
        return $"{shown} <{address}>";
    }

    /// <summary>
    /// Text pasted into the single-line address input with its line breaks and tabs turned into "; ", so a list
    /// with one address per line is not cut to its first line (#980). Null when the text has none, meaning the
    /// paste can go ahead unchanged.
    /// </summary>
    internal static string? FlattenForSingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOfAny(LineBreaksAndTabs) < 0) return null;
        var parts = new List<string>();
        foreach (var line in text.Split(LineBreaksAndTabs))
        {
            var t = line.Trim();
            if (t.Length > 0) parts.Add(t);
        }
        return string.Join("; ", parts);
    }

    private static readonly char[] LineBreaksAndTabs = ['\r', '\n', '\t'];

    // RFC 5322's specials: a display name holding any of them must be quoted to parse back as one phrase.
    private static readonly System.Buffers.SearchValues<char> HeaderSpecials = System.Buffers.SearchValues.Create("()<>[]:;@\\,.\"");
}
