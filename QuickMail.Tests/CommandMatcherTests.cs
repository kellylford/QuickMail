using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The command palette's ranking. Plain <c>[Fact]</c>s — no WPF, no window, no timing — because
/// the ranking is the part of the feature that has to be predictable: the palette reports only
/// the TOP result as the user types, and Enter runs it, so a result nobody can explain is a
/// command nobody meant to run.
/// </summary>
public class CommandMatcherTests
{
    private static CommandDefinition Cmd(string title, string category = "View", string? id = null,
                                         string? description = null)
        => new(id ?? $"test.{title.Replace(" ", "").ToLowerInvariant()}",
               category, title, execute: () => { }, description: description);

    private static List<string> Titles(IEnumerable<CommandDefinition> commands)
        => commands.Select(c => c.Title).ToList();

    // ── The acronym case, which is what "VS Code style" mostly means in practice ──

    [Fact]
    public void Initials_RankTheCommandTheySpell_First()
    {
        var all = new List<CommandDefinition>
        {
            Cmd("Get The File"),        // also contains g, t, f — but not as initials of these words
            Cmd("Go to Folder"),
            Cmd("Toggle Grid Focus"),
        };

        var ranked = CommandMatcher.Rank(all, "gtf");

        Assert.Equal("Go to Folder", ranked[0].Title);
    }

    [Fact]
    public void Initials_MatchAcrossCamelCase()
    {
        Assert.True(CommandMatcher.TryScore("MoveToArchive", "mta", out var camel));
        Assert.True(CommandMatcher.TryScore("Move to Archive", "mta", out var spaced));

        // Both are the same three word-initials; neither should be wildly favoured.
        Assert.True(camel > 0 && spaced > 0);
    }

    // ── The predictable tiers have to outrank a scattered hit ──

    [Fact]
    public void AnExactTitle_OutranksEverythingElse()
    {
        var all = new List<CommandDefinition>
        {
            Cmd("Reply All"),
            Cmd("Reply"),
        };

        Assert.Equal("Reply", CommandMatcher.Rank(all, "Reply")[0].Title);
    }

    [Fact]
    public void APrefix_OutranksAMidWordMatch()
    {
        var all = new List<CommandDefinition>
        {
            Cmd("New Folder"),
            Cmd("Folder Properties"),
        };

        Assert.Equal("Folder Properties", CommandMatcher.Rank(all, "fol")[0].Title);
    }

    [Fact]
    public void AContiguousRun_OutranksTheSameLettersScattered()
    {
        // "Manager Backup Check" spells a-r-c-h mid-word, and its own initials are "mbc", so
        // it is a genuinely scattered hit rather than an acronym one.
        Assert.True(CommandMatcher.TryScore("Move to Archive", "arch", out var contiguous));
        Assert.True(CommandMatcher.TryScore("Manager Backup Check", "arch", out var scattered));

        Assert.True(contiguous > scattered,
            $"a contiguous 'arch' ({contiguous}) must beat a scattered one ({scattered})");
    }

    [Fact]
    public void AWordSpelledByTheInitials_IsTreatedAsAnAcronymHit()
    {
        // Pinning a real consequence of the acronym tier rather than leaving it a surprise:
        // "arch" is the initials of "A Rather Cheerful Heading", so that outranks the
        // contiguous "Move to Archive". Rare in practice with real command titles, and it is
        // the same rule that makes "gtf" reach "Go to Folder" — but it is deliberate.
        var all = new List<CommandDefinition>
        {
            Cmd("Move to Archive"),
            Cmd("A Rather Cheerful Heading"),
        };

        Assert.Equal("A Rather Cheerful Heading", CommandMatcher.Rank(all, "arch")[0].Title);
    }

    [Fact]
    public void ASubstringAnywhere_StillMatches()
    {
        var all = new List<CommandDefinition> { Cmd("Move to Archive") };

        Assert.Single(CommandMatcher.Rank(all, "arch"));
    }

    // ── What must not match ──

    [Fact]
    public void LettersOutOfOrder_DoNotMatch()
    {
        Assert.False(CommandMatcher.TryScore("Go to Folder", "redlof", out _));
    }

    [Fact]
    public void ALetterThatIsNotThere_DoesNotMatch()
    {
        var all = new List<CommandDefinition> { Cmd("Go to Folder"), Cmd("Reply All") };

        Assert.Empty(CommandMatcher.Rank(all, "zzz"));
    }

    // ── Ordering guarantees ──

    [Fact]
    public void AnEmptyQuery_ReturnsTheListUntouched()
    {
        var all = new List<CommandDefinition> { Cmd("Zebra"), Cmd("Apple"), Cmd("Mango") };

        // Not re-sorted: an unfiltered palette shows the registry's own category-then-title order.
        Assert.Equal(new[] { "Zebra", "Apple", "Mango" }, Titles(CommandMatcher.Rank(all, "")));
        Assert.Equal(new[] { "Zebra", "Apple", "Mango" }, Titles(CommandMatcher.Rank(all, "   ")));
        Assert.Equal(new[] { "Zebra", "Apple", "Mango" }, Titles(CommandMatcher.Rank(all, null)));
    }

    [Fact]
    public void EquallyGoodMatches_KeepTheirIncomingOrder()
    {
        // Same title length, same shape, same score — so the registry order decides, every time.
        var all = new List<CommandDefinition> { Cmd("Reply Two"), Cmd("Reply One") };

        Assert.Equal(new[] { "Reply Two", "Reply One" }, Titles(CommandMatcher.Rank(all, "reply")));
    }

    [Fact]
    public void ATie_GoesToTheShorterTitle()
    {
        var all = new List<CommandDefinition>
        {
            Cmd("Reply All and Copy Everyone"),
            Cmd("Reply All"),
        };

        Assert.Equal("Reply All", CommandMatcher.Rank(all, "reply all")[0].Title);
    }

    // ── Fields other than the title ──

    [Fact]
    public void ACategoryWord_FindsTheCommandsInThatCategory()
    {
        var all = new List<CommandDefinition>
        {
            Cmd("Next Theme", category: "View"),
            Cmd("Reply", category: "Mail"),
        };

        var ranked = CommandMatcher.Rank(all, "mail");

        Assert.Equal("Reply", ranked[0].Title);
    }

    [Fact]
    public void ATitleMatch_OutranksACategoryMatch()
    {
        var all = new List<CommandDefinition>
        {
            Cmd("Reply", category: "Mail"),         // matches on category
            Cmd("Mail Merge", category: "Tools"),   // matches on title
        };

        Assert.Equal("Mail Merge", CommandMatcher.Rank(all, "mail")[0].Title);
    }

    [Fact]
    public void TheDescriptionAndId_AreSearchedToo()
    {
        var byDescription = new List<CommandDefinition>
        {
            Cmd("Obscure Name", description: "Marks the conversation as watched"),
        };
        Assert.Single(CommandMatcher.Rank(byDescription, "watched"));

        var byId = new List<CommandDefinition> { Cmd("Obscure Name", id: "mail.markUnread") };
        Assert.Single(CommandMatcher.Rank(byId, "markunread"));
    }

    // ── Multi-word queries ──

    [Fact]
    public void WordsInOrder_MatchAcrossTheTitle()
    {
        var all = new List<CommandDefinition> { Cmd("Go to Folder") };

        Assert.Single(CommandMatcher.Rank(all, "go fold"));
    }

    [Fact]
    public void WordsOutOfOrder_StillMatch()
    {
        // "arch mail" is the category and a title word, the wrong way round. Requiring the
        // user to guess the order would make the category search useless in practice.
        var all = new List<CommandDefinition> { Cmd("Move to Archive", category: "Mail") };

        Assert.Single(CommandMatcher.Rank(all, "arch mail"));
    }

    [Fact]
    public void EveryTypedWord_HasToMatchSomething()
    {
        var all = new List<CommandDefinition> { Cmd("Move to Archive", category: "Mail") };

        Assert.Empty(CommandMatcher.Rank(all, "arch zzz"));
    }

    // ── Case ──

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var all = new List<CommandDefinition> { Cmd("Go to Folder") };

        Assert.Single(CommandMatcher.Rank(all, "GO TO FOLDER"));
        Assert.Single(CommandMatcher.Rank(all, "go to folder"));
    }

    [Fact]
    public void SurroundingWhitespace_IsIgnored()
    {
        var all = new List<CommandDefinition> { Cmd("Go to Folder") };

        Assert.Single(CommandMatcher.Rank(all, "  folder  "));
    }
}
