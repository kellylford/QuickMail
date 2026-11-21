using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Models;

/// <summary>The four ways an item in the folder tree can be moved among its siblings (#1058).</summary>
public enum TreeMove { Up, Down, ToStart, ToEnd }

/// <summary>
/// The order of the folder tree's top level (#1058): the sections (Calendar, Favorites, Views,
/// All Mail) and one <see cref="Account"/> placeholder per account, stored as config.ini's
/// <c>FolderTreeOrder</c>. The placeholders are filled with the accounts in accounts.json order,
/// so config.ini holds no account ids and accounts.json stays the one record of account order.
/// </summary>
public static class FolderTreeOrder
{
    public const string Calendar  = "calendar";
    public const string Favorites = "favorites";
    public const string Views     = "views";
    public const string AllMail   = "allmail";
    public const string Account   = "account";

    /// <summary>The sections in their default order.</summary>
    public static readonly IReadOnlyList<string> Sections = [Calendar, Favorites, Views, AllMail];

    /// <summary>Today's layout plus Favorites: the sections, then every account.</summary>
    public static List<string> Default(int accountCount) =>
        [.. Sections, .. Enumerable.Repeat(Account, Math.Max(0, accountCount))];

    /// <summary>What the retired <c>CalendarAtEndOfFolderList</c> setting meant, as a stored order.</summary>
    public static string FromCalendarAtEnd(bool atEnd) =>
        atEnd ? Format([Favorites, Views, AllMail, Account, Calendar])
              : Format([Calendar, Favorites, Views, AllMail]);

    /// <summary>Splits a stored value into tokens. Empty or missing gives an empty list.</summary>
    public static List<string> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public static string Format(IEnumerable<string> tokens) => string.Join(", ", tokens);

    /// <summary>
    /// Makes a stored order whole for <paramref name="accountCount"/> accounts: tokens are matched
    /// ignoring case; unknown tokens and repeated sections are dropped; a missing section goes back
    /// beside its default neighbour; missing account placeholders are added after the last one (or at
    /// the end when there is none), so a new account appears after the last account; and extra ones
    /// are removed from the end. Empty gives <see cref="Default"/>.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? tokens, int accountCount)
    {
        accountCount = Math.Max(0, accountCount);
        var result = new List<string>();
        foreach (var raw in tokens ?? [])
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token == Account) result.Add(Account);
            else if (Sections.Contains(token) && !result.Contains(token)) result.Add(token);
        }
        if (result.Count == 0) return Default(accountCount);

        for (var s = 0; s < Sections.Count; s++)
        {
            var section = Sections[s];
            if (result.Contains(section)) continue;
            result.Insert(DefaultPlaceFor(result, s), section);
        }

        var accounts = result.Count(t => t == Account);
        if (accounts < accountCount)
        {
            var at = result.LastIndexOf(Account);
            var insertAt = at < 0 ? result.Count : at + 1;
            result.InsertRange(insertAt, Enumerable.Repeat(Account, accountCount - accounts));
        }
        else
        {
            for (var i = result.Count - 1; i >= 0 && accounts > accountCount; i--)
                if (result[i] == Account)
                {
                    result.RemoveAt(i);
                    accounts--;
                }
        }
        return result;
    }

    // Just before the nearest section that follows it by default, else just after the nearest one
    // that precedes it, else at the start.
    private static int DefaultPlaceFor(List<string> result, int sectionIndex)
    {
        for (var n = sectionIndex + 1; n < Sections.Count; n++)
            if (result.IndexOf(Sections[n]) is var at and >= 0) return at;
        for (var p = sectionIndex - 1; p >= 0; p--)
            if (result.IndexOf(Sections[p]) is var at and >= 0) return at + 1;
        return 0;
    }
}
