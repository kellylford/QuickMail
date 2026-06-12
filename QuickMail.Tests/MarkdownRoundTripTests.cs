using System.IO;
using System.Linq;
using System.Windows.Documents;
using System.Xml;
using QuickMail.Helpers;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// End-to-end fidelity tests for the compose pipeline:
///
/// 1. Markdown → HTML (Markdig) → FlowDocument → Markdown must be lossless —
///    this is exactly what switching Markdown → HTML mode and back does.
/// 2. The full document handed to the text/html MIME part must be well-formed,
///    valid HTML with the structural WCAG 2.2 AA requirements (lang, title,
///    alt text, table header semantics) intact.
/// </summary>
[Collection("WpfTests")]
public class MarkdownRoundTripTests
{
    private static readonly MarkdownService Svc = new();

    /// <summary>Markdown → Markdig HTML → FlowDocument → Markdown.</summary>
    private static string RoundTrip(string markdown) =>
        RichTextDocumentConverter.ToMarkdown(
            RichTextDocumentConverter.FromHtml(Svc.ToHtml(markdown)));

    // ── Exact round-trips: the editor must hand back what the author wrote ────

    [StaTheory]
    [InlineData("# Title")]
    [InlineData("## Section")]
    [InlineData("#### Deep heading")]
    [InlineData("###### Deepest heading")]
    [InlineData("Just a paragraph.")]
    [InlineData("This is **bold** and *italic* text.")]
    [InlineData("***both at once***")]
    [InlineData("*a* and *b*")]
    [InlineData("~~struck through~~")]
    [InlineData("Inline `code span` here.")]
    [InlineData("Line one\nLine two")]
    [InlineData("Para one\n\nPara two")]
    [InlineData("- one\n- two\n- three")]
    [InlineData("1. first\n2. second")]
    [InlineData("- parent\n  - child")]
    [InlineData("1. parent\n   1. child")]
    [InlineData("> quoted text")]
    [InlineData("> first paragraph\n>\n> second paragraph")]
    [InlineData("---")]
    [InlineData("```\ncode here\n```")]
    [InlineData("```csharp\nvar x = 1;\n```")]
    [InlineData("[QuickMail](https://example.com)")]
    [InlineData("[C lang](https://en.wikipedia.org/wiki/C_%28programming_language%29)")]
    [InlineData("![Logo](https://example.com/logo.png)")]
    [InlineData("![Figure 1\\]](https://example.com/fig1.png)")]
    [InlineData("![](https://example.com/decorative.png)")]
    [InlineData("https://example.com/page")]
    [InlineData("- [x] done\n- [ ] not yet")]
    [InlineData("| A | B |\n| --- | --- |\n| 1 | 2 |")]
    [InlineData("# Plan\n\nFirst **bold** step.\n\n- alpha\n- beta\n\n[docs](https://example.com/docs)")]
    public void MarkdownRoundTrip_IsExact(string markdown)
    {
        Assert.Equal(markdown, RoundTrip(markdown));
    }

    [StaFact]
    public void MarkdownRoundTrip_TableAlignment_Survives()
    {
        var md = "| Name | Score |\n| :---: | ---: |\n| Kelly | 10 |";
        Assert.Equal(md, RoundTrip(md));
    }

    [StaFact]
    public void MarkdownRoundTrip_KitchenSink_IsStableAfterFirstPass()
    {
        // Anything not byte-exact on the first pass must at least be a fixed
        // point: a second round-trip may not lose or alter anything further.
        var md = KitchenSinkMarkdown;
        var once = RoundTrip(md);
        var twice = RoundTrip(once);
        Assert.Equal(once, twice);
    }

    // ── Accessibility information survives the trip into the editor ───────────

    [StaFact]
    public void ImageAltText_SurvivesIntoEditorAndBack()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            Svc.ToHtml("![Chart of Q3 results](https://example.com/q3.png)"));

        // The alt text is what a screen reader user reads and edits in the editor.
        var para = doc.Blocks.OfType<Paragraph>().First();
        var run = para.Inlines.OfType<Run>().First();
        Assert.Equal("Chart of Q3 results", run.Text);

        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.Contains("alt=\"Chart of Q3 results\"", html);
        Assert.Contains("src=\"https://example.com/q3.png\"", html);
    }

    [StaFact]
    public void TableHeaders_SurviveAsThWithScope()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            Svc.ToHtml("| Name | Score |\n| --- | --- |\n| Kelly | 10 |"));
        var html = RichTextDocumentConverter.ToHtml(doc);

        Assert.Contains("<th scope=\"col\">Name</th>", html);
        Assert.Contains("<th scope=\"col\">Score</th>", html);
        Assert.Contains("<td>Kelly</td>", html);
    }

    [StaFact]
    public void LinkHref_IsPreservedVerbatim()
    {
        // NavigateUri normalizes URIs (adds slashes, lowercases hosts); the
        // serialized href must be exactly what the author wrote.
        var doc = RichTextDocumentConverter.FromHtml(
            "<p><a href=\"https://Example.com/Path?q=A%20B\">link</a></p>");
        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.Contains("href=\"https://Example.com/Path?q=A%20B\"", html);
    }

    [StaFact]
    public void SpacesBetweenStyledSpans_AreNotSwallowed()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p><em>a</em> <strong>b</strong></p>");
        Assert.Equal("a b", RichTextDocumentConverter.ToPlainText(doc));
        Assert.Equal("*a* **b**", RichTextDocumentConverter.ToMarkdown(doc));
    }

    [StaFact]
    public void DangerousLinkAndImageSchemes_DoNotRoundTrip()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            "<p><a href=\"javascript:alert(1)\">x</a> <img src=\"data:text/html,bad\" alt=\"pic\" /></p>");
        var html = RichTextDocumentConverter.ToHtml(doc);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("data:", html);
        Assert.Contains("x", html);     // link text survives without the anchor
        Assert.Contains("[pic]", html); // image degrades to its alt text
    }

    // ── The sent text/html part is a valid, WCAG-conformant document ──────────

    private const string KitchenSinkMarkdown =
        "# Status report\n\n" +
        "Hello **team**, here is *this week's* update — `v1.2` shipped.\n\n" +
        "## Done\n\n" +
        "- item one\n- item two with [a link](https://example.com/page?a=1&b=2)\n" +
        "  - nested detail\n\n" +
        "1. first\n2. second\n\n" +
        "> A quoted remark\n>\n> spanning two paragraphs.\n\n" +
        "| Area | Status |\n| :---: | ---: |\n| Sync | Good |\n| UI | OK |\n\n" +
        "```csharp\nvar x = \"a < b & c\";\n```\n\n" +
        "![Burndown chart](https://example.com/burndown.png)\n\n" +
        "---\n\n" +
        "~~Old plan~~ and the new one.\nSecond line.";

    private static void AssertWellFormed(string fullDocument)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(new StringReader(fullDocument), settings);
        while (reader.Read()) { } // throws XmlException on malformed markup
    }

    [StaFact]
    public void SentHtml_MarkdownMode_IsWellFormedAndAccessible()
    {
        // Exactly what BuildComposeModel produces in Markdown mode.
        var document = Svc.WrapDocument(Svc.ToHtml(KitchenSinkMarkdown), "Status report");

        AssertWellFormed(document);
        Assert.StartsWith("<!DOCTYPE html>", document);
        Assert.Contains("<html lang=\"", document);          // WCAG 3.1.1
        Assert.Contains("<title>Status report</title>", document); // WCAG 2.4.2
        Assert.Contains("alt=\"Burndown chart\"", document);  // WCAG 1.1.1
        Assert.Contains("<th", document);                     // WCAG 1.3.1
        Assert.DoesNotContain("<script", document);
        Assert.DoesNotContain("<input", document);
    }

    [StaFact]
    public void SentHtml_HtmlMode_IsWellFormedAndAccessible()
    {
        // Exactly what BuildComposeModel produces in HTML mode: Markdig output
        // loaded into the editor, serialized back, then wrapped.
        var doc = RichTextDocumentConverter.FromHtml(Svc.ToHtml(KitchenSinkMarkdown));
        var document = Svc.WrapDocument(RichTextDocumentConverter.ToHtml(doc), "Status report");

        AssertWellFormed(document);
        Assert.Contains("<html lang=\"", document);
        Assert.Contains("<title>Status report</title>", document);
        Assert.Contains("alt=\"Burndown chart\"", document);
        Assert.Contains("scope=\"col\"", document);
        Assert.DoesNotContain("<script", document);
    }

    [StaFact]
    public void SentHtml_PlainTextUpgrade_IsWellFormed()
    {
        var document = Svc.WrapDocument(
            Svc.PlainTextToHtml("a < b & c > d\nsecond line\n\nnew paragraph"), "Notes");
        AssertWellFormed(document);
    }
}
