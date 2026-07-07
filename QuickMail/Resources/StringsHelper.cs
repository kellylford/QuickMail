using System.Globalization;
using System.Resources;

namespace QuickMail.Resources;

/// <summary>
/// Pluralization and composition helper over <see cref="Strings"/>. This is the only place
/// plural resolution happens — no inline English "(n == 1 ? "" : "s")" grammar anywhere else.
/// Keys follow the convention "{baseKey}_One" / "{baseKey}_Other"; the culture's own plural
/// rule picks between them (English has two forms; a future language needing more forms adds
/// more suffixes and <see cref="ResolvePluralSuffix"/> grows a case for it).
/// </summary>
public static class StringsHelper
{
    /// <summary>
    /// Resolves "{baseKey}_One" or "{baseKey}_Other" for <paramref name="count"/> under the
    /// current UI culture, formats it with <paramref name="count"/> as {0}, and returns the result.
    /// Falls back to the English suffix if the resolved key is missing for the active culture.
    /// </summary>
    public static string Count(string baseKey, int count)
    {
        var suffix = ResolvePluralSuffix(count, CultureInfo.CurrentUICulture);
        var key = baseKey + suffix;
        var format = Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture)
            ?? Strings.ResourceManager.GetString(baseKey + "_Other", CultureInfo.CurrentUICulture)
            ?? key;
        return string.Format(CultureInfo.CurrentUICulture, format, count);
    }

    /// <summary>
    /// English/Spanish/German/French plural rule: 1 is singular, everything else (including 0) is plural.
    /// Extend with culture-specific branches if a shipped language needs more than two forms
    /// (e.g. Slavic one/few/many, Arabic six-way).
    /// </summary>
    private static string ResolvePluralSuffix(int count, CultureInfo culture) =>
        count == 1 ? "_One" : "_Other";
}
