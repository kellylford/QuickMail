using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for MarkdownService: Markdown → HTML rendering, plain-text conversion,
/// plain-text escaping, and the email document wrapper.
/// </summary>
public class MarkdownServiceTests
{
    private readonly MarkdownService _svc = new();

    [Fact]
    public void ToHtml_BasicMarkdown_RendersStrongAndEm()
    {
        var html = _svc.ToHtml("This is **bold** and *italic*.");
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<em>italic</em>", html);
    }

    [Fact]
    public void ToHtml_Heading_RendersH2()
    {
        var html = _svc.ToHtml("## Section");
        Assert.Contains("<h2", html);
        Assert.Contains("Section", html);
    }

    [Fact]
    public void ToHtml_Table_RendersHtmlTable()
    {
        var html = _svc.ToHtml("| A | B |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<table>", html);
        Assert.Contains("<td>1</td>", html);
    }

    [Fact]
    public void ToHtml_FencedCodeBlock_RendersPreCode()
    {
        var html = _svc.ToHtml("```\nvar x = 1;\n```");
        Assert.Contains("<pre><code>", html);
        Assert.Contains("var x = 1;", html);
    }

    [Fact]
    public void ToHtml_Link_RendersAnchor()
    {
        var html = _svc.ToHtml("[QuickMail](https://example.com)");
        Assert.Contains("<a href=\"https://example.com\">QuickMail</a>", html);
    }

    [Fact]
    public void ToHtml_SoftLineBreak_BecomesBr()
    {
        var html = _svc.ToHtml("line one\nline two");
        Assert.Contains("<br", html);
    }

    [Fact]
    public void ToHtml_RawHtml_IsNeutralized()
    {
        // DisableHtml: raw markup in Markdown must not pass through as active HTML.
        var html = _svc.ToHtml("<script>alert(1)</script>");
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void ToHtml_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, _svc.ToHtml(""));
    }

    [Fact]
    public void ToPlainText_StripsFormatting()
    {
        var text = _svc.ToPlainText("# Hello\n\nThis is **bold**.");
        Assert.Contains("Hello", text);
        Assert.Contains("This is bold.", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("#", text);
    }

    [Fact]
    public void ToPlainText_Link_KeepsUrl()
    {
        var text = _svc.ToPlainText("[Click here](https://example.com)");
        Assert.Contains("Click here", text);
        Assert.Contains("https://example.com", text);
    }

    [Fact]
    public void PlainTextToHtml_EscapesEntities()
    {
        var html = _svc.PlainTextToHtml("a < b & c > d");
        Assert.Contains("&lt;", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("a < b", html);
    }

    [Fact]
    public void PlainTextToHtml_ParagraphsAndLineBreaks()
    {
        var html = _svc.PlainTextToHtml("para one\nstill para one\n\npara two");
        Assert.Contains("<p>para one<br />still para one</p>", html);
        Assert.Contains("<p>para two</p>", html);
    }

    [Fact]
    public void WrapDocument_ProducesValidHtml5Document()
    {
        var doc = _svc.WrapDocument("<p>hi</p>", "Lunch Friday");
        Assert.StartsWith("<!DOCTYPE html>", doc);
        Assert.Contains("<html lang=\"", doc);
        Assert.Contains("<meta charset=\"utf-8\" />", doc);
        Assert.Contains("<title>Lunch Friday</title>", doc);
        Assert.Contains("font-family", doc);
        Assert.Contains("<p>hi</p>", doc);
        Assert.EndsWith("</html>", doc);
    }

    [Fact]
    public void WrapDocument_NoSubject_StillHasTitle()
    {
        var doc = _svc.WrapDocument("<p>hi</p>");
        Assert.Contains("<title>Email message</title>", doc);
    }

    [Fact]
    public void WrapDocument_TitleIsEncoded()
    {
        var doc = _svc.WrapDocument("<p>hi</p>", "Q3 <results> & more");
        Assert.Contains("<title>Q3 &lt;results&gt; &amp; more</title>", doc);
        Assert.DoesNotContain("<title>Q3 <results>", doc);
    }

    [Fact]
    public void ToHtml_TaskListSyntax_StaysLiteralText_NoInputElements()
    {
        // Task-list checkboxes render as unlabeled <input> elements, which fail
        // WCAG 4.1.2 and are stripped by most mail clients — the pipeline keeps
        // the bracket syntax as literal, accessible text instead.
        var html = _svc.ToHtml("- [x] done\n- [ ] not yet");
        Assert.DoesNotContain("<input", html);
        Assert.Contains("[x] done", html);
    }

    [Fact]
    public void ToHtml_Strikethrough_RendersDel()
    {
        var html = _svc.ToHtml("~~gone~~");
        Assert.Contains("<del>gone</del>", html);
    }

    [Fact]
    public void HtmlToPlainText_DelegatesToStripper()
    {
        Assert.Equal("hello world", _svc.HtmlToPlainText("<p>hello <strong>world</strong></p>"));
    }
}
