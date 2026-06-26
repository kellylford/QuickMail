using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for HtmlStripper: HTML → plain text used by the text/plain MIME part
/// and rich → plain mode conversion.
/// </summary>
public class HtmlStripperTests
{
    [Fact]
    public void Strip_SimpleParagraphs_ReturnsPlainTextWithBlankLine()
    {
        var text = HtmlStripper.ToPlainText("<p>First.</p><p>Second.</p>");
        Assert.Equal("First.\n\nSecond.", text);
    }

    [Fact]
    public void Strip_Br_BecomesNewline()
    {
        Assert.Equal("one\ntwo", HtmlStripper.ToPlainText("one<br>two"));
    }

    [Fact]
    public void Strip_Links_KeepsUrlInParens()
    {
        var text = HtmlStripper.ToPlainText("<a href=\"https://example.com\">Click here</a>");
        Assert.Equal("Click here (https://example.com)", text);
    }

    [Fact]
    public void Strip_Link_TextEqualsUrl_NoDuplicateParens()
    {
        var text = HtmlStripper.ToPlainText("<a href=\"https://example.com/\">https://example.com/</a>");
        Assert.Equal("https://example.com/", text);
    }

    [Fact]
    public void Strip_UnorderedList_ConvertsToBullets()
    {
        var text = HtmlStripper.ToPlainText("<ul><li>apple</li><li>pear</li></ul>");
        Assert.Contains("• apple", text);
        Assert.Contains("• pear", text);
    }

    [Fact]
    public void Strip_OrderedList_NumbersItems()
    {
        var text = HtmlStripper.ToPlainText("<ol><li>first</li><li>second</li></ol>");
        Assert.Contains("1. first", text);
        Assert.Contains("2. second", text);
    }

    [Fact]
    public void Strip_DecodesHtmlEntities()
    {
        Assert.Equal("Tom & Jerry's <show>", HtmlStripper.ToPlainText("<p>Tom &amp; Jerry&#39;s &lt;show&gt;</p>"));
    }

    [Fact]
    public void Strip_RemovesScriptAndStyleContent()
    {
        var text = HtmlStripper.ToPlainText(
            "<style>p { color: red; }</style><p>visible</p><script>alert('x')</script>");
        Assert.Equal("visible", text);
    }

    [Fact]
    public void Strip_CollapsesExcessNewlines()
    {
        var text = HtmlStripper.ToPlainText("<p>a</p><br><br><br><p>b</p>");
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void Strip_ImgAltText_RendersInBrackets()
    {
        var text = HtmlStripper.ToPlainText("<p>logo: <img src=\"cid:x\" alt=\"Company logo\"></p>");
        Assert.Equal("logo: [Company logo]", text);
    }

    [Fact]
    public void Strip_Headings_SeparatedFromBody()
    {
        var text = HtmlStripper.ToPlainText("<h1>Title</h1><p>Body text.</p>");
        Assert.Equal("Title\n\nBody text.", text);
    }

    [Fact]
    public void Strip_EmptyAndNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HtmlStripper.ToPlainText(""));
        Assert.Equal(string.Empty, HtmlStripper.ToPlainText(null));
    }

    [Fact]
    public void Strip_JavascriptHref_NotEmitted()
    {
        var text = HtmlStripper.ToPlainText("<a href=\"javascript:alert(1)\">click</a>");
        Assert.Equal("click", text);
    }

    [Fact]
    public void Strip_DataHref_NotEmitted()
    {
        var text = HtmlStripper.ToPlainText("<a href=\"data:application/javascript,alert(1)\">click</a>");
        Assert.Equal("click", text);
    }

    // Preview-text regression: the old StripHtml regex only removed <tags>, leaving
    // CSS inside <style> blocks visible as raw text in the message list.
    [Fact]
    public void Strip_StyleBlockCss_NotAppearedInPreview()
    {
        var html = "<style>body { font-family: Arial; } .preheader { display: none; }</style><p>Hello world</p>";
        var text = HtmlStripper.ToPlainText(html);
        Assert.DoesNotContain("font-family", text);
        Assert.DoesNotContain("display", text);
        Assert.Contains("Hello world", text);
    }

    [Fact]
    public void Strip_ScriptBlockContent_NotAppearedInPreview()
    {
        var html = "<script>var x = 'tracking code';</script><p>Actual content</p>";
        var text = HtmlStripper.ToPlainText(html);
        Assert.DoesNotContain("tracking code", text);
        Assert.Contains("Actual content", text);
    }

    [Fact]
    public void Strip_MultipleStyleBlocks_NoneLeakToPreview()
    {
        var html =
            "<head><style>.a{color:red}</style></head>" +
            "<body><style>.b{margin:0}</style><p>Body text</p></body>";
        var text = HtmlStripper.ToPlainText(html);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("margin", text);
        Assert.Contains("Body text", text);
    }

    [Fact]
    public void Strip_EmptyStyleBlock_ProducesNoOutput()
    {
        var text = HtmlStripper.ToPlainText("<style></style><p>hi</p>");
        Assert.Equal("hi", text);
    }
}
