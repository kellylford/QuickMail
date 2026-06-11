using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// Converts between a RichTextBox <see cref="FlowDocument"/> and the HTML /
/// Markdown / plain-text body representations used by compose. The HTML side
/// is the bounded subset the compose pipeline produces (Markdig output and
/// our own serialization): p, h1–h3, ul/ol/li, blockquote, pre/code, hr,
/// strong/em/u/del/code, a, br. Unknown elements degrade to their text content.
///
/// Heading level, pre, hr, and blockquote are tracked via <c>Paragraph.Tag</c>
/// ("H1".."H3", "PRE", "HR", "BLOCKQUOTE") so they round-trip without
/// guessing from font sizes.
/// </summary>
public static class RichTextDocumentConverter
{
    public const string TagH1 = "H1";
    public const string TagH2 = "H2";
    public const string TagH3 = "H3";
    public const string TagPre = "PRE";
    public const string TagHr = "HR";
    public const string TagBlockquote = "BLOCKQUOTE";

    private static readonly FontFamily CodeFont = new("Consolas");

    /// <summary>Heading visual sizes relative to the editor's default 13px body text.</summary>
    public static double HeadingFontSize(int level) => level switch
    {
        1 => 24,
        2 => 19,
        _ => 16,
    };

    // ───────────────────────────── HTML → FlowDocument ─────────────────────────

    public static FlowDocument FromHtml(string html)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(4),
        };
        var root = HtmlNode.Parse(html ?? string.Empty);
        foreach (var block in BuildBlocks(root.Children))
            doc.Blocks.Add(block);
        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new Paragraph());
        return doc;
    }

    private static IEnumerable<Block> BuildBlocks(List<HtmlNode> nodes)
    {
        // Bare inline content between block elements is gathered into a paragraph.
        var pendingInlines = new List<Inline>();

        IEnumerable<Block> FlushPending()
        {
            if (pendingInlines.Count == 0) yield break;
            var p = new Paragraph();
            foreach (var i in pendingInlines) p.Inlines.Add(i);
            pendingInlines.Clear();
            yield return p;
        }

        foreach (var node in nodes)
        {
            switch (node.Name)
            {
                case "p" or "div":
                    foreach (var b in FlushPending()) yield return b;
                    var para = new Paragraph();
                    AddInlines(para.Inlines, node.Children, new InlineStyle());
                    yield return para;
                    break;

                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    foreach (var b in FlushPending()) yield return b;
                    var level = Math.Min(node.Name[1] - '0', 3);
                    var heading = new Paragraph
                    {
                        Tag = "H" + level,
                        FontSize = HeadingFontSize(level),
                        FontWeight = FontWeights.Bold,
                    };
                    AddInlines(heading.Inlines, node.Children, new InlineStyle());
                    yield return heading;
                    break;

                case "ul" or "ol":
                    foreach (var b in FlushPending()) yield return b;
                    var list = new List
                    {
                        MarkerStyle = node.Name == "ol"
                            ? System.Windows.TextMarkerStyle.Decimal
                            : System.Windows.TextMarkerStyle.Disc,
                    };
                    foreach (var li in node.Children.Where(c => c.Name == "li"))
                    {
                        var item = new ListItem();
                        foreach (var inner in BuildBlocks(li.Children))
                            item.Blocks.Add(inner);
                        if (item.Blocks.Count == 0)
                            item.Blocks.Add(new Paragraph());
                        list.ListItems.Add(item);
                    }
                    if (list.ListItems.Count > 0)
                        yield return list;
                    break;

                case "blockquote":
                    foreach (var b in FlushPending()) yield return b;
                    foreach (var inner in BuildBlocks(node.Children))
                    {
                        if (inner is Paragraph qp)
                        {
                            qp.Tag = TagBlockquote;
                            qp.Margin = new Thickness(20, qp.Margin.Top, 0, qp.Margin.Bottom);
                            qp.Foreground = Brushes.DarkSlateGray;
                        }
                        yield return inner;
                    }
                    break;

                case "pre":
                    foreach (var b in FlushPending()) yield return b;
                    var pre = new Paragraph
                    {
                        Tag = TagPre,
                        FontFamily = CodeFont,
                    };
                    // <pre> content is whitespace-significant; lines become LineBreaks.
                    var text = node.InnerText().TrimEnd('\n');
                    var lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) pre.Inlines.Add(new LineBreak());
                        pre.Inlines.Add(new Run(lines[i]));
                    }
                    yield return pre;
                    break;

                case "hr":
                    foreach (var b in FlushPending()) yield return b;
                    yield return new Paragraph(new Run("──────────")) { Tag = TagHr };
                    break;

                case "table":
                    // Tables degrade to one paragraph per row, cells joined by tabs.
                    foreach (var b in FlushPending()) yield return b;
                    foreach (var row in node.Descendants("tr"))
                    {
                        var cells = row.Children
                            .Where(c => c.Name is "td" or "th")
                            .Select(c => c.InnerText().Trim());
                        yield return new Paragraph(new Run(string.Join("\t", cells)));
                    }
                    break;

                case "script" or "style" or "head":
                    break; // never render

                default:
                    // Text or inline element at block level — accumulate.
                    AddInlines(pendingInlines, [node], new InlineStyle());
                    break;
            }
        }

        foreach (var b in FlushPending()) yield return b;
    }

    private readonly record struct InlineStyle(
        bool Bold = false, bool Italic = false, bool Underline = false,
        bool Strike = false, bool Code = false);

    private static void AddInlines(ICollection<Inline> target, List<HtmlNode> nodes, InlineStyle style)
    {
        foreach (var node in nodes)
        {
            switch (node.Name)
            {
                case "":
                    var decoded = WebUtility.HtmlDecode(node.Text).Replace('\n', ' ');
                    if (decoded.Length > 0)
                        target.Add(StyleRun(new Run(decoded), style));
                    break;

                case "br":
                    target.Add(new LineBreak());
                    break;

                case "strong" or "b":
                    AddInlines(target, node.Children, style with { Bold = true });
                    break;

                case "em" or "i":
                    AddInlines(target, node.Children, style with { Italic = true });
                    break;

                case "u" or "ins":
                    AddInlines(target, node.Children, style with { Underline = true });
                    break;

                case "del" or "s" or "strike":
                    AddInlines(target, node.Children, style with { Strike = true });
                    break;

                case "code":
                    AddInlines(target, node.Children, style with { Code = true });
                    break;

                case "a":
                    var link = new Hyperlink();
                    var href = WebUtility.HtmlDecode(node.Href ?? string.Empty);
                    if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
                        && uri.Scheme is "http" or "https" or "mailto")
                        link.NavigateUri = uri;
                    AddInlines(link.Inlines, node.Children, style);
                    if (link.Inlines.Count == 0 && href.Length > 0)
                        link.Inlines.Add(new Run(href));
                    target.Add(link);
                    break;

                case "img":
                    var alt = WebUtility.HtmlDecode(node.Alt ?? string.Empty);
                    if (alt.Length > 0)
                        target.Add(StyleRun(new Run($"[{alt}]"), style));
                    break;

                default:
                    AddInlines(target, node.Children, style);
                    break;
            }
        }
    }

    private static Run StyleRun(Run run, InlineStyle style)
    {
        if (style.Bold) run.FontWeight = FontWeights.Bold;
        if (style.Italic) run.FontStyle = FontStyles.Italic;
        if (style.Code) run.FontFamily = CodeFont;
        var decorations = new TextDecorationCollection();
        if (style.Underline) decorations.Add(TextDecorations.Underline);
        if (style.Strike) decorations.Add(TextDecorations.Strikethrough);
        if (decorations.Count > 0) run.TextDecorations = decorations;
        return run;
    }

    // ───────────────────────────── FlowDocument → HTML ─────────────────────────

    public static string ToHtml(FlowDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var block in doc.Blocks)
            EmitBlockHtml(sb, block);
        return sb.ToString().TrimEnd('\n');
    }

    private static void EmitBlockHtml(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case Paragraph p:
                var tag = p.Tag as string;
                if (tag == TagHr) { sb.Append("<hr />\n"); return; }
                if (tag == TagPre)
                {
                    sb.Append("<pre><code>");
                    sb.Append(WebUtility.HtmlEncode(InlinesPlainText(p.Inlines)));
                    sb.Append("</code></pre>\n");
                    return;
                }
                var (open, close) = tag switch
                {
                    TagH1 => ("<h1>", "</h1>"),
                    TagH2 => ("<h2>", "</h2>"),
                    TagH3 => ("<h3>", "</h3>"),
                    TagBlockquote => ("<blockquote><p>", "</p></blockquote>"),
                    _ => ("<p>", "</p>"),
                };
                sb.Append(open);
                EmitInlinesHtml(sb, p.Inlines, BaselineOf(p));
                sb.Append(close).Append('\n');
                break;

            case List list:
                var listTag = list.MarkerStyle == System.Windows.TextMarkerStyle.Decimal ? "ol" : "ul";
                sb.Append('<').Append(listTag).Append(">\n");
                foreach (var item in list.ListItems)
                {
                    sb.Append("<li>");
                    // Single-paragraph items emit inline content; anything richer recurses.
                    if (item.Blocks.Count == 1 && item.Blocks.FirstBlock is Paragraph ip)
                        EmitInlinesHtml(sb, ip.Inlines, BaselineOf(ip));
                    else
                        foreach (var inner in item.Blocks)
                            EmitBlockHtml(sb, inner);
                    sb.Append("</li>\n");
                }
                sb.Append("</").Append(listTag).Append(">\n");
                break;

            case Section section:
                foreach (var inner in section.Blocks)
                    EmitBlockHtml(sb, inner);
                break;
        }
    }

    /// <summary>Paragraph-level formatting that inherited runs should not re-emit (e.g. bold headings).</summary>
    private static InlineStyle BaselineOf(Paragraph p) => new(
        Bold: p.FontWeight >= FontWeights.Bold,
        Italic: p.FontStyle == FontStyles.Italic);

    private static void EmitInlinesHtml(StringBuilder sb, InlineCollection inlines, InlineStyle baseline)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LineBreak:
                    sb.Append("<br />\n");
                    break;

                case Run run:
                    var style = EffectiveStyle(run, baseline);
                    var openTags = new List<string>();
                    if (style.Bold) openTags.Add("strong");
                    if (style.Italic) openTags.Add("em");
                    if (style.Underline) openTags.Add("u");
                    if (style.Strike) openTags.Add("del");
                    if (style.Code) openTags.Add("code");
                    foreach (var t in openTags) sb.Append('<').Append(t).Append('>');
                    sb.Append(WebUtility.HtmlEncode(run.Text));
                    for (int i = openTags.Count - 1; i >= 0; i--)
                        sb.Append("</").Append(openTags[i]).Append('>');
                    break;

                case Hyperlink link:
                    var href = link.NavigateUri?.ToString() ?? string.Empty;
                    sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">");
                    EmitInlinesHtml(sb, link.Inlines, baseline);
                    sb.Append("</a>");
                    break;

                case Span span:
                    EmitInlinesHtml(sb, span.Inlines, baseline);
                    break;
            }
        }
    }

    private static InlineStyle EffectiveStyle(Run run, InlineStyle baseline)
    {
        var decorations = run.TextDecorations;
        bool Has(TextDecorationCollection reference) =>
            decorations != null && decorations.Any(d => reference.Any(r => r.Location == d.Location));
        return new InlineStyle(
            Bold: run.FontWeight >= FontWeights.Bold && !baseline.Bold,
            Italic: run.FontStyle == FontStyles.Italic && !baseline.Italic,
            Underline: Has(TextDecorations.Underline),
            Strike: Has(TextDecorations.Strikethrough),
            Code: run.FontFamily?.Source?.Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true
                  || run.FontFamily?.Source?.Contains("Cascadia", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ─────────────────────────── FlowDocument → Markdown ───────────────────────

    public static string ToMarkdown(FlowDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var block in doc.Blocks)
            EmitBlockMarkdown(sb, block);
        return sb.ToString().Trim('\n');
    }

    private static void EmitBlockMarkdown(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case Paragraph p:
                var tag = p.Tag as string;
                if (tag == TagHr) { sb.Append("---\n\n"); return; }
                if (tag == TagPre)
                {
                    sb.Append("```\n").Append(InlinesPlainText(p.Inlines)).Append("\n```\n\n");
                    return;
                }
                var prefix = tag switch
                {
                    TagH1 => "# ",
                    TagH2 => "## ",
                    TagH3 => "### ",
                    TagBlockquote => "> ",
                    _ => string.Empty,
                };
                sb.Append(prefix);
                EmitInlinesMarkdown(sb, p.Inlines, BaselineOf(p), prefix == "> " ? "> " : string.Empty);
                sb.Append("\n\n");
                break;

            case List list:
                bool ordered = list.MarkerStyle == System.Windows.TextMarkerStyle.Decimal;
                int n = 1;
                foreach (var item in list.ListItems)
                {
                    sb.Append(ordered ? $"{n}. " : "- ");
                    n++;
                    if (item.Blocks.Count == 1 && item.Blocks.FirstBlock is Paragraph ip)
                        EmitInlinesMarkdown(sb, ip.Inlines, BaselineOf(ip), "  ");
                    else
                    {
                        var inner = new StringBuilder();
                        foreach (var b in item.Blocks) EmitBlockMarkdown(inner, b);
                        sb.Append(inner.ToString().Trim('\n').Replace("\n", "\n  "));
                    }
                    sb.Append('\n');
                }
                sb.Append('\n');
                break;

            case Section section:
                foreach (var inner in section.Blocks)
                    EmitBlockMarkdown(sb, inner);
                break;
        }
    }

    private static void EmitInlinesMarkdown(StringBuilder sb, InlineCollection inlines, InlineStyle baseline, string continuationPrefix)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LineBreak:
                    sb.Append('\n').Append(continuationPrefix);
                    break;

                case Run run:
                    var style = EffectiveStyle(run, baseline);
                    string marker = style.Code ? "`"
                        : (style.Bold, style.Italic) switch
                        {
                            (true, true) => "***",
                            (true, false) => "**",
                            (false, true) => "*",
                            _ => string.Empty,
                        };
                    var strike = style.Strike ? "~~" : string.Empty;
                    // Underline has no Markdown form — the text passes through unstyled.
                    var text = run.Text;
                    if (marker.Length > 0 || strike.Length > 0)
                    {
                        // Emphasis markers don't survive leading/trailing spaces.
                        var trimmed = text.Trim();
                        if (trimmed.Length == 0) { sb.Append(text); break; }
                        var leading = text[..(text.Length - text.TrimStart().Length)];
                        var trailing = text[(leading.Length + trimmed.Length)..];
                        sb.Append(leading).Append(strike).Append(marker)
                          .Append(trimmed)
                          .Append(marker).Append(strike).Append(trailing);
                    }
                    else
                    {
                        sb.Append(text);
                    }
                    break;

                case Hyperlink link:
                    var inner = new StringBuilder();
                    EmitInlinesMarkdown(inner, link.Inlines, baseline, continuationPrefix);
                    var url = link.NavigateUri?.ToString() ?? string.Empty;
                    if (url.Length > 0)
                        sb.Append('[').Append(inner).Append("](").Append(url).Append(')');
                    else
                        sb.Append(inner);
                    break;

                case Span span:
                    EmitInlinesMarkdown(sb, span.Inlines, baseline, continuationPrefix);
                    break;
            }
        }
    }

    // ─────────────────────────── Plain text & snapshot ─────────────────────────

    public static string ToPlainText(FlowDocument doc) => HtmlStripper.ToPlainText(ToHtml(doc));

    /// <summary>All three representations in one pass — handed to the ViewModel via RichBodyProvider.</summary>
    public static RichBodySnapshot Snapshot(FlowDocument doc) =>
        new(ToHtml(doc), ToMarkdown(doc), ToPlainText(doc));

    private static string InlinesPlainText(InlineCollection inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LineBreak: sb.Append('\n'); break;
                case Run run: sb.Append(run.Text); break;
                case Span span: sb.Append(InlinesPlainText(span.Inlines)); break;
            }
        }
        return sb.ToString();
    }

    // ───────────────────────────── Minimal HTML parser ─────────────────────────

    /// <summary>
    /// Tiny stack-based parser for the bounded HTML subset compose works with.
    /// Name is "" for text nodes. Comments and doctype are skipped; mismatched
    /// closing tags pop to the nearest matching ancestor or are ignored.
    /// </summary>
    private sealed class HtmlNode
    {
        public string Name = string.Empty;
        public string Text = string.Empty;
        public string? Href;
        public string? Alt;
        public List<HtmlNode> Children = [];

        private static readonly HashSet<string> VoidTags =
            ["br", "hr", "img", "meta", "link", "input", "area", "base", "col", "embed", "source", "track", "wbr"];

        public static HtmlNode Parse(string html)
        {
            var root = new HtmlNode { Name = "root" };
            var stack = new Stack<HtmlNode>();
            stack.Push(root);
            int i = 0;

            while (i < html.Length)
            {
                int lt = html.IndexOf('<', i);
                if (lt < 0)
                {
                    AppendText(stack.Peek(), html[i..]);
                    break;
                }
                if (lt > i)
                    AppendText(stack.Peek(), html[i..lt]);

                // Comments and doctype.
                if (lt + 3 < html.Length && html[lt + 1] == '!')
                {
                    int endC = html.IndexOf('>', lt);
                    i = endC < 0 ? html.Length : endC + 1;
                    continue;
                }

                int gt = html.IndexOf('>', lt + 1);
                if (gt < 0) { AppendText(stack.Peek(), html[lt..]); break; }

                var raw = html[(lt + 1)..gt].Trim();
                i = gt + 1;
                if (raw.Length == 0) continue;

                bool closing = raw.StartsWith('/');
                bool selfClosed = raw.EndsWith('/');
                var body = raw.Trim('/').Trim();
                int nameEnd = 0;
                while (nameEnd < body.Length && (char.IsLetterOrDigit(body[nameEnd]))) nameEnd++;
                var name = body[..nameEnd].ToLowerInvariant();
                if (name.Length == 0) continue;

                if (closing)
                {
                    // Pop to the matching open element if one exists.
                    if (stack.Any(n => n.Name == name))
                        while (stack.Count > 1 && stack.Pop().Name != name) { }
                    continue;
                }

                // Raw-content elements: capture everything to the closing tag as text and skip it.
                if (name is "script" or "style")
                {
                    int close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                    if (close < 0) break;
                    int closeGt = html.IndexOf('>', close);
                    i = closeGt < 0 ? html.Length : closeGt + 1;
                    continue;
                }

                var node = new HtmlNode { Name = name };
                var attrs = body[nameEnd..];
                node.Href = ExtractAttr(attrs, "href");
                node.Alt = ExtractAttr(attrs, "alt");
                stack.Peek().Children.Add(node);

                if (!selfClosed && !VoidTags.Contains(name))
                    stack.Push(node);
            }

            return root;
        }

        private static void AppendText(HtmlNode parent, string text)
        {
            if (text.Length == 0) return;
            // Whitespace-only text between blocks is layout noise except inside <pre>.
            if (string.IsNullOrWhiteSpace(text) && parent.Name != "pre" && parent.Name != "code")
                return;
            parent.Children.Add(new HtmlNode { Text = text });
        }

        private static string? ExtractAttr(string attrs, string attribute)
        {
            var idx = attrs.IndexOf(attribute + "=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            if (idx > 0 && (char.IsLetterOrDigit(attrs[idx - 1]) || attrs[idx - 1] == '-')) return null;
            int v = idx + attribute.Length + 1;
            if (v >= attrs.Length) return null;
            char quote = attrs[v];
            if (quote is '"' or '\'')
            {
                int endQuote = attrs.IndexOf(quote, v + 1);
                return endQuote < 0 ? attrs[(v + 1)..] : attrs[(v + 1)..endQuote];
            }
            int endSpace = attrs.IndexOf(' ', v);
            return endSpace < 0 ? attrs[v..] : attrs[v..endSpace];
        }

        public string InnerText()
        {
            if (Name.Length == 0) return WebUtility.HtmlDecode(Text);
            var sb = new StringBuilder();
            foreach (var child in Children)
            {
                if (child.Name == "br") { sb.Append('\n'); continue; }
                sb.Append(child.InnerText());
            }
            return sb.ToString();
        }

        public IEnumerable<HtmlNode> Descendants(string name)
        {
            foreach (var child in Children)
            {
                if (child.Name == name) yield return child;
                foreach (var d in child.Descendants(name)) yield return d;
            }
        }
    }
}
