using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #1077: paragraphs in the plain-text compose editor are the runs between hard line
/// breaks, so Ctrl+Up / Ctrl+Down and a screen reader's paragraph reading have something to
/// work with. A WPF TextBox on its own treats the whole text as one paragraph.
/// </summary>
public class PlainTextParagraphsTests
{
    //                       0         1         2         3
    //                       0123456789012345678901234567890123456
    private const string Text = "One one.\r\n\r\nTwo two.\r\nThree.";

    [Theory]
    [InlineData(0, 0)]   // start of the first
    [InlineData(5, 0)]   // middle of the first
    [InlineData(10, 10)] // the empty line
    [InlineData(12, 12)] // start of "Two two."
    [InlineData(17, 12)]
    [InlineData(22, 22)] // "Three."
    [InlineData(28, 22)] // the end of the text
    public void Start_IsAfterTheLineBreakBefore(int index, int expected) =>
        Assert.Equal(expected, PlainTextParagraphs.Start(Text, index));

    [Theory]
    [InlineData(3, 0, 10)]   // a paragraph and the line break that ends it
    [InlineData(10, 10, 12)] // an empty line: just its line break, read as "blank"
    [InlineData(14, 12, 22)]
    [InlineData(25, 22, 28)] // the last paragraph runs to the end
    public void Enclosing_GivesTheParagraphOrAnEmptyLinesBreak(int index, int start, int end) =>
        Assert.Equal((start, end), PlainTextParagraphs.Enclosing(Text, index));

    [Fact]
    public void CtrlDown_StopsAtEveryParagraphIncludingEmptyLines()
    {
        var stops = new List<int>();
        int caret = 0;
        for (int i = 0; i < 5; i++) stops.Add(caret = PlainTextParagraphs.CaretDown(Text, caret));
        // Empty line, "Two two.", "Three.", then the end of the text, where it stays.
        Assert.Equal(new[] { 10, 12, 22, 28, 28 }, stops);
    }

    [Fact]
    public void CtrlUp_GoesToTheParagraphStartFirst_ThenToThePreviousParagraph()
    {
        Assert.Equal(22, PlainTextParagraphs.CaretUp(Text, 25)); // mid "Three." → its start
        Assert.Equal(12, PlainTextParagraphs.CaretUp(Text, 22)); // at its start → "Two two."
        Assert.Equal(10, PlainTextParagraphs.CaretUp(Text, 12)); // → the empty line
        Assert.Equal(0, PlainTextParagraphs.CaretUp(Text, 10));
        Assert.Equal(0, PlainTextParagraphs.CaretUp(Text, 0));   // stays at the top
    }

    [Fact]
    public void TextEndingInALineBreak_HasAnEmptyLastParagraph()
    {
        const string text = "Hello\r\n";
        Assert.Equal(7, PlainTextParagraphs.NextStart(text, 0));
        Assert.Null(PlainTextParagraphs.NextStart(text, 7));
        Assert.Equal((7, 7), PlainTextParagraphs.Enclosing(text, 7));
        Assert.Equal(0, PlainTextParagraphs.PreviousStart(text, 7));
    }

    [Fact]
    public void BareLineFeeds_AreLineBreaksToo()
    {
        const string text = "a\nb\n\nc";
        Assert.Equal(new[] { 2, 4, 5, 6 }, new[]
        {
            PlainTextParagraphs.CaretDown(text, 0),
            PlainTextParagraphs.CaretDown(text, 2),
            PlainTextParagraphs.CaretDown(text, 4),
            PlainTextParagraphs.CaretDown(text, 5),
        });
        Assert.Equal((4, 5), PlainTextParagraphs.Enclosing(text, 4));
    }

    [Fact]
    public void EmptyText_HasOneEmptyParagraph()
    {
        Assert.Equal((0, 0), PlainTextParagraphs.Enclosing("", 0));
        Assert.Equal(0, PlainTextParagraphs.CaretDown("", 0));
        Assert.Equal(0, PlainTextParagraphs.CaretUp("", 0));
    }
}
