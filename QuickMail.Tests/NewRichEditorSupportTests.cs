using System.Linq;
using QuickMail.Controls;
using QuickMail.Helpers;
using QuickMail.Views;

namespace QuickMail.Tests;

/// <summary>
/// The parts of HTML mode on the new editor (#1099) that need no window: the clipboard's HTML
/// format, how a table lies in the editor's text, what a paragraph is called, and where the
/// quoted original starts for Check Spelling.
/// </summary>
public class NewRichEditorSupportTests
{
    // ── Clipboard HTML ───────────────────────────────────────────────────────

    [Fact]
    public void ClipboardHtml_Build_ThenExtract_GivesTheFragmentBack()
    {
        const string fragment = "<p>Café <strong>bold</strong> and a <a href=\"https://example.com/\">link</a></p>";
        Assert.Equal(fragment, ClipboardHtml.ExtractFragment(ClipboardHtml.Build(fragment)));
    }

    [Fact]
    public void ClipboardHtml_Build_WritesOffsetsInUtf8Bytes()
    {
        const string fragment = "<p>ééé</p>"; // each accented letter is two bytes
        var data = ClipboardHtml.Build(fragment);
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);

        int Offset(string name)
        {
            int at = data.IndexOf(name + ":", System.StringComparison.Ordinal) + name.Length + 1;
            return int.Parse(data.AsSpan(at, 10), System.Globalization.CultureInfo.InvariantCulture);
        }

        int start = Offset("StartFragment"), end = Offset("EndFragment");
        Assert.Equal(fragment, System.Text.Encoding.UTF8.GetString(bytes, start, end - start));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
        Assert.Equal('<', (char)bytes[Offset("StartHTML")]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Version:0.9\r\nStartHTML:1\r\n<html><body><!--StartFragment--><b>x</b><!--EndFragment--></body></html>", "<b>x</b>")]
    [InlineData("<!--StartFragment-->no end marker", "no end marker")]
    [InlineData("Version:0.9\r\n<html><body><p>no markers</p></body></html>", "<html><body><p>no markers</p></body></html>")]
    [InlineData("<!--StartFragment-->   <!--EndFragment-->", null)]
    [InlineData("Version:0.9 and nothing else", null)]
    public void ClipboardHtml_ExtractFragment(string? data, string? expected) =>
        Assert.Equal(expected, ClipboardHtml.ExtractFragment(data));

    // ── How paragraphs and table cells lie in the editor's text ──────────────

    private static string Raw(string pattern) => pattern
        .Replace('{', (char)0xFFF9)   // a row starts
        .Replace('}', (char)0xFFFB)   // a row ends
        .Replace('|', (char)0x0007)   // a cell ends
        .Replace('/', '\r');          // a paragraph ends

    [Fact]
    public void Slots_OfPlainParagraphs_AreOneEach()
    {
        var raw = Raw("one/two//four/");
        var slots = RichEditDocument.SlotsOf(raw, 0, raw.Length);

        Assert.Equal(new[] { "one", "two", "", "four" }, slots.Select(s => raw.Substring(s.Start, s.Length)).ToArray());
        Assert.All(slots, s => Assert.Equal(-1, s.Table));
    }

    [Fact]
    public void Slots_OfATable_AreItsCells_NumberedByRow()
    {
        var raw = Raw("before/{/A1|B1|}/{/A2|B2|}/after/");
        var slots = RichEditDocument.SlotsOf(raw, 0, raw.Length);

        Assert.Equal(new[] { "before", "A1", "B1", "A2", "B2", "after" },
            slots.Select(s => raw.Substring(s.Start, s.Length)).ToArray());
        Assert.Equal(new[] { -1, 0, 0, 0, 0, -1 }, slots.Select(s => s.Table).ToArray());
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 0 }, slots.Select(s => s.Row).ToArray());
    }

    [Fact]
    public void Slots_TwoTablesApart_AreTwoTables_AndAnEmptyCellIsStillACell()
    {
        var raw = Raw("{/x||}/between/{/y|}/");
        var slots = RichEditDocument.SlotsOf(raw, 0, raw.Length);

        Assert.Equal(new[] { "x", "", "between", "y" }, slots.Select(s => raw.Substring(s.Start, s.Length)).ToArray());
        Assert.Equal(new[] { 0, 0, -1, 1 }, slots.Select(s => s.Table).ToArray());
    }

    [Fact]
    public void Slots_OfAPartOfTheText_StayInsideIt()
    {
        var raw = Raw("one/two/three/");
        int from = raw.IndexOf("two", System.StringComparison.Ordinal);
        var slots = RichEditDocument.SlotsOf(raw, from, from + 4);

        var only = Assert.Single(slots);
        Assert.Equal("two", raw.Substring(only.Start, only.Length));
    }

    // ── What a paragraph is called ───────────────────────────────────────────

    private static RichEditDocument.BlockInfo Block(int heading = 0, bool pre = false,
        RichListKind list = RichListKind.None, int level = 0, int quote = 0, bool table = false) =>
        new(0, 1, heading, pre, list, level, quote, table);

    [Fact]
    public void BlockLabel_NamesEachKindOfParagraph_AsTheWpfEditorDoes()
    {
        Assert.Equal("Normal text", ComposeWindow.BlockLabel(Block()));
        Assert.Equal("Heading 3", ComposeWindow.BlockLabel(Block(heading: 3)));
        Assert.Equal("Code block", ComposeWindow.BlockLabel(Block(pre: true)));
        Assert.Equal("Bullet list item", ComposeWindow.BlockLabel(Block(list: RichListKind.Bullet)));
        Assert.Equal("Numbered list item, level 2", ComposeWindow.BlockLabel(Block(list: RichListKind.Numbered, level: 1)));
        Assert.Equal("Table cell", ComposeWindow.BlockLabel(Block(table: true)));
        Assert.Equal("Table cell, row 2, column 3",
            ComposeWindow.BlockLabel(Block(table: true) with { TableRow = 2, TableColumn = 3 }));
        Assert.Equal("Table cell, row 1, column 1, quote",
            ComposeWindow.BlockLabel(Block(table: true, quote: 1) with { TableRow = 1, TableColumn = 1 }));
        Assert.Equal("Quote", ComposeWindow.BlockLabel(Block(quote: 1)));
        Assert.Equal("Quote, level 2", ComposeWindow.BlockLabel(Block(quote: 2)));
        Assert.Equal("Heading 2, quote", ComposeWindow.BlockLabel(Block(heading: 2, quote: 1)));
        Assert.Equal("Bullet list item, quote level 2", ComposeWindow.BlockLabel(Block(list: RichListKind.Bullet, quote: 2)));
    }

    // ── Where the quoted original starts in a rich body ──────────────────────

    [Fact]
    public void RichQuoteBoundaryIndex_IsTheLineThatStartsTheQuotedOriginal()
    {
        const string text = "My reply.\r\n\r\nOn Thursday, Ava Chen wrote:\r\nDo you have five minutes?";
        Assert.Equal(text.IndexOf("On Thursday", System.StringComparison.Ordinal), SpellScan.RichQuoteBoundaryIndex(text));

        const string forward = "See below.\r\n---------- Forwarded message ----------\r\nFrom: someone";
        Assert.Equal(forward.IndexOf("-----", System.StringComparison.Ordinal), SpellScan.RichQuoteBoundaryIndex(forward));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Just my own text.\r\nNothing quoted here.")]
    [InlineData("I said that on Monday she wrote: hello")] // the marker must start a line
    public void RichQuoteBoundaryIndex_IsMinusOne_WithNoQuotedOriginal(string text) =>
        Assert.Equal(-1, SpellScan.RichQuoteBoundaryIndex(text));
}
