using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for MarkdownEditing — the pure text operations behind the Format
/// menu/toolbar/shortcuts in Markdown compose mode.
/// </summary>
public class MarkdownEditingTests
{
    private static string Apply(string text, MarkdownEdit edit) =>
        text[..edit.ReplaceStart] + edit.Replacement + text[(edit.ReplaceStart + edit.ReplaceLength)..];

    // ── ToggleInline ──────────────────────────────────────────────────────────

    [Fact]
    public void ToggleInline_WrapsSelection()
    {
        var text = "make this bold please";
        var edit = MarkdownEditing.ToggleInline(text, 5, 9, "**");   // "this bold"

        Assert.True(edit.TurnedOn);
        Assert.Equal("make **this bold** please", Apply(text, edit));
        Assert.Equal(7, edit.SelectionStart);   // selection still covers the words
        Assert.Equal(9, edit.SelectionLength);
    }

    [Fact]
    public void ToggleInline_UnwrapsWhenMarkersInsideSelection()
    {
        var text = "make **this bold** please";
        var edit = MarkdownEditing.ToggleInline(text, 5, 13, "**");  // "**this bold**"

        Assert.False(edit.TurnedOn);
        Assert.Equal("make this bold please", Apply(text, edit));
    }

    [Fact]
    public void ToggleInline_UnwrapsWhenMarkersSurroundSelection()
    {
        var text = "make **this bold** please";
        var edit = MarkdownEditing.ToggleInline(text, 7, 9, "**");   // "this bold" inside markers

        Assert.False(edit.TurnedOn);
        Assert.Equal("make this bold please", Apply(text, edit));
        Assert.Equal(5, edit.SelectionStart);
        Assert.Equal(9, edit.SelectionLength);
    }

    [Fact]
    public void ToggleInline_AtCaret_InsertsPairWithCaretInside()
    {
        var edit = MarkdownEditing.ToggleInline("hello ", 6, 0, "**");

        Assert.True(edit.TurnedOn);
        Assert.Equal("hello ****", Apply("hello ", edit));
        Assert.Equal(8, edit.SelectionStart);   // between the markers
        Assert.Equal(0, edit.SelectionLength);
    }

    [Fact]
    public void ToggleInline_AtCaretInsideEmptyPair_RemovesPair()
    {
        var edit = MarkdownEditing.ToggleInline("hello ****", 8, 0, "**");

        Assert.False(edit.TurnedOn);
        Assert.Equal("hello ", Apply("hello ****", edit));
        Assert.Equal(6, edit.SelectionStart);
    }

    [Fact]
    public void ToggleInline_Strikethrough_UsesTildeMarkers()
    {
        var edit = MarkdownEditing.ToggleInline("gone", 0, 4, "~~");
        Assert.Equal("~~gone~~", Apply("gone", edit));
    }

    // ── ToggleHeading ─────────────────────────────────────────────────────────

    [Fact]
    public void ToggleHeading_AddsPrefixToCaretLine()
    {
        var text = "first line\nsecond line";
        var edit = MarkdownEditing.ToggleHeading(text, 15, 2);   // caret in "second line"

        Assert.True(edit.TurnedOn);
        Assert.Equal("first line\n## second line", Apply(text, edit));
    }

    [Fact]
    public void ToggleHeading_SameLevelRemovesPrefix()
    {
        var text = "## a heading";
        var edit = MarkdownEditing.ToggleHeading(text, 5, 2);

        Assert.False(edit.TurnedOn);
        Assert.Equal("a heading", Apply(text, edit));
    }

    [Fact]
    public void ToggleHeading_DifferentLevelReplacesPrefix()
    {
        var text = "## a heading";
        var edit = MarkdownEditing.ToggleHeading(text, 5, 3);

        Assert.True(edit.TurnedOn);
        Assert.Equal("### a heading", Apply(text, edit));
    }

    // ── ToggleListPrefix ──────────────────────────────────────────────────────

    [Fact]
    public void ToggleListPrefix_BulletsSelectedLines()
    {
        var text = "alpha\nbeta\ngamma";
        var edit = MarkdownEditing.ToggleListPrefix(text, 0, text.Length, ordered: false);

        Assert.True(edit.TurnedOn);
        Assert.Equal("- alpha\n- beta\n- gamma", Apply(text, edit));
    }

    [Fact]
    public void ToggleListPrefix_RemovesBulletsWhenAllBulleted()
    {
        var text = "- alpha\n- beta";
        var edit = MarkdownEditing.ToggleListPrefix(text, 0, text.Length, ordered: false);

        Assert.False(edit.TurnedOn);
        Assert.Equal("alpha\nbeta", Apply(text, edit));
    }

    [Fact]
    public void ToggleListPrefix_NumbersLinesSequentially()
    {
        var text = "alpha\nbeta\ngamma";
        var edit = MarkdownEditing.ToggleListPrefix(text, 0, text.Length, ordered: true);

        Assert.Equal("1. alpha\n2. beta\n3. gamma", Apply(text, edit));
    }

    [Fact]
    public void ToggleListPrefix_ConvertsBulletsToNumbers()
    {
        var text = "- alpha\n- beta";
        var edit = MarkdownEditing.ToggleListPrefix(text, 0, text.Length, ordered: true);

        Assert.True(edit.TurnedOn);
        Assert.Equal("1. alpha\n2. beta", Apply(text, edit));
    }

    [Fact]
    public void ToggleListPrefix_CaretOnly_AffectsCurrentLine()
    {
        var text = "alpha\nbeta\ngamma";
        var edit = MarkdownEditing.ToggleListPrefix(text, 8, 0, ordered: false); // caret in "beta"

        Assert.Equal("alpha\n- beta\ngamma", Apply(text, edit));
    }

    // ── InsertLink / ClearFormatting ──────────────────────────────────────────

    [Fact]
    public void InsertLink_ReplacesSelectionWithMarkdownLink()
    {
        var text = "see docs here";
        var edit = MarkdownEditing.InsertLink(text, 4, 4, "docs", "https://example.com");

        Assert.Equal("see [docs](https://example.com) here", Apply(text, edit));
    }

    [Fact]
    public void ClearFormatting_StripsMarkersAndPrefixes()
    {
        var text = "## A **bold** and ~~struck~~ `code` title";
        var edit = MarkdownEditing.ClearFormatting(text, 0, text.Length);

        Assert.Equal("A bold and struck code title", Apply(text, edit));
    }

    [Fact]
    public void ClearFormatting_NoSelection_ClearsCurrentLine()
    {
        var text = "plain\n- **listed** item\nplain";
        var edit = MarkdownEditing.ClearFormatting(text, 10, 0);   // caret in middle line

        Assert.Equal("plain\nlisted item\nplain", Apply(text, edit));
    }

    // ── DescribeFormatting ────────────────────────────────────────────────────

    [Fact]
    public void DescribeFormatting_Heading()
    {
        var text = "## section title";
        Assert.StartsWith("Heading 2.", MarkdownEditing.DescribeFormatting(text, 6));
    }

    [Fact]
    public void DescribeFormatting_BoldOnInsideMarkers()
    {
        var text = "some **bold** text";
        var description = MarkdownEditing.DescribeFormatting(text, 9);   // inside "bold"
        Assert.Contains("Bold on", description);
        Assert.Contains("Italic off", description);
    }

    [Fact]
    public void DescribeFormatting_BoldOffOutsideMarkers()
    {
        var text = "some **bold** text";
        Assert.Contains("Bold off", MarkdownEditing.DescribeFormatting(text, 2));
    }

    [Fact]
    public void DescribeFormatting_BulletListItem()
    {
        Assert.StartsWith("Bullet list item.", MarkdownEditing.DescribeFormatting("- item one", 4));
    }

    [Fact]
    public void DescribeFormatting_InsideCodeFence()
    {
        var text = "```\nvar x = 1;\n```";
        Assert.StartsWith("Code block.", MarkdownEditing.DescribeFormatting(text, 8));
    }

    [Fact]
    public void DescribeFormattingParts_OneFactPerEntry()
    {
        var text = "## some **bold** title";
        var parts = MarkdownEditing.DescribeFormattingParts(text, 12);   // inside "bold"

        Assert.Equal(5, parts.Count);
        Assert.Equal("Heading 2", parts[0]);
        Assert.Equal("Bold on", parts[1]);
        Assert.Equal("Italic off", parts[2]);
        Assert.Equal("Strikethrough off", parts[3]);
        Assert.Equal("Code off", parts[4]);
    }
}
