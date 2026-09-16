using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Decides whether a loaded message matches a <see cref="MessageSearchQuery"/> (#717), from two sources
/// of evidence:
/// <list type="bullet">
/// <item>the row itself — sender, recipients, subject and preview, matched as a case-insensitive
/// "contains", exactly as the search box always has, so nothing it used to find stops being found
/// (including the middle of a word, which a full-text index does not match); and</item>
/// <item>the full-text index, which has the whole message: body, Cc, attachment names.</item>
/// </list>
/// A message matches its words if either source finds all of them, and fails if either source finds a
/// word it must not contain. Conditions (<c>is:unread</c>, dates, folder, account) are always answered
/// from the row.
/// </summary>
public sealed class MessageSearchMatcher
{
    private readonly MessageSearchQuery _query;
    private readonly Func<MailMessageSummary, string> _folderName;
    private readonly Func<MailMessageSummary, string> _accountName;
    private readonly List<SearchTerm> _positive;
    private readonly List<SearchTerm> _negative;

    private HashSet<string>? _indexPositive;
    private HashSet<string>? _indexNegative;

    /// <param name="folderName">The name a <c>folder:</c> condition is matched against.</param>
    /// <param name="accountName">The names (label and address) an <c>account:</c> condition is matched against.</param>
    public MessageSearchMatcher(
        MessageSearchQuery query,
        Func<MailMessageSummary, string> folderName,
        Func<MailMessageSummary, string> accountName)
    {
        _query = query;
        _folderName = folderName;
        _accountName = accountName;
        _positive = [.. query.Terms.Where(t => !t.Negated)];
        _negative = [.. query.Terms.Where(t => t.Negated)];
    }

    public MessageSearchQuery Query => _query;

    /// <summary>
    /// Supplies what the full-text index found: the messages holding every wanted word, and the
    /// messages holding any unwanted one. Either may be null when the index could not answer.
    /// </summary>
    public void SetIndexHits(IEnumerable<SearchHit>? positive, IEnumerable<SearchHit>? negative)
    {
        _indexPositive = positive == null ? null : KeysOf(positive);
        _indexNegative = negative == null ? null : KeysOf(negative);
    }

    public bool Matches(MailMessageSummary msg)
    {
        if (!MatchesConditions(msg)) return false;
        if (!_query.HasText) return true;

        var wanted = _positive.Count == 0
            || _positive.All(t => RowContains(msg, t))
            || InIndex(_indexPositive, msg);
        if (!wanted) return false;

        if (_negative.Count == 0) return true;
        if (_negative.Any(t => RowContains(msg, t))) return false;
        return !InIndex(_indexNegative, msg);
    }

    private bool MatchesConditions(MailMessageSummary msg)
    {
        var q = _query;
        if (q.HasAttachment.HasValue && msg.HasAttachments != q.HasAttachment.Value) return false;
        if (q.IsRead.HasValue && msg.IsRead != q.IsRead.Value) return false;
        if (q.IsFlagged.HasValue && msg.IsFlagged != q.IsFlagged.Value) return false;
        if (q.After.HasValue && msg.Date < new DateTimeOffset(q.After.Value)) return false;
        if (q.Before.HasValue && msg.Date >= new DateTimeOffset(q.Before.Value)) return false;
        if (q.Folders.Count > 0)
        {
            var name = _folderName(msg);
            if (!q.Folders.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (q.Accounts.Count > 0)
        {
            var name = _accountName(msg);
            if (!q.Accounts.Any(a => name.Contains(a, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    /// <summary>The row's own text for a term — the four fields the search box has always read.</summary>
    private static bool RowContains(MailMessageSummary msg, SearchTerm t) => t.Field switch
    {
        SearchField.Any     => Has(msg.From, t) || Has(msg.To, t) || Has(msg.Subject, t) || Has(msg.Preview, t),
        SearchField.From    => Has(msg.From, t),
        SearchField.To      => Has(msg.To, t),
        SearchField.Subject => Has(msg.Subject, t),
        SearchField.Body    => Has(msg.Preview, t),
        // Cc and attachment names are not on the row; only the index can answer them.
        _                   => false,
    };

    private static bool Has(string? field, SearchTerm t)
        => !string.IsNullOrEmpty(field) && field.Contains(t.Text, StringComparison.OrdinalIgnoreCase);

    private static bool InIndex(HashSet<string>? keys, MailMessageSummary msg)
    {
        if (keys == null || keys.Count == 0) return false;
        if (keys.Contains(MessageDeduplicator.PerFolderKeyFor(msg))) return true;
        // An aggregate view shows one copy of a message filed in several folders (Gmail labels); the
        // copy whose body was cached may not be the one on screen.
        return !string.IsNullOrWhiteSpace(msg.InternetMessageId)
            && keys.Contains(MessageDeduplicator.CollapseKeyFor(msg));
    }

    private static HashSet<string> KeysOf(IEnumerable<SearchHit> hits)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in hits)
        {
            var row = new MailMessageSummary
            {
                AccountId = h.AccountId, FolderName = h.FolderName, MessageId = h.MessageId,
                InternetMessageId = h.InternetMessageId,
            };
            keys.Add(MessageDeduplicator.PerFolderKeyFor(row));
            if (!string.IsNullOrWhiteSpace(h.InternetMessageId))
                keys.Add(MessageDeduplicator.CollapseKeyFor(row));
        }
        return keys;
    }
}

/// <summary>
/// Turns search terms into an SQLite FTS5 <c>MATCH</c> expression (#717). Every word is a prefix match
/// (<c>budg</c> finds "budget", which is what typing a word means), except a single character, which
/// would match nearly everything and must be the whole word; a quoted phrase is matched as written.
/// </summary>
public static class SearchMatchExpression
{
    /// <summary>The index column each field is looked for in. Null: every column.</summary>
    public static string? ColumnFor(SearchField field) => field switch
    {
        SearchField.From       => "sender",
        SearchField.To         => "to_addr",
        SearchField.Cc         => "cc",
        SearchField.Subject    => "subject",
        SearchField.Body       => "body",
        SearchField.Attachment => "attachments",
        _                      => null,
    };

    /// <summary>
    /// All of <paramref name="terms"/>, ANDed. Null when there is nothing the index can express — a
    /// term with no letter or digit in it tokenizes to nothing, and an index answer that silently
    /// ignored it would claim matches the term rules out.
    /// </summary>
    public static string? AllOf(IEnumerable<SearchTerm> terms) => Join(terms, " AND ", requireAll: true);

    /// <summary>Any of <paramref name="terms"/>, ORed; terms the index cannot express are skipped.</summary>
    public static string? AnyOf(IEnumerable<SearchTerm> terms) => Join(terms, " OR ", requireAll: false);

    private static string? Join(IEnumerable<SearchTerm> terms, string op, bool requireAll)
    {
        var parts = new List<string>();
        foreach (var t in terms)
        {
            var part = Expression(t);
            if (part == null)
            {
                if (requireAll) return null;
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join(op, parts);
    }

    private static string? Expression(SearchTerm t)
    {
        if (!t.Text.Any(char.IsLetterOrDigit)) return null;
        // Double quotes are the only character with meaning inside an FTS5 string; doubled, they are
        // literal. Everything else — @, ., :, - — is left to the tokenizer, which splits on it the
        // same way it split the indexed text, so "sam@example.com" becomes the phrase "sam example com".
        var escaped = t.Text.Replace("\"", "\"\"", StringComparison.Ordinal);
        var tokens = Tokens(t.Text);
        var prefix = !t.IsPhrase && (tokens.Count > 1 || tokens[0].Length > 1);
        var phrase = "\"" + escaped + "\"" + (prefix ? "*" : string.Empty);
        var column = ColumnFor(t.Field);
        return column == null ? phrase : column + " : " + phrase;
    }

    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
            if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
