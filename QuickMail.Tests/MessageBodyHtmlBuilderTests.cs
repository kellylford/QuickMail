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

    // ── Plain-text view (issue #34) ──────────────────────────────────────────────

    [Fact]
    public void BuildMessageHtml_ForcePlainText_UsesPlainTextPartVerbatim_NoNote()
    {
        var detail = new QuickMail.Models.MailMessageDetail
        {
            Subject       = "Subj",
            HtmlBody      = "<html><body><p>HTML <b>version</b></p></body></html>",
            PlainTextBody = "PLAIN-PART-MARKER line one\nline two",
        };

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: true);

        // The sender's plain-text part is shown verbatim (HTML-encoded), not the HTML body.
        Assert.Contains("PLAIN-PART-MARKER line one", html);
        Assert.Contains("line two", html);
        Assert.DoesNotContain("<b>version</b>", html);
        // A message that HAS a plain part gets no derivation note.
        Assert.DoesNotContain("no plain-text version", html);
        Assert.DoesNotContain("simplified body", html);
    }

    [Fact]
    public void BuildMessageHtml_ForcePlainText_NoPlainPart_ExtractsHtmlWithNote()
    {
        var detail = new QuickMail.Models.MailMessageDetail
        {
            Subject       = "Subj",
            HtmlBody      = "<html><body><p>Extracted <b>content</b> here</p></body></html>",
            PlainTextBody = string.Empty,
        };

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: true);

        // Text is extracted from the HTML (tags stripped) and the derivation note is present.
        Assert.Contains("Extracted", html);
        Assert.Contains("content", html);
        Assert.DoesNotContain("<b>content</b>", html);
        Assert.Contains("no plain-text version", html);
    }

    [Fact]
    public void BuildMessageHtml_ForcePlainText_NoBodyAtAll_EmptyFocusableBodyNoNote()
    {
        var detail = new QuickMail.Models.MailMessageDetail
        {
            Subject       = "Subj",
            HtmlBody      = string.Empty,
            PlainTextBody = string.Empty,
        };

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: true);

        // A focusable document is still produced (so the reading pane can receive focus),
        // and with no HTML to derive from there is no "no plain-text version" note.
        Assert.Contains("tabindex=\"0\"", html);
        Assert.DoesNotContain("no plain-text version", html);
    }

    [Fact]
    public void BuildMessageHtml_ForcePlainTextFalse_MatchesDefault()
    {
        var detail = new QuickMail.Models.MailMessageDetail
        {
            Subject       = "Subj",
            HtmlBody      = "<html><body><p>Hello world</p></body></html>",
            PlainTextBody = "Hello world",
        };

        // The explicit forcePlainText:false call must produce identical output to the
        // default two-arg call — i.e. the new parameter is inert when off.
        var defaultHtml = MessageBodyHtmlBuilder.BuildMessageHtml(detail);
        var explicitHtml = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: false);

        Assert.Equal(defaultHtml, explicitHtml);
        // And the default still renders the HTML body (sanitized), not the plain-text path.
        Assert.Contains("Content-Security-Policy", defaultHtml);
    }

    // ── Issue #483: links must reach the user's default browser ──────────────────

    [Theory]
    [InlineData("<a href=\"https://example.com\" target=\"_blank\">Link</a>")]
    [InlineData("<a href=\"https://example.com\" target='_blank'>Link</a>")]
    [InlineData("<a href=\"https://example.com\" TARGET=_blank>Link</a>")]
    public void StripHeavyHtml_AnchorTarget_IsRemoved(string anchor)
    {
        var result = MessageBodyHtmlBuilder.StripHeavyHtml("<body>" + anchor + "</body>");

        // target="_blank" makes WebView2 raise NewWindowRequested instead of
        // NavigationStarting; a host that only handles the latter opens the link in an
        // in-app popup rather than the default browser.
        Assert.DoesNotContain("_blank", result);
        Assert.DoesNotContain("target", result, System.StringComparison.OrdinalIgnoreCase);
        // The link itself must survive — only the target attribute is dropped.
        Assert.Contains("href=\"https://example.com\"", result);
        Assert.Contains("Link", result);
    }

    [Fact]
    public void BuildMessageHtml_HtmlBodyWithBlankTarget_RendersLinkWithoutTarget()
    {
        var detail = new QuickMail.Models.MailMessageDetail
        {
            Subject  = "Subj",
            HtmlBody = "<html><body><a href=\"https://example.com/reset\" target=\"_blank\">Reset</a></body></html>",
        };

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail);

        Assert.DoesNotContain("_blank", html);
        Assert.Contains("https://example.com/reset", html);
    }
}
