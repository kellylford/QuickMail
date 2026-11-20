using System;

namespace QuickMail.Helpers;

/// <summary>
/// How well a person matches what has been typed in an address field (#1037). Suggestions are found
/// by "contains", but the one offered first, and the only kind Tab will enter, is one the user is
/// visibly in the middle of typing: the address itself, or a name or address that begins with it.
/// </summary>
internal static class SuggestionMatch
{
    /// <summary>Lower is better: 0 the address exactly, 1 begins with the text, 2 only contains it.</summary>
    internal static int Strength(string? name, string? email, string typed)
    {
        typed = typed.Trim();
        email ??= string.Empty;
        if (typed.Length > 0 && email.Trim().Equals(typed, StringComparison.OrdinalIgnoreCase)) return 0;
        return BeginsWith(name, email, typed) ? 1 : 2;
    }

    /// <summary>True when the name, any word of it, or the address begins with <paramref name="typed"/>.</summary>
    internal static bool BeginsWith(string? name, string? email, string typed)
    {
        typed = typed.Trim();
        if (typed.Length == 0) return true;
        name ??= string.Empty;
        if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var word in name.Split([' ', ',', '.', '-', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
            if (word.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        return (email ?? string.Empty).TrimStart().StartsWith(typed, StringComparison.OrdinalIgnoreCase);
    }
}
