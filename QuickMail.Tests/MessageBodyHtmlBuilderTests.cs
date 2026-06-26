using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class MessageBodyHtmlBuilderTests
{
    // ── Existing preheader tests ──────────────────────────────────────────────────

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

    // ── Phase 2: Generalized display:none / visibility:hidden pre-pass ────────────

    [Fact]
    public void StripHeavyHtml_DisplayNoneOnTableCell_IsRemoved()
    {
        var html = "<table><tr>" +
                   "<td style=\"display:none\">secret</td>" +
                   "<td>visible</td>" +
                   "</tr></table>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("secret", result);
        Assert.Contains("visible", result);
    }

    [Fact]
    public void StripHeavyHtml_DisplayNoneOnSection_IsRemoved()
    {
        var html = "<section style='display: none'>hidden section</section><p>shown</p>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("hidden section", result);
        Assert.Contains("shown", result);
    }

    [Fact]
    public void StripHeavyHtml_VisibilityHiddenOnDiv_IsRemoved()
    {
        var html = "<div style=\"visibility:hidden\">invisible</div><p>visible</p>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("invisible", result);
        Assert.Contains("visible", result);
    }

    [Fact]
    public void StripHeavyHtml_VisibilityHiddenWithOtherProperties_IsRemoved()
    {
        var html = "<span style='color:red;visibility: hidden;font-size:12px'>gone</span>kept";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("gone", result);
        Assert.Contains("kept", result);
    }

    // ── Phase 3: FilterStyleAttribute unit tests ──────────────────────────────────

    [Fact]
    public void FilterStyleAttribute_PreservesDisplay()
    {
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("display: none");
        Assert.NotNull(result);
        Assert.Contains("display", result);
        Assert.Contains("none", result);
    }

    [Fact]
    public void FilterStyleAttribute_PreservesColorAndFont()
    {
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("color: #333; font-size: 14px; font-weight: bold");
        Assert.NotNull(result);
        Assert.Contains("color", result);
        Assert.Contains("font-size", result);
        Assert.Contains("font-weight", result);
    }

    [Fact]
    public void FilterStyleAttribute_PreservesTableCellWidth()
    {
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("width: 300px; background-color: #fff; padding: 8px");
        Assert.NotNull(result);
        Assert.Contains("width", result);
        Assert.Contains("background-color", result);
        Assert.Contains("padding", result);
    }

    [Fact]
    public void FilterStyleAttribute_StripsPositionProperty()
    {
        // position, top, left are all off the allowlist
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("position: fixed; top: 0; left: 0");
        Assert.Null(result);
    }

    [Fact]
    public void FilterStyleAttribute_StripsContentProperty()
    {
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("content: \"injected text\"");
        Assert.Null(result);
    }

    [Fact]
    public void FilterStyleAttribute_StripsUrlInBackgroundImage()
    {
        // background-image is not on the allowlist; url() token is also dangerous
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("background-image: url(http://tracker.example.com/pixel.gif)");
        Assert.Null(result);
    }

    [Fact]
    public void FilterStyleAttribute_StripsUrlEvenInAllowedProperty()
    {
        // background-color is on the allowlist, but url() in the value is blocked
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("background-color: url(http://evil.example/)");
        Assert.Null(result);
    }

    [Fact]
    public void FilterStyleAttribute_StripsExpressionValue()
    {
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("width: expression(document.body.scrollWidth)");
        Assert.Null(result);
    }

    [Fact]
    public void FilterStyleAttribute_ReturnsNullWhenAllDeclarationsStripped()
    {
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("position: absolute; z-index: 9999; cursor: pointer");
        Assert.Null(result);
    }

    [Fact]
    public void FilterStyleAttribute_HandlesMalformedDeclaration_NoColon()
    {
        // "display none" has no colon — skipped silently; color: red survives
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("display none; color: red");
        Assert.NotNull(result);
        Assert.Contains("color", result);
    }

    [Fact]
    public void FilterStyleAttribute_ReturnsNullForEmpty()
    {
        Assert.Null(MessageBodyHtmlBuilder.FilterStyleAttribute(""));
        Assert.Null(MessageBodyHtmlBuilder.FilterStyleAttribute("   "));
    }

    // ── Phase 3: StripHeavyHtml integration — allowlist preserved / blocked ───────

    [Fact]
    public void StripHeavyHtml_DisplayNoneOnTd_IsRemovedByPrePass()
    {
        // The pre-pass removes elements with inline display:none entirely (better than just
        // keeping them hidden via style, since the content is gone rather than merely hidden).
        var html = "<table><tr><td style=\"display:none\">Hidden cell</td><td>Shown</td></tr></table>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("Hidden cell", result);
        Assert.Contains("Shown", result);
    }

    [Fact]
    public void FilterStyleAttribute_PreservesDisplayNoneDeclaration()
    {
        // FilterStyleAttribute independently preserves display:none — verifies the allowlist
        // without the pre-pass interfering (pre-pass only affects full element removal).
        var result = MessageBodyHtmlBuilder.FilterStyleAttribute("display: none; color: blue");
        Assert.NotNull(result);
        Assert.Contains("display: none", result);
        Assert.Contains("color: blue", result);
    }

    [Fact]
    public void StripHeavyHtml_PreservesTableCellBackgroundColor()
    {
        var html = "<td style=\"background-color: #f5f5f5; padding: 12px\">content</td>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.Contains("background-color", result);
        Assert.Contains("padding", result);
    }

    [Fact]
    public void StripHeavyHtml_StripsBackgroundImageFromInlineStyle()
    {
        var html = "<div style=\"background-image: url(http://tracker.example.com/t.gif)\">text</div>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("background-image", result);
        Assert.DoesNotContain("tracker.example.com", result);
    }

    [Fact]
    public void StripHeavyHtml_StripsPositionFixedFromInlineStyle()
    {
        var html = "<div style=\"position: fixed; top: 0; left: 0; z-index: 9999\">overlay</div>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("position", result);
        Assert.DoesNotContain("z-index", result);
        Assert.Contains("overlay", result);
    }

    [Fact]
    public void StripHeavyHtml_RemovesStyleAttributeWhenAllDangerous()
    {
        var html = "<div style=\"position: absolute; cursor: crosshair\">text</div>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("style=", result);
        Assert.Contains("text", result);
    }

    // ── Phase 4: CleanStyleBlock unit tests ───────────────────────────────────────

    [Fact]
    public void CleanStyleBlock_RemovesAtImport()
    {
        var css = "@import url('https://fonts.googleapis.com/css?family=Roboto'); p { color: red; }";
        var result = MessageBodyHtmlBuilder.CleanStyleBlock(css);
        Assert.DoesNotContain("@import", result);
        Assert.DoesNotContain("fonts.googleapis.com", result);
        Assert.Contains("color: red", result);
    }

    [Fact]
    public void CleanStyleBlock_RemovesUrlFromPropertyValue()
    {
        var css = "body { background: url(http://evil.example/track.gif); color: #333; }";
        var result = MessageBodyHtmlBuilder.CleanStyleBlock(css);
        Assert.DoesNotContain("url(", result);
        Assert.DoesNotContain("evil.example", result);
        Assert.Contains("color: #333", result);
    }

    [Fact]
    public void CleanStyleBlock_RemovesBackgroundImage()
    {
        var css = ".header { background-image: url(header.jpg); font-size: 24px; }";
        var result = MessageBodyHtmlBuilder.CleanStyleBlock(css);
        Assert.DoesNotContain("background-image", result);
        Assert.Contains("font-size: 24px", result);
    }

    [Fact]
    public void CleanStyleBlock_PreservesClassDisplayNone()
    {
        // The key invariant: .preheader { display: none } must survive so class-hidden
        // elements remain invisible in the rendered reading pane.
        var css = ".preheader { display: none; max-height: 0; overflow: hidden; }";
        var result = MessageBodyHtmlBuilder.CleanStyleBlock(css);
        Assert.Contains("display: none", result);
        Assert.Contains("max-height: 0", result);
        Assert.Contains("overflow: hidden", result);
    }

    [Fact]
    public void CleanStyleBlock_PreservesMediaQuery()
    {
        var css = "@media only screen and (max-width: 600px) { .col { width: 100%; } }";
        var result = MessageBodyHtmlBuilder.CleanStyleBlock(css);
        Assert.Contains("@media", result);
        Assert.Contains("width: 100%", result);
    }

    [Fact]
    public void CleanStyleBlock_RemovesCssComment()
    {
        var css = "/* hack: background: url(evil) */ p { color: blue; }";
        var result = MessageBodyHtmlBuilder.CleanStyleBlock(css);
        Assert.DoesNotContain("hack:", result);
        Assert.Contains("color: blue", result);
    }

    // ── Phase 4: StripHeavyHtml integration — style block cleaning ───────────────

    [Fact]
    public void StripHeavyHtml_PreservesCleanedStyleBlock()
    {
        var html = "<style>.preheader { display: none; } p { color: #333; }</style><p>body</p>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.Contains("<style>", result);
        Assert.Contains("display: none", result);
        Assert.Contains("color: #333", result);
        Assert.Contains("body", result);
    }

    [Fact]
    public void StripHeavyHtml_RemovesDangerousContentFromStyleBlock()
    {
        // Dangerous content is removed; the remaining empty rule is harmless.
        var html = "<style>@import url('evil.css'); body { background: url(track.gif); }</style><p>text</p>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("evil.css", result);
        Assert.DoesNotContain("url(", result);
        Assert.DoesNotContain("@import", result);
        Assert.Contains("text", result);
    }

    [Fact]
    public void StripHeavyHtml_StyleBlockImportRemovedSafeRulesKept()
    {
        var html = "<style>@import 'fonts.css'; h1 { font-size: 24px; }</style><h1>Title</h1>";
        var result = MessageBodyHtmlBuilder.StripHeavyHtml(html);
        Assert.DoesNotContain("@import", result);
        Assert.Contains("font-size: 24px", result);
    }
}
