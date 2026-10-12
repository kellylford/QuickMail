using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuickMail.Services;

/// <summary>
/// Rewrites account ids in the text of a profile file (issue #507, spec Revision 2 R4).
///
/// <para>Rules, saved views, contacts, per-account settings, shared mailboxes, the default
/// calendar and per-folder view state all refer to accounts by id, in several shapes — a JSON
/// property, an INI section header, the front of a <c>"{id}|{calendar}"</c> string. Rewriting the
/// text catches all of them, including a reference added to some model later, where a rewrite
/// through typed models would silently miss it. Account ids are random, so text that merely
/// looks like one is not a realistic concern.</para>
///
/// <para>One pass, every id at once: rewriting ids one after another would turn A→B, B→C into
/// A→C.</para>
/// </summary>
public static class AccountIdRewriter
{
    public static string Rewrite(string text, IReadOnlyDictionary<Guid, Guid> map)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var effective = map.Where(kv => kv.Key != kv.Value).ToList();
        if (effective.Count == 0) return text;

        // Both the hyphenated form every serializer here writes and the bare 32-digit form, in
        // either case. Longest alternatives first so the hyphenated form wins over a partial match.
        var lookup = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in effective)
        {
            lookup[from.ToString("D")] = to;
            lookup[from.ToString("N")] = to;
        }
        var pattern = string.Join("|", lookup.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        return Regex.Replace(text, pattern, m =>
        {
            var to = lookup[m.Value];
            var hyphenated = m.Value.Length == 36;
            var replacement = to.ToString(hyphenated ? "D" : "N");
            // Keep the case the file used, so an upper-case id stays upper-case.
            return m.Value.Any(char.IsUpper) && !m.Value.Any(char.IsLower) ? replacement.ToUpperInvariant() : replacement;
        }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>True when any id in <paramref name="ids"/> appears in <paramref name="text"/>, in either form.</summary>
    public static bool ContainsAny(string text, IEnumerable<Guid> ids) =>
        ids.Any(id => text.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)
                   || text.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase));
}
