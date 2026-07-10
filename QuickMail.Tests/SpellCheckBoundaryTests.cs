using System.Windows.Documents;
using QuickMail.Helpers;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for the quoted/forwarded-original boundary detection that keeps spell check inside the
/// user's own text on reply/forward (issue #228).
/// </summary>
public class SpellCheckBoundaryTests
{
    [Fact]
    public void QuoteBoundaryIndex_FindsReplyAttribution()
    {
        var body = "My reply text with a typpo.\n\nOn Monday, June 1, 2026, Bob <b@x.com> wrote:\n> original line";

        var idx = SpellScan.QuoteBoundaryIndex(body);

        Assert.True(idx > 0);
        Assert.StartsWith("On Monday", body[idx..]);
        Assert.Contains("My reply text", body[..idx]);
        Assert.DoesNotContain("original line", body[..idx]);
    }

    [Fact]
    public void QuoteBoundaryIndex_FindsForwardHeader()
    {
        var body = "See below.\n\n---------- Forwarded message ----------\nFrom: Bob\nSubject: Hi";

        var idx = SpellScan.QuoteBoundaryIndex(body);

        Assert.True(idx > 0);
        Assert.StartsWith("---------- Forwarded message", body[idx..]);
        Assert.Contains("See below.", body[..idx]);
    }

    [Fact]
    public void QuoteBoundaryIndex_ReturnsMinusOne_WhenNoQuote()
    {
        Assert.Equal(-1, SpellScan.QuoteBoundaryIndex("Just a fresh message, no quote."));
        Assert.Equal(-1, SpellScan.QuoteBoundaryIndex(""));
    }

    [Fact]
    public void QuoteBoundaryIndex_TakesEarliestMarker_OnNestedQuotes()
    {
        // A nested/original quote further down must not win over the outer attribution.
        var body = "My note.\n\nOn Tue, Alice wrote:\n> On Mon, Bob wrote:\n> > deeper";

        var idx = SpellScan.QuoteBoundaryIndex(body);

        Assert.StartsWith("On Tue, Alice wrote:", body[idx..]);
    }

    [StaFact]
    public void QuoteBoundaryPointer_FindsForwardHeaderParagraph_AboveBlockquote()
    {
        // Mirrors the real forward structure: an nbsp spacer, the header paragraph, then the quoted
        // body in a blockquote. The boundary must land on the header paragraph (above the quote).
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph(new Run(" ")));
        var header = new Paragraph(new Run("---------- Forwarded message ----------\nFrom: Bob"));
        doc.Blocks.Add(header);
        doc.Blocks.Add(new Paragraph(new Run("Original quoted content")) { Tag = RichTextDocumentConverter.TagBlockquote });

        var ptr = SpellScan.QuoteBoundaryPointer(doc);

        Assert.NotNull(ptr);
        Assert.Equal(0, ptr!.CompareTo(header.ContentStart));
    }

    [StaFact]
    public void QuoteBoundaryPointer_IgnoresLoneUserBlockquote()
    {
        // A blockquote the user inserts themselves (no reply/forward marker) must NOT be treated as
        // the boundary — otherwise their text below it would be silently skipped.
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph(new Run("My own text.")));
        doc.Blocks.Add(new Paragraph(new Run("A quote I pasted")) { Tag = RichTextDocumentConverter.TagBlockquote });
        doc.Blocks.Add(new Paragraph(new Run("More of my text.")));

        Assert.Null(SpellScan.QuoteBoundaryPointer(doc));
    }

    [StaFact]
    public void QuoteBoundaryPointer_FindsAttributionParagraph()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph(new Run("My reply.")));
        var attribution = new Paragraph(new Run("On Monday, Bob wrote:"));
        doc.Blocks.Add(attribution);

        var ptr = SpellScan.QuoteBoundaryPointer(doc);

        Assert.NotNull(ptr);
        Assert.Equal(0, ptr!.CompareTo(attribution.ContentStart));
    }

    [StaFact]
    public void QuoteBoundaryPointer_ReturnsNull_WhenNoQuote()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph(new Run("A fresh message with no quoted original.")));

        Assert.Null(SpellScan.QuoteBoundaryPointer(doc));
    }
}
