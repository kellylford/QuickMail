using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// Ranks command-palette entries against what the user has typed, the way VS Code does: the
/// typed characters have to appear in order but not next to each other, so "gtf" reaches
/// "Go to Folder" and "arch" reaches "Move to Archive".
///
/// <para>Pure and WPF-free, so the ranking is covered by plain unit tests rather than by
/// driving a window.</para>
///
/// <para><b>Why the weights lean so hard on the obvious cases.</b> The palette reports only the
/// TOP result as it changes, and Enter runs it. A scattered subsequence hit that outranked a
/// title the user had typed the front of would be a result they cannot explain and did not
/// ask for, so the whole-string tiers (exact, prefix, substring) and the acronym pass are
/// each worth more than any combination of per-character bonuses can reach.</para>
/// </summary>
public static class CommandMatcher
{
    // Whole-string tiers. Ordered so the more literal match always wins.
    private const int ExactBonus     = 120;
    private const int PrefixBonus    = 50;
    private const int SubstringBonus = 25;
    private const int AcronymBonus   = 60;

    // Per-character bonuses.
    private const int FirstCharBonus   = 10;
    private const int WordStartBonus   = 6;
    private const int ConsecutiveBonus = 4;

    // Gaps between matched runs cost, but the total is capped: uncapped, a long title scores
    // worse than a short one purely for being long, whatever the user typed.
    private const int GapPenalty    = 1;
    private const int MaxGapPenalty = 12;

    // Which field matched. The title is what the user sees first, so it is what they take
    // themselves to be matching; everything else has to beat it by a margin to take the top.
    private const int TitlePenalty         = 0;
    private const int CategoryTitlePenalty = 12;
    private const int DescriptionPenalty   = 30;
    private const int IdPenalty            = 40;

    // Order-free multi-term matching ("arch mail") sits behind the whole-query reading
    // ("go fold"), never ahead of it.
    private const int MultiTermPenalty = 10;

    /// <summary>
    /// Ranks <paramref name="all"/> best-first. An empty query returns the input untouched, so
    /// an unfiltered palette still shows the registry's own category-then-title order.
    /// </summary>
    public static IReadOnlyList<CommandDefinition> Rank(
        IReadOnlyList<CommandDefinition> all, string? query)
    {
        if (all is null) return Array.Empty<CommandDefinition>();

        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0) return all;

        var scored = new List<(CommandDefinition Cmd, int Score, int Index)>();
        for (var i = 0; i < all.Count; i++)
            if (TryScoreCommand(all[i], trimmed, out var score))
                scored.Add((all[i], score, i));

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Cmd.Title.Length)   // a tie goes to the more direct command
            .ThenBy(x => x.Index)              // then to registry order, so ranking is stable
            .Select(x => x.Cmd)
            .ToList();
    }

    /// <summary>
    /// Scores one command across its title, category, description and id, keeping the best.
    /// <see langword="false"/> when the query is not a subsequence of any of them.
    /// </summary>
    public static bool TryScoreCommand(CommandDefinition cmd, string? query, out int score)
    {
        score = int.MinValue;

        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0) { score = 0; return true; }

        var matched = false;
        matched |= TryField(cmd.Title, trimmed, TitlePenalty, ref score);
        matched |= TryField($"{cmd.Category} {cmd.Title}", trimmed, CategoryTitlePenalty, ref score);
        if (!string.IsNullOrEmpty(cmd.Description))
            matched |= TryField(cmd.Description!, trimmed, DescriptionPenalty, ref score);
        matched |= TryField(cmd.Id, trimmed, IdPenalty, ref score);

        if (!matched) score = 0;
        return matched;
    }

    /// <summary>
    /// Scores <paramref name="query"/> against a single string. <see langword="false"/> when the
    /// query's characters do not all appear in it, in order.
    /// </summary>
    public static bool TryScore(string? candidate, string? query, out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrEmpty(candidate)) return false;
        return TryScoreText(candidate!, query!.Trim(), out score);
    }

    // ── Scoring ──────────────────────────────────────────────────────────────

    private static bool TryField(string candidate, string query, int penalty, ref int best)
    {
        if (!TryScoreText(candidate, query, out var score)) return false;

        var value = score - penalty;
        if (value > best) best = value;
        return true;
    }

    private static bool TryScoreText(string candidate, string query, out int score)
    {
        score = int.MinValue;
        var matched = false;

        // The whole query, spaces included — "go fold" over "Go to Folder".
        if (TryScoreCore(candidate, query.ToLowerInvariant(), out var whole))
        {
            score   = whole;
            matched = true;
        }

        // Each word on its own, in any order — "arch mail" over "Mail Move to Archive".
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 1)
        {
            var sum = 0;
            var all = true;
            foreach (var term in terms)
            {
                if (!TryScoreCore(candidate, term.ToLowerInvariant(), out var termScore))
                {
                    all = false;
                    break;
                }
                sum += termScore;
            }

            if (all)
            {
                var average = sum / terms.Length - MultiTermPenalty;
                if (!matched || average > score) score = average;
                matched = true;
            }
        }

        return matched;
    }

    /// <summary>
    /// Greedy leftmost subsequence scan. Taking each character as early as possible can score
    /// lower than the best available assignment, but it never misses a match that exists —
    /// which is the property that matters here: a command the user can see must never be
    /// unreachable by typing.
    /// </summary>
    private static bool TryScoreCore(string candidate, string needleLower, out int score)
    {
        score = 0;
        if (needleLower.Length == 0) return true;

        var hay = candidate.ToLowerInvariant();
        if (needleLower.Length > hay.Length) return false;

        // Word starts are read off the original casing, for camelCase boundaries. Invariant
        // lowering preserves length for everything we ship; fall back rather than mis-index.
        var cased = hay.Length == candidate.Length ? candidate : hay;

        var total    = 0;
        var gaps     = 0;
        var previous = -2;
        var from     = 0;

        foreach (var ch in needleLower)
        {
            var at = hay.IndexOf(ch, from);
            if (at < 0) return false;

            total += 1;
            if (at == 0) total += FirstCharBonus;
            else if (IsWordStart(cased, at)) total += WordStartBonus;
            if (at == previous + 1) total += ConsecutiveBonus;

            gaps += at - Math.Max(previous + 1, 0);
            previous = at;
            from     = at + 1;
        }

        total -= Math.Min(gaps * GapPenalty, MaxGapPenalty);

        if (string.Equals(hay, needleLower, StringComparison.Ordinal))
            total += ExactBonus;
        else if (hay.StartsWith(needleLower, StringComparison.Ordinal))
            total += PrefixBonus;
        else if (hay.Contains(needleLower, StringComparison.Ordinal))
            total += SubstringBonus;

        // "gtf" over the initials of "Go to Folder". Two characters minimum: one letter is
        // already served by the prefix tier, and would otherwise lift every command starting
        // with that letter for no reason.
        if (needleLower.Length >= 2 && !needleLower.Contains(' ')
            && Initials(cased).StartsWith(needleLower, StringComparison.Ordinal))
            total += AcronymBonus;

        score = total;
        return true;
    }

    private static string Initials(string text)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
            if (IsWordStart(text, i))
                builder.Append(char.ToLowerInvariant(text[i]));
        return builder.ToString();
    }

    private static bool IsWordStart(string text, int index)
    {
        if (index <= 0) return true;

        var previous = text[index - 1];
        if (!char.IsLetterOrDigit(previous)) return true;

        // camelCase / PascalCase boundary.
        return char.IsUpper(text[index]) && !char.IsUpper(previous);
    }
}
