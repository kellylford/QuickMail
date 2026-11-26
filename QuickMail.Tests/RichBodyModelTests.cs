using System.Linq;
using QuickMail.Helpers;

namespace QuickMail.Tests;

/// <summary>
/// The flat form a message body takes on its way to and from the hosted editor (#1099, HTML
/// mode). Whatever HTML QuickMail can hold must come back out of it unchanged, since what is
/// sent is written from the document rebuilt from the editor.
/// </summary>
public class RichBodyModelTests
{
    private static string RoundTrip(string html)
    {
        var doc = RichTextDocumentConverter.FromHtml(html);
        var rebuilt = RichBody.ToDocument(RichBody.Flatten(doc));
        return RichTextDocumentConverter.ToHtml(rebuilt);
    }

    private static string Direct(string html) =>
        RichTextDocumentConverter.ToHtml(RichTextDocumentConverter.FromHtml(html));

    [StaTheory]
    [InlineData("<p>Plain text.</p>")]
    [InlineData("<p>One <strong>bold</strong>, one <em>italic</em>, one <u>underlined</u>, one <del>struck</del>, one <code>code</code>.</p>")]
    [InlineData("<p><strong><em>Both at once</em></strong> and plain.</p>")]
    [InlineData("<h1>Heading one</h1><h2>Two</h2><h3>Three</h3><h4>Four</h4><h5>Five</h5><h6>Six</h6><p>Body.</p>")]
    [InlineData("<h2>A heading with <em>italic</em> in it</h2>")]
    [InlineData("<p>A <a href=\"https://example.com/a?b=1&amp;c=2\">link with <strong>bold</strong> inside</a> and after.</p>")]
    [InlineData("<p>Mail <a href=\"mailto:someone@example.com\">someone</a>.</p>")]
    [InlineData("<p>Line one<br />line two<br />line three</p>")]
    [InlineData("<ul><li>First</li><li>Second</li></ul>")]
    [InlineData("<ol><li>One</li><li>Two</li><li>Three</li></ol>")]
    [InlineData("<ul><li>Top<ul><li>Nested one</li><li>Nested two</li></ul></li><li>Top again</li></ul>")]
    [InlineData("<ul><li>Bullet</li></ul><ol><li>Number</li></ol>")]
    [InlineData("<ol><li>One<ul><li>Bullet inside a numbered item</li></ul></li><li>Two</li></ol>")]
    [InlineData("<blockquote><p>Quoted.</p></blockquote>")]
    [InlineData("<blockquote><p>Outer.</p><blockquote><p>Inner.</p></blockquote><p>Outer again.</p></blockquote>")]
    [InlineData("<blockquote><h2>A heading in a quote</h2><ul><li>A list in a quote</li></ul></blockquote><p>After.</p>")]
    [InlineData("<pre><code>var x = 1;\nvar y = 2;\n</code></pre>")]
    [InlineData("<pre><code class=\"language-csharp\">int n;\n</code></pre>")]
    [InlineData("<p>Before</p><hr /><p>After</p>")]
    [InlineData("<table><tr><th>Name</th><th>Size</th></tr><tr><td>One</td><td style=\"text-align: right;\">12</td></tr></table>")]
    [InlineData("<table><tr><td>A <strong>bold</strong> cell</td><td><a href=\"https://example.com/\">link</a></td></tr></table><p>After the table.</p>")]
    [InlineData("<blockquote><table><tr><td>Quoted cell</td></tr></table></blockquote>")]
    [InlineData("<p>A picture <img src=\"cid:abc\" alt=\"A red square\" width=\"48\" height=\"32\" /> in a line.</p>")]
    [InlineData("<p><img src=\"cid:decorative\" alt=\"\" /></p>")]
    [InlineData("<p><img src=\"cid:undescribed\" /></p>")]
    [InlineData("<p>Reply text.</p><p>On Thursday, Ava wrote:</p><blockquote><p>Do you have five minutes?</p><p>Thanks,<br />Ava</p></blockquote>")]
    public void Html_ComesBackUnchanged_ThroughTheFlatForm(string html) =>
        Assert.Equal(Direct(html), RoundTrip(html));

    [StaFact]
    public void Flatten_WritesTheStructureOnEachParagraph()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            "<h2>Title</h2>" +
            "<blockquote><p>Quoted</p><ul><li>Item</li><li>Second<ol><li>Deeper</li></ol></li></ul></blockquote>" +
            "<table><tr><th>Head</th></tr><tr><td>Cell</td></tr></table>" +
            "<pre><code class=\"language-js\">a\nb\n</code></pre><hr />");

        var flat = RichBody.Flatten(doc);

        Assert.Equal(new[] { "Title", "Quoted", "Item", "Second", "Deeper", "Head", "Cell", "a\vb", RichBody.RuleText },
            flat.Select(p => p.Text).ToArray());
        Assert.Equal(2, flat[0].Heading);
        Assert.Equal(new[] { 0, 1, 1, 1, 1, 0, 0, 0, 0 }, flat.Select(p => p.QuoteDepth).ToArray());
        Assert.Equal(
            new[] { RichListKind.None, RichListKind.None, RichListKind.Bullet, RichListKind.Bullet, RichListKind.Numbered,
                    RichListKind.None, RichListKind.None, RichListKind.None, RichListKind.None },
            flat.Select(p => p.List).ToArray());
        Assert.Equal(1, flat[4].ListLevel);
        Assert.True(flat[5].IsCell && flat[5].HeaderCell && flat[5].Row == 0);
        Assert.True(flat[6].IsCell && !flat[6].HeaderCell && flat[6].Row == 1);
        Assert.Equal(flat[5].Table, flat[6].Table);
        Assert.True(flat[7].Pre);
        Assert.Equal("js", flat[7].PreLanguage);
        Assert.True(flat[8].Rule);
    }

    [StaFact]
    public void Flatten_KeepsEachRunsFormatting_AndALinksAddress()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            "<p>plain <strong>bold</strong> <a href=\"https://example.com/\">a <em>link</em></a></p>");

        var runs = RichBody.Flatten(doc).Single().Runs;

        Assert.Equal(new[] { "plain ", "bold", " ", "a ", "link" }, runs.Select(r => r.Text).ToArray());
        Assert.True(runs[1].Bold);
        Assert.Null(runs[2].Link);
        Assert.Equal("https://example.com/", runs[3].Link);
        Assert.True(runs[4].Italic);
        Assert.Equal("https://example.com/", runs[4].Link);
    }

    [StaFact]
    public void Flatten_APicture_IsARunCarryingItsDetails()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p>See <img src=\"cid:abc\" alt=\"A chart\" width=\"10\" height=\"20\" /></p>");

        var runs = RichBody.Flatten(doc).Single().Runs;

        var picture = Assert.Single(runs, r => r.Image is not null);
        Assert.Equal(RichBody.ObjectChar, picture.Text);
        Assert.Equal(new ComposeImage("cid:abc", "A chart", 10, 20), picture.Image);
    }

    [Theory]
    [InlineData("no change", "no change")]
    [InlineData("a\rb\nc", "a b c")]
    [InlineData("cell\u0007mark", "cell mark")]
    [InlineData("row￹start￻end", "row start end")]
    [InlineData("object￼here", "object here")]
    public void Clean_TakesOutCharactersTheEditorGivesAMeaning(string text, string expected) =>
        Assert.Equal(expected, RichBody.Clean(text));

    [StaFact]
    public void ToDocument_OnNothing_IsOneEmptyParagraph()
    {
        var doc = RichBody.ToDocument(System.Array.Empty<RichParagraph>());
        Assert.Single(doc.Blocks);
        Assert.Equal(string.Empty, RichTextDocumentConverter.ToHtml(doc).Replace("<p></p>", string.Empty));
    }
}
