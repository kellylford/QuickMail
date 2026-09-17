using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MailKit.Search;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// Translates a <see cref="MessageSearchQuery"/> into what each kind of server searches with (#717, phase 3):
/// IMAP SEARCH criteria, Gmail's own search syntax (X-GM-RAW), and Microsoft Graph's KQL <c>$search</c>.
/// Each server understands a different subset; what a translation cannot express is left out, and the caller
/// checks the results against the query's conditions itself, so leaving something out widens what the server
/// returns but never lets a message through that the query rules out on its conditions.
/// <para><c>folder:</c> and <c>account:</c> are never sent: the caller chooses which accounts and folders to
/// ask.</para>
/// </summary>
public static class ServerSearchQuery
{
    /// <summary>
    /// IMAP SEARCH criteria, or null when there is nothing to ask for. Words anywhere become TEXT (headers and
    /// body), which also covers attachment names, since those are in the MIME headers.
    /// </summary>
    public static SearchQuery? ToImap(MessageSearchQuery query)
    {
        var parts = new List<SearchQuery>();
        foreach (var t in query.Terms)
        {
            // An excluded word has to be matched exactly where the query means it, or the server hides
            // messages that merely mention it somewhere else. IMAP has no attachment-name criterion, so an
            // excluded attachment word is left to the caller instead of becoming NOT TEXT.
            if (t.Negated && t.Field == SearchField.Attachment) continue;
            SearchQuery part = t.Field switch
            {
                SearchField.From       => SearchQuery.FromContains(t.Text),
                SearchField.To         => SearchQuery.ToContains(t.Text),
                SearchField.Cc         => SearchQuery.CcContains(t.Text),
                SearchField.Subject    => SearchQuery.SubjectContains(t.Text),
                SearchField.Body       => SearchQuery.BodyContains(t.Text),
                _                      => SearchQuery.MessageContains(t.Text),
            };
            parts.Add(t.Negated ? SearchQuery.Not(part) : part);
        }
        if (query.IsRead == true)     parts.Add(SearchQuery.Seen);
        if (query.IsRead == false)    parts.Add(SearchQuery.NotSeen);
        if (query.IsFlagged == true)  parts.Add(SearchQuery.Flagged);
        if (query.IsFlagged == false) parts.Add(SearchQuery.NotFlagged);
        if (query.After.HasValue)     parts.Add(SearchQuery.DeliveredAfter(query.After.Value));
        if (query.Before.HasValue)    parts.Add(SearchQuery.DeliveredBefore(query.Before.Value));
        // has:attachment has no IMAP criterion; the caller filters on it where the summary knows.

        if (parts.Count == 0) return null;
        return parts.Aggregate((a, b) => SearchQuery.And(a, b));
    }

    /// <summary>Gmail's search syntax for X-GM-RAW, or null when there is nothing to ask for.</summary>
    public static string? ToGmailRaw(MessageSearchQuery query)
    {
        var parts = new List<string>();
        foreach (var t in query.Terms)
        {
            // Gmail has no body-only operator, so an excluded body word would exclude messages that have it
            // in their subject or sender too. Left out; the caller still checks what comes back.
            if (t.Negated && t.Field == SearchField.Body) continue;
            var value = QuoteIfNeeded(t.Text, t.IsPhrase);
            var part = t.Field switch
            {
                SearchField.From       => "from:" + value,
                SearchField.To         => "to:" + value,
                SearchField.Cc         => "cc:" + value,
                SearchField.Subject    => "subject:" + value,
                SearchField.Attachment => "filename:" + value,
                // Gmail has no body-only operator; a plain word searches the whole message.
                _                      => value,
            };
            parts.Add((t.Negated ? "-" : string.Empty) + part);
        }
        if (query.HasAttachment == true)  parts.Add("has:attachment");
        if (query.HasAttachment == false) parts.Add("-has:attachment");
        if (query.IsRead == true)         parts.Add("is:read");
        if (query.IsRead == false)        parts.Add("is:unread");
        if (query.IsFlagged == true)      parts.Add("is:starred");
        if (query.IsFlagged == false)     parts.Add("-is:starred");
        if (query.After.HasValue)         parts.Add("after:" + query.After.Value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
        if (query.Before.HasValue)        parts.Add("before:" + query.Before.Value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// Microsoft Graph KQL for <c>$search</c>, before URL encoding and without the surrounding quotes Graph
    /// wants, or null when there is nothing Graph can search for. Read state and flags have no KQL property
    /// on messages, so they are left to the caller's check of the results.
    /// </summary>
    public static string? ToGraphKql(MessageSearchQuery query)
    {
        var parts = new List<string>();
        foreach (var t in query.Terms)
        {
            var value = QuoteIfNeeded(t.Text, t.IsPhrase, quote: "\\\"");
            var part = t.Field switch
            {
                SearchField.From       => "from:" + value,
                SearchField.To         => "to:" + value,
                SearchField.Cc         => "cc:" + value,
                SearchField.Subject    => "subject:" + value,
                SearchField.Body       => "body:" + value,
                SearchField.Attachment => "attachment:" + value,
                _                      => value,
            };
            parts.Add((t.Negated ? "NOT " : string.Empty) + part);
        }
        if (query.HasAttachment.HasValue) parts.Add("hasAttachments:" + (query.HasAttachment.Value ? "true" : "false"));
        if (query.After.HasValue)         parts.Add("received>=" + query.After.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (query.Before.HasValue)        parts.Add("received<" + query.Before.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        // A query of only NOTs has nothing to match against.
        if (parts.Count == 0 || parts.All(p => p.StartsWith("NOT ", StringComparison.Ordinal))) return null;
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// A word as-is, or in quotes when it is a phrase or would otherwise be misread (a space, a colon, a leading
    /// minus). Quotes inside are dropped. Graph's <c>$search</c> value is itself quoted, so its phrases take
    /// escaped quotes.
    /// </summary>
    private static string QuoteIfNeeded(string text, bool isPhrase, string quote = "\"")
    {
        // Backslashes and quotes are what a server would read as syntax of its own; KQL also has AND, OR,
        // NOT and brackets. Quoting the value makes all of it literal.
        var clean = text.Replace("\"", string.Empty, StringComparison.Ordinal)
                        .Replace("\\", string.Empty, StringComparison.Ordinal);
        var risky = clean.Any(c => char.IsWhiteSpace(c) || c is ':' or '(' or ')' or '"')
            || clean.StartsWith('-')
            || clean is "AND" or "OR" or "NOT";
        return isPhrase || risky ? quote + clean + quote : clean;
    }
}
