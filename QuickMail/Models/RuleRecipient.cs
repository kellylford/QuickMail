using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QuickMail.Models;

/// <summary>
/// One recipient entry in a server rule — a From address, a Sent-to address, or a Forward-to address —
/// carrying the display name alongside the address, as Graph's <c>recipient</c> type does.
/// <para>
/// The name is not decoration. QuickMail used to keep the address alone and write back <c>{ address }</c>,
/// so every save stripped the names from a rule made in Outlook, and Outlook Classic then switched such a
/// rule off as one it could not upload (#810). The name now goes through the model, the mapper and the
/// editor, and a save always sends one: where nobody gave a name, the address stands in for it, which is
/// the form Exchange itself uses for Outlook's address-only entries.
/// </para>
/// </summary>
public sealed record RuleRecipient(string Address, string? Name = null)
{
    /// <summary>The name to send to the server: the display name, or the address where there is none.</summary>
    public string NameOrAddress => string.IsNullOrWhiteSpace(Name) ? Address : Name.Trim();

    /// <summary>True when the entry has a name that says something the address does not. A name equal to the
    /// address (Exchange's form for an address-only entry) is not shown, so the editor reads exactly as it did.</summary>
    public bool HasDistinctName
        => !string.IsNullOrWhiteSpace(Name) && !string.Equals(Name.Trim(), Address, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The entry as the editor's address box shows it: <c>Name &lt;address&gt;</c>, or the bare address
    /// where there is no distinct name. A name holding a separator, a quote or an angle bracket is quoted,
    /// so <see cref="ParseList"/> reads it back as one entry — an Exchange address book routinely names
    /// people "Last, First", and splitting that at the comma would turn one recipient into two.
    /// </summary>
    public string ToEditorText()
        => HasDistinctName ? QuoteIfNeeded(Name!.Trim()) + " <" + Address + ">" : Address;

    /// <summary>The entry as rule summaries say it: "Name (address)", or the address alone.</summary>
    public string ToDisplayText()
        => HasDistinctName ? Name!.Trim() + " (" + Address + ")" : Address;

    public override string ToString() => ToDisplayText();

    /// <summary>The editor text for a whole list, entries separated by a comma.</summary>
    public static string FormatList(IEnumerable<RuleRecipient> recipients)
        => string.Join(", ", recipients.Select(r => r.ToEditorText()));

    /// <summary>
    /// Parses an address box ("a@b.com, Bob &lt;bob@c.com&gt;; \"Ford, Kelly\" &lt;k@d.com&gt;"). Commas and
    /// semicolons separate entries except inside double quotes or angle brackets. An entry in the form
    /// <c>Name &lt;address&gt;</c> keeps its name; anything else is taken whole as the address, with no name.
    /// Entries that come to nothing are dropped, as the plain split before it dropped them.
    /// </summary>
    public static List<RuleRecipient> ParseList(string? text)
    {
        var list = new List<RuleRecipient>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (var token in SplitEntries(text))
        {
            if (ParseEntry(token) is { } recipient) list.Add(recipient);
        }
        return list;
    }

    private static IEnumerable<string> SplitEntries(string text)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        var inAngle = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes && ch == '\\' && i + 1 < text.Length)
            {
                // A backslash escape inside quotes: keep both characters for ParseEntry to unescape.
                sb.Append(ch).Append(text[++i]);
                continue;
            }
            if (ch == '"' && !inAngle) inQuotes = !inQuotes;
            else if (ch == '<' && !inQuotes) inAngle = true;
            else if (ch == '>' && !inQuotes) inAngle = false;
            else if ((ch == ',' || ch == ';') && !inQuotes && !inAngle)
            {
                yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        yield return sb.ToString();
    }

    private static RuleRecipient? ParseEntry(string token)
    {
        var t = token.Trim();
        if (t.Length == 0) return null;

        var open = t.LastIndexOf('<');
        if (t.EndsWith('>') && open >= 0)
        {
            var address = t[(open + 1)..^1].Trim();
            if (address.Length == 0) return null;
            var name = Unquote(t[..open].Trim());
            return new RuleRecipient(address, string.IsNullOrWhiteSpace(name) ? null : name);
        }

        return new RuleRecipient(t);
    }

    private static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var sb = new StringBuilder();
        for (var i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length - 1) i++;
            sb.Append(s[i]);
        }
        return sb.ToString().Trim();
    }

    private static string QuoteIfNeeded(string name)
    {
        if (name.IndexOfAny([',', ';', '"', '<', '>', '\\']) < 0) return name;
        return "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
