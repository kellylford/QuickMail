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
    public void StripHeavyHtml_ImageAltText_SurvivesTheImage()
    {
        const string html = "<body><p>See the <img src=\"chart.png\" alt=\"Q3 revenue chart\"> above.</p></body>";

        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);

        Assert.DoesNotContain("<img", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Q3 revenue chart", result);
    }

    [Fact]
    public void StripHeavyHtml_IconOnlyLink_IsNamedByAltNotItsHref()
    {
        // Issue #163: the social-icon footer every newsletter ships. Dropping the image left the
        // anchor empty, so it took its accessible name from the tracking href and was announced as
        // "redirect" rather than "Facebook".
        const string html =
            "<body><a href=\"https://substack.com/redirect/a7992ee5\">" +
            "<img src=\"fb.png\" alt=\"Facebook\"></a></body>";

        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);

        Assert.Contains(">Facebook</a>", result);
    }

    [Theory]
    [InlineData("<img src='x.png' alt=''>")]           // author-declared decorative
    [InlineData("<img src='x.png' alt='   '>")]        // whitespace is not a name
    [InlineData("<img src='x.png'>")]                  // no alt at all
    public void StripHeavyHtml_ImageWithoutUsefulAlt_LeavesNothingBehind(string img)
    {
        var result = MessageBodyHtmlBuilder.StripHeavyHtml("<body><p>A</p>" + img + "<p>B</p></body>");

        Assert.DoesNotContain("<img", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x.png", result);
        Assert.Contains("<p>A</p><p>B</p>", result);
    }

    [Fact]
    public void StripHeavyHtml_AltTextWithMarkup_IsEncodedNotSpliced()
    {
        // A bare '<' is legal inside a quoted attribute; moved into content unescaped it would
        // open a tag the sanitizer has already finished inspecting. (A literal <script> in the alt
        // never reaches this pass — the script removal earlier in the chain eats it — so the case
        // worth pinning is the markup that does survive to here.)
        const string html = "<body><img src=\"x.png\" alt=\"a <b>bold</b> logo\"></body>";

        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);

        Assert.Contains("a &lt;b&gt;bold&lt;/b&gt; logo", result);
    }

    [Fact]
    public void StripHeavyHtml_AltTextEntities_StayDecodedOnce()
    {
        const string html = "<body><img src=\"x.png\" alt=\"Tom &amp; Jerry\"></body>";

        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);

        Assert.Contains("Tom &amp; Jerry", result);
        Assert.DoesNotContain("&amp;amp;", result);
    }

    [Fact]
    public void HtmlToText_ImageAltText_ReachesTheSimplifiedBody()
    {
        var text = MessageBodyHtmlBuilder.HtmlToText(
            "<body><p>Follow us on <a href=\"http://t.example/c/1p\"><img alt=\"Facebook\"></a></p></body>");

        Assert.Contains("Facebook", text);
    }

    [Fact]
    public void TryStripHeavyHtml_ImageAltPassTimeout_ReturnsFalse()
    {
        // The alt-substitution pass must fail closed with the rest: a partially substituted
        // document is not a sanitized one.
        var html = string.Concat(System.Linq.Enumerable.Repeat(
            "<img src=\"x.png\" alt=\"icon\">", 5000));

        var ok = MessageBodyHtmlBuilder.TryStripHeavyHtml(
            html, System.TimeSpan.FromTicks(1), out _);

        Assert.False(ok);
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

    // ---------------------------------------------------------------------------------------
    // Script execution from a crafted message (reported privately 2026-09-09).
    //
    // Two independent gaps combined into arbitrary script in the reading pane from a message the
    // user only had to open: the CSP was spliced in at the first literal "<head>" ANYWHERE in the
    // sender's markup, so a sender who wrote that string mid-paragraph got the policy emitted into
    // body content where a browser ignores it; and the sanitizer's end-tag patterns required a
    // literal "</script>", while the tokenizer also closes the element at "</script >".
    //
    // These assert the invariants, not the two shapes of the original proof of concept: the CSP is
    // a child of the real head no matter what the sender writes, and an end tag is recognised in
    // every form the tokenizer recognises it.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("</script>")]
    [InlineData("</script >")]
    [InlineData("</script\t>")]
    [InlineData("</script/>")]
    [InlineData("</script foo=\"bar\">")]
    [InlineData("</SCRIPT >")]
    public void BuildSanitizedDocument_ScriptClosedAnyTokenizerWay_IsStripped(string endTag)
    {
        var body = "<p>Hello.</p><script>window.__pwned=1;" + endTag;

        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument("Subj", body, out var doc));

        Assert.DoesNotContain("__pwned", doc, System.StringComparison.Ordinal);
        Assert.Contains("Hello.", doc, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("</style>")]
    [InlineData("</style >")]
    [InlineData("</style/>")]
    public void BuildSanitizedDocument_StyleClosedAnyTokenizerWay_IsStripped(string endTag)
    {
        // A surviving sender stylesheet is not merely a rendering problem: [aria-live]{display:none}
        // or content-visibility:hidden takes the in-document status regions (#329, #671) out of the
        // accessibility tree, and a status region that announces nothing reads as success.
        var body = "<p>Hello.</p><style>[aria-live]{content-visibility:hidden !important}" + endTag;

        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument("Subj", body, out var doc));

        Assert.DoesNotContain("aria-live]", doc, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSanitizedDocument_EndTagOfLongerName_IsNotTreatedAsMatch()
    {
        // "</scriptable>" closes a different element; accepting it would silently eat real content.
        const string body = "<p>Hi.</p><scriptable>keep me</scriptable>";

        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument("Subj", body, out var doc));

        Assert.Contains("keep me", doc, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<p>Hello.</p><head><script>window.__pwned=1;</script >")]
    [InlineData("<p>Hello.</p><HEAD>")]
    [InlineData("<html><head><title>Sender title</title></head><body><p>Hello.</p></body></html>")]
    public void BuildSanitizedDocument_WhateverTheSenderWrites_CspIsInsideTheRealHead(string body)
    {
        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument("Subj", body, out var doc));

        // A meta CSP is a policy only as a child of the document's own head. Asserting the document
        // merely CONTAINS the meta is what let the original bug through: it was present every time,
        // and inert whenever a sender supplied the string "<head>".
        var headStart = doc.IndexOf("<head>", System.StringComparison.Ordinal);
        var headEnd   = doc.IndexOf("</head>", System.StringComparison.Ordinal);
        var cspIdx    = doc.IndexOf("Content-Security-Policy", System.StringComparison.Ordinal);

        Assert.StartsWith("<!DOCTYPE html><html lang=\"en\"><head>", doc, System.StringComparison.Ordinal);
        Assert.InRange(cspIdx, headStart, headEnd);
        Assert.Contains("Hello.", doc, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSanitizedDocument_SenderStructuralTags_DoNotReachTheDocumentsOwnElements()
    {
        // A stray <body> start tag in body content is ignored by the parser, but its ATTRIBUTES are
        // merged onto the real body element.
        const string body = "<p>Hi.</p><body class=\"sender\" data-evil=\"1\">more";

        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument("Subj", body, out var doc));

        Assert.DoesNotContain("data-evil", doc, System.StringComparison.Ordinal);
        Assert.Contains("<body tabindex=\"0\">", doc, System.StringComparison.Ordinal);
        Assert.Contains("more", doc, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSanitizedDocument_SenderTitle_DoesNotBecomeTheDocumentTitle()
    {
        // <title> in body content is handled by the parser's in-head rules, so a sender one would
        // set document.title over the message's own subject.
        const string body = "<p>Hi.</p><title>Sender title</title>";

        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument("Subj", body, out var doc));

        Assert.DoesNotContain("Sender title", doc, System.StringComparison.Ordinal);
        Assert.Contains("<title>Subj</title>", doc, System.StringComparison.Ordinal);
    }
}
