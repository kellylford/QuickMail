using System;

namespace QuickMail.Models;

/// <summary>
/// What goes between the fields of a list row's spoken name (#802). A user who listens at a slow
/// rate reported that the pause after each period adds up across every row, so a comma or a plain
/// space can be chosen instead. How long each one pauses is the user's screen reader's business,
/// not ours. Punctuation inside a field is never touched.
/// </summary>
public enum RowFieldSeparator
{
    /// <summary>". " between fields and a period at the end. What rows have always said.</summary>
    Period,

    /// <summary>", " between fields, no period at the end.</summary>
    Comma,

    /// <summary>" " between fields, no period at the end.</summary>
    Space,
}

/// <summary>The config.ini spelling of <see cref="RowFieldSeparator"/>, and the text it joins with.</summary>
public static class RowFieldSeparators
{
    /// <summary>The text placed between two fields.</summary>
    public static string Between(RowFieldSeparator separator) => separator switch
    {
        RowFieldSeparator.Comma => ", ",
        RowFieldSeparator.Space => " ",
        _                       => ". ",
    };

    /// <summary>
    /// Only the period choice ends the row with a period. Someone who picks a comma or a space has
    /// asked for fewer periods, and the #802 report's own example ends without one.
    /// </summary>
    public static bool EndsWithPeriod(RowFieldSeparator separator) => separator == RowFieldSeparator.Period;

    public static string ToConfigValue(RowFieldSeparator separator) => separator switch
    {
        RowFieldSeparator.Comma => "comma",
        RowFieldSeparator.Space => "space",
        _                       => "period",
    };

    /// <summary>Unknown or missing values read as the period, so a typo never changes speech.</summary>
    public static RowFieldSeparator FromConfigValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "comma" => RowFieldSeparator.Comma,
        "space" => RowFieldSeparator.Space,
        _       => RowFieldSeparator.Period,
    };
}
