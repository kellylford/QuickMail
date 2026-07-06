using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class MessageBodyHtmlBuilderTests
{
    [Fact]
    public void StripHeavyHtml_PreheaderDivDisplayNone_IsRemoved()
    {
        // U+034F (Combining Grapheme Joiner) and U+200C (Zero-Width Non-Joiner) are the
        // invisible padding characters newsletter tools inject into preheader divs.
        const string preheaderContent = "PREHEADER-PADDING-MARKER";
        var html =
            "<body>" +
            $"<div style=\"display: none; max-height: 0px; overflow: hidden;\">{preheaderContent}</div>" +
            "<p>Real content</p>" +
            "</body>";

        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);

        Assert.DoesNotContain(preheaderContent, result);
        Assert.Contains("Real content", result);
    }

    [Fact]
    public void StripHeavyHtml_PreheaderSpanDisplayNone_IsRemoved()
    {
        const string html =
            "<body>" +
            "<span style='display:none'>hidden</span>" +
            "<p>Visible</p>" +
            "</body>";

        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);

        Assert.DoesNotContain("hidden", result);
        Assert.Contains("Visible", result);
    }

    [Fact]
    public void StripHeavyHtml_VisibleDiv_IsPreserved()
    {
        const string html = "<div>Keep this</div>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.Contains("Keep this", result);
    }

    [Fact]
    public void TryStripHeavyHtml_NormalInput_ReturnsTrueAndStrips()
    {
        const string html = "<body><script>alert(1)</script><p onclick=\"x()\">Hello</p></body>";

        var ok = MessageBodyHtmlBuilder.TryStripHeavyHtml(html, out var stripped);

        Assert.True(ok);
        Assert.DoesNotContain("<script", stripped, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", stripped, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello", stripped);
    }

    [Fact]
    public void TryStripHeavyHtml_Timeout_ReturnsFalse()
    {
        // A 1-tick timeout cannot complete any pass over a non-trivial document, which
        // simulates a message crafted to stall the stripping regexes.
        var html = string.Concat(System.Linq.Enumerable.Repeat(
            "<div style=\"color:red\"><p onclick=\"x()\">content</p></div>", 5000));

        var ok = MessageBodyHtmlBuilder.TryStripHeavyHtml(
            html, System.TimeSpan.FromTicks(1), out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryBuildSanitizedHtmlDocument_Timeout_FailsClosed()
    {
        var html = string.Concat(System.Linq.Enumerable.Repeat(
            "<div style=\"color:red\"><p onclick=\"x()\">content</p></div>", 5000));

        var ok = MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument(
            "Subject", html, themeCss: null, System.TimeSpan.FromTicks(1), out var document);

        // The partially stripped document must be discarded, never rendered.
        Assert.False(ok);
        Assert.Equal(string.Empty, document);
    }

    [Fact]
    public void TryBuildSanitizedHtmlDocument_NormalInput_ProducesCspDocument()
    {
        var ok = MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument(
            "Subject", "<p>Body text</p>", out var document);

        Assert.True(ok);
        Assert.Contains("Content-Security-Policy", document);
        Assert.Contains("script-src 'none'", document);
        Assert.Contains("Body text", document);
    }

    [Fact]
    public void BuildMessageHtml_ComplexHtml_FallsBackToReaderMode()
    {
        // Over the table-count threshold: the builder must switch to the simplified body.
        var tables = string.Concat(System.Linq.Enumerable.Repeat("<table><tr><td>x</td></tr></table>", 501));
        var detail = new QuickMail.Models.MailMessageDetail
        {
            HtmlBody = "<html><body>" + tables + "</body></html>",
            PlainTextBody = "Plain fallback text",
        };

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail);

        Assert.Contains("Plain fallback text", html);
        Assert.Contains("simplified body", html);
    }
}
