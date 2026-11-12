using System.Text.RegularExpressions;

namespace QuickMail.Helpers;

/// <summary>
/// Spelling errors the compose spelling features must never report: the pieces of a web or email
/// address (#902).
///
/// <para>
/// WPF's speller checks the pieces of an address as ordinary words, so arrowing over
/// <c>https://contoso.example/plan</c> was announced as "Misspelling: …" with suggestions for
/// "contoso". The caller passes the whitespace-delimited token around a spelling error and where
/// the error starts in it. Only an error at or after the point where the address begins is
/// skipped, so the text of a Markdown link, <c>[recieve](https://x.example)</c>, or a word run up
/// against an address, <c>teh(https://x.example)</c>, is still checked. A skipped error is passed
/// over by the navigation announcement, by Next and Previous Misspelling, by the Alt+1/2/3
/// suggestions that ride on them, and by Check Spelling.
/// </para>
///
/// <para>
/// Kept a pure function of the token so it can be tested without a spelling engine, which not
/// every test machine has.
/// </para>
/// </summary>
public static class SpellingTokens
{
    // A scheme, "mailto:", or "www." that starts a word ("awww.nice" is not an address).
    private static readonly Regex WebAddress = new(
        @"\b(?:https?|ftp)://\S|\bmailto:\S|(?<![\p{L}\p{N}])www\.[\p{L}\p{N}]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A dotted host name followed by a path, without a scheme: github.com/kellylford/QuickMail.
    // The "/" is what tells it from a missing space after a full stop ("end.Start"); the last
    // label must be letters, two or more, so "e.g./i.e." and "1.5/2" are not addresses.
    private static readonly Regex HostWithPath = new(
        @"(?<![\p{L}\p{N}.@/-])[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)*\.\p{L}{2,}/",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // An email address: its match starts at the local part, which may not follow another
    // address character (so "a@b@c.example" is not one), and the domain needs a dot.
    private static readonly Regex EmailAddress = new(
        @"(?<![\p{L}\p{N}._%+'@-])[\p{L}\p{N}._%+'-]+@[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+(?![\p{L}\p{N}@-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex[] Patterns = [WebAddress, HostWithPath, EmailAddress];

    /// <summary>
    /// Where the first web or email address in <paramref name="token"/> — a whitespace-delimited
    /// piece of text — begins, or -1 when it has none. The address is taken to run to the end of
    /// the token.
    /// </summary>
    public static int AddressStart(string? token)
    {
        if (string.IsNullOrEmpty(token)) return -1;
        int start = -1;
        foreach (var regex in Patterns)
        {
            var match = regex.Match(token);
            if (match.Success && (start < 0 || match.Index < start)) start = match.Index;
        }
        return start;
    }

    /// <summary>
    /// True when a spelling error starting <paramref name="errorOffset"/> characters into
    /// <paramref name="token"/> lies inside a web or email address, and so is not a misspelling.
    /// </summary>
    public static bool IsInAddress(string? token, int errorOffset)
    {
        int start = AddressStart(token);
        return start >= 0 && errorOffset >= start;
    }
}
