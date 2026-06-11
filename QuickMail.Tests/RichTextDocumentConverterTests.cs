using System.Linq;
using System.Windows;
using System.Windows.Documents;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for RichTextDocumentConverter: the FlowDocument ↔ HTML/Markdown bridge
/// behind the HTML compose mode. FlowDocument requires an STA thread.
/// </summary>
[Collection("WpfTests")]
public class RichTextDocumentConverterTests
{
    [StaFact]
    public void FromHtml_BoldParagraph_RoundTripsToHtml()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p>plain <strong>bold</strong> tail</p>");
        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.Equal("<p>plain <strong>bold</strong> tail</p>", html);
    }

    [StaFact]
    public void FromHtml_ItalicUnderlineStrike_RoundTrip()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p><em>i</em> <u>u</u> <del>d</del></p>");
        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.Contains("<em>i</em>", html);
        Assert.Contains("<u>u</u>", html);
        Assert.Contains("<del>d</del>", html);
    }

    [StaFact]
    public void FromHtml_Heading_RoundTripsViaTag()
    {
        var doc = RichTextDocumentConverter.FromHtml("<h2>Section title</h2><p>body</p>");
        var heading = doc.Blocks.OfType<Paragraph>().First();
        Assert.Equal("H2", heading.Tag);
        Assert.Equal(FontWeights.Bold, heading.FontWeight);

        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.Contains("<h2>Section title</h2>", html);
        // Heading bold comes from the paragraph — runs must not re-emit <strong>.
        Assert.DoesNotContain("<strong>", html);
    }

    [StaFact]
    public void FromHtml_Lists_RoundTrip()
    {
        var doc = RichTextDocumentConverter.FromHtml("<ul><li>one</li><li>two</li></ul><ol><li>first</li></ol>");
        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<ol>", html);
        Assert.Contains("<li>first</li>", html);
    }

    [StaFact]
    public void FromHtml_Hyperlink_RoundTrip()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p><a href=\"https://example.com/\">site</a></p>");
        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.Contains("<a href=\"https://example.com/\">site</a>", html);
    }

    [StaFact]
    public void FromHtml_JavascriptHref_NotNavigable()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p><a href=\"javascript:alert(1)\">x</a></p>");
        var link = doc.Blocks.OfType<Paragraph>().First().Inlines.OfType<Hyperlink>().First();
        Assert.Null(link.NavigateUri);
    }

    [StaFact]
    public void FromHtml_EscapedEntities_DecodeAndReencode()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p>a &amp; b &lt;tag&gt;</p>");
        var text = RichTextDocumentConverter.ToPlainText(doc);
        Assert.Equal("a & b <tag>", text);
        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.Contains("&amp;", html);
        Assert.Contains("&lt;tag&gt;", html);
    }

    [StaFact]
    public void ToMarkdown_BoldItalicHeadingListLink()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            "<h1>Title</h1><p><strong>bold</strong> and <em>italic</em></p>" +
            "<ul><li>item</li></ul><p><a href=\"https://example.com/\">link</a></p>");
        var md = RichTextDocumentConverter.ToMarkdown(doc);

        Assert.Contains("# Title", md);
        Assert.Contains("**bold**", md);
        Assert.Contains("*italic*", md);
        Assert.Contains("- item", md);
        Assert.Contains("[link](https://example.com/)", md);
    }

    [StaFact]
    public void ToMarkdown_NumberedList()
    {
        var doc = RichTextDocumentConverter.FromHtml("<ol><li>first</li><li>second</li></ol>");
        var md = RichTextDocumentConverter.ToMarkdown(doc);
        Assert.Contains("1. first", md);
        Assert.Contains("2. second", md);
    }

    [StaFact]
    public void ToPlainText_StripsAllFormatting()
    {
        var doc = RichTextDocumentConverter.FromHtml("<h1>Title</h1><p><strong>bold</strong> text</p>");
        var text = RichTextDocumentConverter.ToPlainText(doc);
        Assert.Contains("Title", text);
        Assert.Contains("bold text", text);
        Assert.DoesNotContain("<", text);
    }

    [StaFact]
    public void FromHtml_Empty_ProducesEditableDocument()
    {
        var doc = RichTextDocumentConverter.FromHtml("");
        Assert.NotEmpty(doc.Blocks);
        Assert.Equal(string.Empty, RichTextDocumentConverter.ToPlainText(doc));
    }

    [StaFact]
    public void Snapshot_ProducesAllThreeRepresentations()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p><strong>hi</strong> there</p>");
        var snapshot = RichTextDocumentConverter.Snapshot(doc);
        Assert.Contains("<strong>hi</strong>", snapshot.Html);
        Assert.Contains("**hi**", snapshot.Markdown);
        Assert.Equal("hi there", snapshot.PlainText);
        Assert.False(snapshot.IsEmpty);
    }

    [StaFact]
    public void MarkdigOutput_LoadsCleanly()
    {
        // The exact pipeline used in Markdown→HTML mode switches.
        var svc = new QuickMail.Services.MarkdownService();
        var html = svc.ToHtml("# Head\n\n**bold** *it*\n\n- a\n- b\n\n[x](https://e.com)");
        var doc = RichTextDocumentConverter.FromHtml(html);

        var roundTripped = RichTextDocumentConverter.ToHtml(doc);
        Assert.Contains("<h1>Head</h1>", roundTripped);
        Assert.Contains("<strong>bold</strong>", roundTripped);
        Assert.Contains("<li>a</li>", roundTripped);
        Assert.Contains("https://e.com", roundTripped);
    }
}
