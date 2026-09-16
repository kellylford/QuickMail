using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace QuickMail.Models;

/// <summary>Which part of a message a search term is looked for in (#717).</summary>
public enum SearchField
{
    /// <summary>Anywhere: sender, recipients, subject, body, attachment names.</summary>
    Any,
    From,
    To,
    Cc,
    Subject,
    Body,
    Attachment,
}

/// <summary>One word or quoted phrase of a search, optionally tied to a field and optionally excluded.</summary>
public sealed record SearchTerm(SearchField Field, string Text, bool IsPhrase = false, bool Negated = false);

/// <summary>
/// A parsed message search (#717): what the search box and Advanced Search both mean by a query.
/// Plain words must all appear; <c>-word</c> must not. Field prefixes narrow a word to one part of the
/// message (<c>from:</c> <c>to:</c> <c>cc:</c> <c>subject:</c> <c>body:</c> <c>attachment:</c>), and the
/// rest are conditions on the message rather than on its text (<c>has:attachment</c>,
/// <c>is:unread</c>, <c>is:read</c>, <c>is:flagged</c>, <c>is:unflagged</c>, <c>after:</c>,
/// <c>before:</c>, <c>folder:</c>, <c>account:</c>). A prefix QuickMail does not know — <c>Re:</c>, a
/// time like <c>10:30</c>, a link — is ordinary text, so pasting a subject line still finds it.
/// <para>A known condition whose value can't be read yet (<c>after:2026-0</c> while it is being typed, a
/// bare <c>from:</c>) is left out rather than turned into text, so the list does not empty and refill
/// on every keystroke of a half-typed date.</para>
/// </summary>
public sealed class MessageSearchQuery
{
    public List<SearchTerm> Terms { get; } = [];

    /// <summary>True: must have attachments; false: must have none; null: either.</summary>
    public bool? HasAttachment { get; set; }

    /// <summary>True: read only; false: unread only; null: either.</summary>
    public bool? IsRead { get; set; }

    /// <summary>True: flagged only; false: unflagged only; null: either.</summary>
    public bool? IsFlagged { get; set; }

    /// <summary>Received on or after this day (local midnight).</summary>
    public DateTime? After { get; set; }

    /// <summary>Received before this day (local midnight) — the day itself is not included.</summary>
    public DateTime? Before { get; set; }

    /// <summary>Folder names the message must be in one of (matched as part of the folder's name).</summary>
    public List<string> Folders { get; } = [];

    /// <summary>Account names or addresses the message must belong to one of (matched as part of the name).</summary>
    public List<string> Accounts { get; } = [];

    public bool HasText => Terms.Count > 0;

    public bool HasConditions =>
        HasAttachment.HasValue || IsRead.HasValue || IsFlagged.HasValue ||
        After.HasValue || Before.HasValue || Folders.Count > 0 || Accounts.Count > 0;

    public bool IsEmpty => !HasText && !HasConditions;

    private static readonly Dictionary<string, SearchField> FieldPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["from"]        = SearchField.From,
        ["to"]          = SearchField.To,
        ["cc"]          = SearchField.Cc,
        ["subject"]     = SearchField.Subject,
        ["body"]        = SearchField.Body,
        ["attachment"]  = SearchField.Attachment,
    };

    private static readonly HashSet<string> ConditionPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "has", "is", "after", "before", "folder", "account",
    };

    public static MessageSearchQuery Parse(string? text)
    {
        var query = new MessageSearchQuery();
        if (string.IsNullOrWhiteSpace(text)) return query;

        foreach (var token in Tokenize(text))
        {
            var negated = false;
            var body = token.Text;
            if (!token.Quoted && body.Length > 1 && body[0] == '-')
            {
                negated = true;
                body = body[1..];
            }

            // A whole-token quote ("exact phrase") has no prefix.
            if (token.Quoted)
            {
                AddTerm(query, SearchField.Any, token.Text, isPhrase: true, negated: token.Negated);
                continue;
            }

            var colon = body.IndexOf(':');
            if (colon > 0)
            {
                var prefix = body[..colon];
                var value = token.ValueQuoted ? token.Value : body[(colon + 1)..];
                if (FieldPrefixes.TryGetValue(prefix, out var field))
                {
                    AddTerm(query, field, value, isPhrase: token.ValueQuoted, negated);
                    continue;
                }
                if (ConditionPrefixes.Contains(prefix))
                {
                    ApplyCondition(query, prefix.ToLowerInvariant(), value.Trim(), negated);
                    continue;
                }
            }

            AddTerm(query, SearchField.Any, body, isPhrase: false, negated);
        }
        return query;
    }

    /// <summary>
    /// The words of one field's text, all tied to <paramref name="field"/> — for Advanced Search, whose From box
    /// means the sender whatever is typed in it. Quotes keep a phrase and a leading minus excludes, as in the
    /// search box, but nothing else is special: <c>is:unread</c> typed into From is a word to find in the
    /// sender, not a condition.
    /// </summary>
    public static List<SearchTerm> ParseFieldWords(string? text, SearchField field)
    {
        var terms = new List<SearchTerm>();
        if (string.IsNullOrWhiteSpace(text)) return terms;
        foreach (var token in Tokenize(text))
        {
            if (token.Quoted)
            {
                if (token.Text.Trim().Length > 0)
                    terms.Add(new SearchTerm(field, token.Text.Trim(), IsPhrase: true, Negated: token.Negated));
                continue;
            }
            var word = token.Text;
            var negated = word.Length > 1 && word[0] == '-';
            if (negated) word = word[1..];
            if (word.Length > 0) terms.Add(new SearchTerm(field, word, IsPhrase: false, Negated: negated));
        }
        return terms;
    }

    /// <summary>Writes field words back as <see cref="ParseFieldWords"/> reads them.</summary>
    public static string FieldWordsToText(IEnumerable<SearchTerm> terms)
        => string.Join(' ', terms.Select(t => (t.Negated ? "-" : string.Empty)
            + (t.IsPhrase || t.Text.Any(char.IsWhiteSpace) ? Quote(t.Text) : t.Text)));

    private static void AddTerm(MessageSearchQuery query, SearchField field, string text, bool isPhrase, bool negated)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        query.Terms.Add(new SearchTerm(field, text, isPhrase, negated));
    }

    private static void ApplyCondition(MessageSearchQuery query, string prefix, string value, bool negated)
    {
        if (value.Length == 0) return;
        var v = value.ToLowerInvariant();
        switch (prefix)
        {
            case "has":
                if (v is "attachment" or "attachments")
                    query.HasAttachment = !negated;
                break;
            case "is":
                switch (v)
                {
                    case "unread":    query.IsRead    = negated;  break;
                    case "read":      query.IsRead    = !negated; break;
                    case "flagged":   query.IsFlagged = !negated; break;
                    case "unflagged": query.IsFlagged = negated;  break;
                }
                break;
            case "after":
                if (!negated && TryParseDay(value, out var after)) query.After = after;
                break;
            case "before":
                if (!negated && TryParseDay(value, out var before)) query.Before = before;
                break;
            case "folder":
                if (!negated) query.Folders.Add(value);
                break;
            case "account":
                if (!negated) query.Accounts.Add(value);
                break;
        }
    }

    /// <summary>
    /// Reads a day: ISO <c>2026-01-15</c> always, and otherwise whatever the current culture writes as a
    /// short date. Anything with a time in it is refused — these conditions are whole days.
    /// </summary>
    public static bool TryParseDay(string value, out DateTime day)
    {
        day = default;
        if (DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            day = iso.Date;
            return true;
        }
        // A bare year or a single number would parse as something surprising in some cultures.
        if (value.Count(char.IsDigit) < 4 || !value.Any(c => c is '/' or '-' or '.')) return false;
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local)
            && local.TimeOfDay == TimeSpan.Zero)
        {
            day = local.Date;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Writes the query back as text the parser reads to the same query — what Advanced Search puts in
    /// front of the user so the syntax is learnable by seeing it.
    /// </summary>
    public string ToQueryString()
    {
        var parts = new List<string>();
        foreach (var t in Terms)
        {
            var prefix = t.Field switch
            {
                SearchField.From       => "from:",
                SearchField.To         => "to:",
                SearchField.Cc         => "cc:",
                SearchField.Subject    => "subject:",
                SearchField.Body       => "body:",
                SearchField.Attachment => "attachment:",
                _                      => string.Empty,
            };
            var needsQuotes = t.IsPhrase || t.Text.Any(char.IsWhiteSpace)
                || (prefix.Length == 0 && t.Text.Contains(':'))
                || (prefix.Length == 0 && t.Text.StartsWith('-'));
            var text = needsQuotes ? Quote(t.Text) : t.Text;
            parts.Add((t.Negated ? "-" : string.Empty) + prefix + text);
        }
        if (HasAttachment == true)  parts.Add("has:attachment");
        if (HasAttachment == false) parts.Add("-has:attachment");
        if (IsRead == false)        parts.Add("is:unread");
        if (IsRead == true)         parts.Add("is:read");
        if (IsFlagged == true)      parts.Add("is:flagged");
        if (IsFlagged == false)     parts.Add("is:unflagged");
        if (After.HasValue)         parts.Add("after:" + After.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (Before.HasValue)        parts.Add("before:" + Before.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (var f in Folders)  parts.Add("folder:" + (f.Any(char.IsWhiteSpace) ? Quote(f) : f));
        foreach (var a in Accounts) parts.Add("account:" + (a.Any(char.IsWhiteSpace) ? Quote(a) : a));
        return string.Join(' ', parts);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    private readonly record struct Token(string Text, bool Quoted, bool Negated, bool ValueQuoted, string Value);

    /// <summary>
    /// Splits on whitespace, keeping a quoted run together: <c>"exact phrase"</c>, <c>-"not this"</c>,
    /// and <c>from:"Sam Smith"</c>. An unclosed quote runs to the end, which is what someone still
    /// typing the phrase means.
    /// </summary>
    private static IEnumerable<Token> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) yield break;

            // -"phrase" or "phrase"
            var negated = false;
            var start = i;
            if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '"')
            {
                negated = true;
                i++;
            }
            if (text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                var end = close < 0 ? text.Length : close;
                var phrase = text[(i + 1)..end];
                i = close < 0 ? text.Length : close + 1;
                yield return new Token(phrase, Quoted: true, negated, ValueQuoted: false, Value: string.Empty);
                continue;
            }
            i = start;

            var sb = new StringBuilder();
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                // prefix:"quoted value"
                if (text[i] == '"' && sb.Length > 0 && sb[^1] == ':')
                {
                    var close = text.IndexOf('"', i + 1);
                    var end = close < 0 ? text.Length : close;
                    var value = text[(i + 1)..end];
                    i = close < 0 ? text.Length : close + 1;
                    var prefixed = sb.ToString();
                    yield return new Token(prefixed + value, Quoted: false, Negated: false, ValueQuoted: true, Value: value);
                    sb.Clear();
                    goto next;
                }
                sb.Append(text[i]);
                i++;
            }
            if (sb.Length > 0)
                yield return new Token(sb.ToString(), Quoted: false, Negated: false, ValueQuoted: false, Value: string.Empty);
            next:;
        }
    }
}
