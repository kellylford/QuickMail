using System.Text.RegularExpressions;

namespace QuickMail.Helpers;

/// <summary>
/// Words the compose spelling features must never report as misspellings (#902).
///
/// <para>
/// WPF's speller checks the pieces of a web address or an email address as ordinary words, so
/// arrowing over <c>https://contoso.example/plan</c> was announced as "Misspelling: …" with
/// suggestions for "contoso". The caller passes the whitespace-delimited token around a spelling
/// error; when it is an address, the error is skipped by the navigation announcement, by Next and
/// Previous Misspelling, by the Alt+1/2/3 suggestions that ride on them, and by Check Spelling.
/// </para>
///
/// <para>
/// Kept a pure function of the token so it can be tested without a spelling engine, which not
/// every test machine has.
/// </para>
/// </summary>
public static class SpellingTokens
{
    // Punctuation that commonly wraps an address in running text: "(see https://x.example).",
    // "<kelly@example.com>," or a quoted "www.example.com".
    private static readonly char[] LeadingWrap = ['(', '[', '{', '<', '"', '\'', '“', '‘'];
    private static readonly char[] TrailingWrap =
        ['.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '"', '\'', '”', '’'];

    // A scheme anywhere in the token, so a Markdown link "[plan](https://x.example/plan)" counts as
    // well as a bare address. "www." must start the token or follow punctuation, so a word that
    // merely contains "www." is not mistaken for one.
    private static readonly Regex WebAddress = new(
        @"(?:\b(?:https?|ftp)://\S|\bmailto:\S|(?<![\p{L}\p{N}])www\.[\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EmailAddress = new(
        @"^[\p{L}\p{N}._%+'-]+@[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="token"/> — a whitespace-delimited piece of text — is a web address
    /// (http, https, ftp, www., mailto:) or an email address, allowing for punctuation around it.
    /// </summary>
    public static bool IsWebOrEmailAddress(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var trimmed = token.Trim().TrimStart(LeadingWrap).TrimEnd(TrailingWrap);
        if (trimmed.Length == 0) return false;
        return WebAddress.IsMatch(trimmed) || EmailAddress.IsMatch(trimmed);
    }
}
