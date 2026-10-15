using System;
using System.Linq;

namespace QuickMail.Models;

/// <summary>
/// The word each field says, and the user's replacement for it (#807). Someone listening at a slow
/// rate can make "3 messages" say "3", or "2 unread" say "2 of", and so hear the fewest words that
/// still tell them what they need.
/// </summary>
public static class RowFieldWords
{
    /// <summary>Longest word kept. Long enough for a short phrase, short enough to stay a word.</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// The shipped word a field says, shown in its Spoken as box: a state's true word, a count's
    /// unit, a text field's label. Message count shows its plural; until the user changes it,
    /// "1 message" keeps its singular.
    /// </summary>
    public static string DefaultWord(RowFieldDef def) => def.Format switch
    {
        RowFieldFormat.State         => def.TrueWord ?? string.Empty,
        RowFieldFormat.Count         => "messages",
        RowFieldFormat.UnreadCount   => "unread",
        RowFieldFormat.UnreadOfTotal => "unread",
        _                            => def.SpokenLabel,
    };

    /// <summary>
    /// What may be stored: line breaks and other control or format characters removed (a row name
    /// is one line, and a right-to-left override would make it read backwards), then capped. Not
    /// trimmed: the box is bound as you type, and trimming would eat the space before a second word.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var kept = new string(text.Where(c =>
            !char.IsControl(c)
            && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
               != System.Globalization.UnicodeCategory.Format).ToArray());
        return kept.Length > MaxLength ? kept[..MaxLength] : kept;
    }

    /// <summary>
    /// The override to store for what the user typed: null when it is exactly the shipped word, so
    /// a later change to the shipped wording still reaches them; otherwise the cleaned text.
    /// Exactly, not after trimming: the box is bound as you type, and "unread " on the way to
    /// "unread mail" must not snap back to "unread" and lose its space. <see cref="Normalize"/>
    /// tidies a stored word on load.
    /// </summary>
    public static string? Override(string? typed, string shipped)
    {
        var clean = Clean(typed);
        return string.Equals(clean, shipped, StringComparison.Ordinal) ? null : clean;
    }

    /// <summary>
    /// A stored word as it should be kept: cleaned, trimmed, and null when that leaves the shipped
    /// word. Run where words enter the app (loading rowlayout.json, which a hand edit or a settings
    /// import can fill with anything), not in the box, where it would fight the typing.
    /// </summary>
    public static string? Normalize(string? stored, string shipped)
    {
        if (stored is null) return null;
        var clean = Clean(stored).Trim();
        return string.Equals(clean, shipped, StringComparison.Ordinal) ? null : clean;
    }
}
