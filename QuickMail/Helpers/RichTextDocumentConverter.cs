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
/// our own serialization): p, h1–h6, ul/ol/li, blockquote, pre/code, hr,
/// table/tr/th/td, strong/em/u/del/code, a, img, br. Unknown elements degrade
/// to their text content.
///
/// Heading level, pre (with fence language), hr, and blockquote are tracked via
/// <c>Paragraph.Tag</c> ("H1".."H6", "PRE" / "PRE:lang", "HR", "BLOCKQUOTE");
/// table header cells and column alignment via <c>TableCell.Tag</c> ("TH"/"TD"
/// with an optional ":L"/":C"/":R" suffix); images via <c>Run.Tag</c>
/// ("IMG:src" with the alt text as the run text); and the author's original
/// link target via <c>Hyperlink.Tag</c> — all so content round-trips without
/// guessing from visual formatting.
/// </summary>
public static class RichTextDocumentConverter
{
    public const string TagH1 = "H1";
    public const string TagH2 = "H2";
    public const string TagH3 = "H3";
    public const string TagH4 = "H4";
    public const string TagH5 = "H5";
    public const string TagH6 = "H6";
    public const string TagPre = "PRE";
    public const string TagHr = "HR";
    public const string TagBlockquote = "BLOCKQUOTE";

    /// <summary>Run.Tag prefix marking an image placeholder; the rest is the src.</summary>
    public const string ImageTagPrefix = "IMG:";

    /// <summary>Run text shown for images whose alt text is empty (round-trips back to alt="").</summary>
    public const string ImageAltPlaceholder = "(image)";

    private static readonly FontFamily CodeFont = new("Consolas");

    /// <summary>Heading visual sizes relative to the editor's default 13px body text.</summary>
    public static double HeadingFontSize(int level) => level switch
    {
        1 => 24,
        2 => 19,
        3 => 16,
        4 => 14,
        5 => 13,
        _ => 12,
    };

    /// <summary>True when the tag marks a code block ("PRE" or "PRE:language").</summary>
    public static bool IsPreTag(string? tag) =>
        tag is not null && (tag == TagPre || tag.StartsWith(TagPre + ":", StringComparison.Ordinal));

    /// <summary>The fence language of a "PRE:language" tag, or null for a plain "PRE".</summary>
    public static string? PreLanguageOf(string? tag) =>
        tag is not null && tag.StartsWith(TagPre + ":", StringComparison.Ordinal)
            ? tag[(TagPre.Length + 1)..]
            : null;

    // ───────────────────────────── HTML → FlowDocument ─────────────────────────

    public static FlowDocument FromHtml(string html)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(4),
        };
        LoadInto(doc, html);
        return doc;
    }

    /// <summary>
    /// Replaces the content of an existing document with the parsed HTML.
    /// The editor must always load through this (never assign a new document to
    /// <c>RichTextBox.Document</c>): the control's UIA automation peer binds to
    /// the original document's text container at creation and never rebinds, so
    /// after a Document replacement screen readers permanently read the stale
    /// (empty) document instead of what is on screen.
    /// </summary>
    public static void LoadInto(FlowDocument doc, string html)
    {
        doc.Blocks.Clear();
        var root = HtmlNode.Parse(html ?? string.Empty);
        foreach (var block in BuildBlocks(root.Children))
            doc.Blocks.Add(block);
        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new Paragraph());
    }

    private static IEnumerable<Block> BuildBlocks(List<HtmlNode> nodes)
    {
        // Bare inline content between block elements is gathered into a paragraph.
        var pendingInlines = new List<Inline>();

        IEnumerable<Block> FlushPending()
        {
            // Stray whitespace between block elements is layout noise, not content.
            while (pendingInlines.Count > 0
                   && pendingInlines[^1] is Run r
                   && r.Tag is null
                   && string.IsNullOrWhiteSpace(r.Text))
                pendingInlines.RemoveAt(pendingInlines.Count - 1);
            if (pendingInlines.Count == 0) yield break;
            // Trailing whitespace at the end of a block does not render in HTML
            // (e.g. the newline before a nested list inside an <li>).
            if (pendingInlines[^1] is Run lastRun && lastRun.Tag is null)
                lastRun.Text = lastRun.Text.TrimEnd();
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
                    var level = node.Name[1] - '0';
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
                        inner.Tag = TagBlockquote;
                        if (inner is Paragraph qp)
                        {
                            qp.Margin = new Thickness(20, qp.Margin.Top, 0, qp.Margin.Bottom);
                            qp.Foreground = Brushes.DarkSlateGray;
                        }
                        yield return inner;
                    }
                    break;

                case "pre":
                    foreach (var b in FlushPending()) yield return b;
                    var fenceLang = FenceLanguageOf(node);
                    var pre = new Paragraph
                    {
                        Tag = string.IsNullOrEmpty(fenceLang) ? TagPre : TagPre + ":" + fenceLang,
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
                    foreach (var b in FlushPending()) yield return b;
                    var table = BuildTable(node);
                    if (table != null) yield return table;
                    break;

                case "html" or "body":
                    foreach (var b in FlushPending()) yield return b;
                    foreach (var inner in BuildBlocks(node.Children))
                        yield return inner;
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

    private static string? FenceLanguageOf(HtmlNode preNode)
    {
        var cls = preNode.Children.FirstOrDefault(c => c.Name == "code")?.Class;
        var token = cls?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.StartsWith("language-", StringComparison.OrdinalIgnoreCase));
        return token?["language-".Length..];
    }

    private static Table? BuildTable(HtmlNode node)
    {
        var rowNodes = node.Descendants("tr").ToList();
        if (rowNodes.Count == 0) return null;

        var table = new Table { CellSpacing = 0 };
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        int columns = 0;

        foreach (var rowNode in rowNodes)
        {
            var row = new TableRow();
            foreach (var cellNode in rowNode.Children.Where(c => c.Name is "td" or "th"))
            {
                bool isHeader = cellNode.Name == "th";
                var alignment = AlignmentOf(cellNode.Style);
                var cp = new Paragraph();
                AddInlines(cp.Inlines, cellNode.Children, new InlineStyle());
                var cell = new TableCell(cp)
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6, 2, 6, 2),
                    Tag = (isHeader ? "TH" : "TD") + alignment switch
                    {
                        TextAlignment.Left => ":L",
                        TextAlignment.Center => ":C",
                        TextAlignment.Right => ":R",
                        _ => string.Empty,
                    },
                };
                if (isHeader) cell.FontWeight = FontWeights.Bold;
                if (alignment.HasValue) cell.TextAlignment = alignment.Value;
                row.Cells.Add(cell);
            }
            columns = Math.Max(columns, row.Cells.Count);
            group.Rows.Add(row);
        }

        for (int c = 0; c < columns; c++)
            table.Columns.Add(new TableColumn());
        return table;
    }

    private static TextAlignment? AlignmentOf(string? style)
    {
        if (style is null || !style.Contains("text-align", StringComparison.OrdinalIgnoreCase))
            return null;
        if (style.Contains("center", StringComparison.OrdinalIgnoreCase)) return TextAlignment.Center;
        if (style.Contains("right", StringComparison.OrdinalIgnoreCase)) return TextAlignment.Right;
        if (style.Contains("left", StringComparison.OrdinalIgnoreCase)) return TextAlignment.Left;
        return null;
    }

    private static (bool IsHeader, string? Align) CellInfo(TableCell? cell)
    {
        if (cell?.Tag is not string tag) return (false, null);
        var parts = tag.Split(':');
        bool header = parts[0] == "TH";
        string? align = parts.Length > 1 ? parts[1] switch
        {
            "L" => "left",
            "C" => "center",
            "R" => "right",
            _ => null,
        } : null;
        return (header, align);
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
                    // HTML whitespace semantics: runs of whitespace collapse to one
                    // space; a space at the start of a line (start of the inline
                    // stream or right after a <br>) does not render.
                    var decoded = CollapseWhitespace(WebUtility.HtmlDecode(node.Text));
                    if (target.Count == 0 || target.LastOrDefault() is LineBreak)
                        decoded = decoded.TrimStart();
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
                    // Keep the author's exact href for round-tripping (NavigateUri
                    // normalizes), but never a script/data scheme.
                    if (href.Length > 0 && !IsDangerousHref(href))
                        link.Tag = href;
                    AddInlines(link.Inlines, node.Children, style);
                    if (link.Inlines.Count == 0 && href.Length > 0)
                        link.Inlines.Add(new Run(href));
                    target.Add(link);
                    break;

                case "img":
                    var alt = WebUtility.HtmlDecode(node.Alt ?? string.Empty);
                    var src = WebUtility.HtmlDecode(node.Src ?? string.Empty);
                    if (src.Length > 0 && !IsDangerousHref(src))
                    {
                        // The alt text is the editable run text; the src rides on Tag.
                        var imgRun = StyleRun(new Run(alt.Length > 0 ? alt : ImageAltPlaceholder), style);
                        imgRun.Tag = ImageTagPrefix + src;
                        target.Add(imgRun);
                    }
                    else if (alt.Length > 0)
                    {
                        target.Add(StyleRun(new Run($"[{alt}]"), style));
                    }
                    break;

                default:
                    AddInlines(target, node.Children, style);
                    break;
            }
        }
    }

    private static bool IsDangerousHref(string href)
    {
        var s = href.TrimStart();
        return s.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collapses every run of whitespace (including newlines) to a single space.</summary>
    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = true; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }
        if (pendingSpace) sb.Append(' ');
        return sb.ToString();
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
        EmitBlocksHtml(sb, doc.Blocks);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Emits a run of sibling blocks, merging consecutive blockquote paragraphs
    /// into a single &lt;blockquote&gt; so multi-paragraph quotes keep their
    /// structure instead of splitting into adjacent one-paragraph quotes.
    /// </summary>
    private static void EmitBlocksHtml(StringBuilder sb, IEnumerable<Block> blocks)
    {
        bool inQuote = false;
        foreach (var block in blocks)
        {
            bool isQuote = block.Tag as string == TagBlockquote;
            if (inQuote && !isQuote) { sb.Append("</blockquote>\n"); inQuote = false; }
            if (isQuote)
            {
                if (!inQuote) { sb.Append("<blockquote>\n"); inQuote = true; }
                if (block is Paragraph qp)
                {
                    sb.Append("<p>");
                    EmitInlinesHtml(sb, qp.Inlines, BaselineOf(qp));
                    sb.Append("</p>\n");
                }
                else
                {
                    EmitBlockHtml(sb, block);
                }
                continue;
            }
            EmitBlockHtml(sb, block);
        }
        if (inQuote) sb.Append("</blockquote>\n");
    }

    private static void EmitBlockHtml(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case Paragraph p:
                var tag = p.Tag as string;
                if (tag == TagHr) { sb.Append("<hr />\n"); return; }
                if (IsPreTag(tag))
                {
                    var lang = PreLanguageOf(tag);
                    sb.Append(lang is null
                        ? "<pre><code>"
                        : $"<pre><code class=\"language-{WebUtility.HtmlEncode(lang)}\">");
                    sb.Append(WebUtility.HtmlEncode(InlinesPlainText(p.Inlines)));
                    sb.Append("\n</code></pre>\n");
                    return;
                }
                var (open, close) = tag switch
                {
                    TagH1 => ("<h1>", "</h1>"),
                    TagH2 => ("<h2>", "</h2>"),
                    TagH3 => ("<h3>", "</h3>"),
                    TagH4 => ("<h4>", "</h4>"),
                    TagH5 => ("<h5>", "</h5>"),
                    TagH6 => ("<h6>", "</h6>"),
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
                        EmitBlocksHtml(sb, item.Blocks);
                    sb.Append("</li>\n");
                }
                sb.Append("</").Append(listTag).Append(">\n");
                break;

            case Table table:
                sb.Append("<table>\n");
                foreach (var rowGroup in table.RowGroups)
                {
                    foreach (var row in rowGroup.Rows)
                    {
                        sb.Append("<tr>\n");
                        foreach (var cell in row.Cells)
                        {
                            var (isHeader, align) = CellInfo(cell);
                            var cellTag = isHeader ? "th" : "td";
                            sb.Append('<').Append(cellTag);
                            if (isHeader) sb.Append(" scope=\"col\"");
                            if (align != null) sb.Append(" style=\"text-align: ").Append(align).Append(";\"");
                            sb.Append('>');
                            bool firstBlock = true;
                            foreach (var cb in cell.Blocks)
                            {
                                if (cb is not Paragraph cp) continue;
                                if (!firstBlock) sb.Append("<br />");
                                EmitInlinesHtml(sb, cp.Inlines, BaselineOf(cp));
                                firstBlock = false;
                            }
                            sb.Append("</").Append(cellTag).Append(">\n");
                        }
                        sb.Append("</tr>\n");
                    }
                }
                sb.Append("</table>\n");
                break;

            case Section section:
                EmitBlocksHtml(sb, section.Blocks);
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

                case Run imgRun when imgRun.Tag is string imgTag
                                     && imgTag.StartsWith(ImageTagPrefix, StringComparison.Ordinal):
                    var imgAlt = imgRun.Text == ImageAltPlaceholder ? string.Empty : imgRun.Text;
                    sb.Append("<img src=\"")
                      .Append(WebUtility.HtmlEncode(imgTag[ImageTagPrefix.Length..]))
                      .Append("\" alt=\"")
                      .Append(WebUtility.HtmlEncode(imgAlt))
                      .Append("\" />");
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
                    var href = link.Tag as string ?? link.NavigateUri?.ToString() ?? string.Empty;
                    if (href.Length == 0)
                    {
                        // No usable target — emit the text without a dead anchor.
                        EmitInlinesHtml(sb, link.Inlines, baseline);
                        break;
                    }
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
        EmitBlocksMarkdown(sb, doc.Blocks);
        return sb.ToString().Trim('\n');
    }

    /// <summary>
    /// Emits a run of sibling blocks, joining consecutive blockquote paragraphs
    /// with a "&gt;" continuation line so they re-parse as one quote.
    /// </summary>
    private static void EmitBlocksMarkdown(StringBuilder sb, IEnumerable<Block> blocks)
    {
        bool prevQuote = false;
        foreach (var block in blocks)
        {
            bool isQuote = block is Paragraph { Tag: TagBlockquote };
            if (isQuote)
            {
                if (prevQuote && sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n')
                {
                    // Replace the blank separator with a ">" continuation line.
                    sb.Length -= 1;
                    sb.Append(">\n");
                }
                var qp = (Paragraph)block;
                sb.Append("> ");
                EmitInlinesMarkdown(sb, qp.Inlines, BaselineOf(qp), "> ");
                sb.Append("\n\n");
                prevQuote = true;
                continue;
            }
            prevQuote = false;
            EmitBlockMarkdown(sb, block);
        }
    }

    private static void EmitBlockMarkdown(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case Paragraph p:
                var tag = p.Tag as string;
                if (tag == TagHr) { sb.Append("---\n\n"); return; }
                if (IsPreTag(tag))
                {
                    sb.Append("```").Append(PreLanguageOf(tag) ?? string.Empty).Append('\n')
                      .Append(InlinesPlainText(p.Inlines)).Append("\n```\n\n");
                    return;
                }
                var prefix = tag switch
                {
                    TagH1 => "# ",
                    TagH2 => "## ",
                    TagH3 => "### ",
                    TagH4 => "#### ",
                    TagH5 => "##### ",
                    TagH6 => "###### ",
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
                    var marker = ordered ? $"{n}. " : "- ";
                    n++;
                    sb.Append(marker);
                    // Continuation lines must be indented to the marker's width
                    // ("1. " needs three spaces) for nested content to re-parse.
                    var indent = new string(' ', marker.Length);
                    if (item.Blocks.Count == 1 && item.Blocks.FirstBlock is Paragraph ip)
                        EmitInlinesMarkdown(sb, ip.Inlines, BaselineOf(ip), indent);
                    else
                        sb.Append(ListItemMarkdown(item, indent));
                    sb.Append('\n');
                }
                sb.Append('\n');
                break;

            case Table table:
                EmitTableMarkdown(sb, table);
                break;

            case Section section:
                EmitBlocksMarkdown(sb, section.Blocks);
                break;
        }
    }

    /// <summary>
    /// Markdown for a multi-block list item. A nested list directly after a
    /// paragraph joins with a single newline (a blank line would make the list
    /// loose); other neighbors keep the blank-line separator.
    /// </summary>
    private static string ListItemMarkdown(ListItem item, string indent)
    {
        var parts = new List<(bool IsList, string Text)>();
        foreach (var block in item.Blocks)
        {
            var part = new StringBuilder();
            EmitBlocksMarkdown(part, [block]);
            parts.Add((block is List, part.ToString().Trim('\n')));
        }
        var joined = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                joined.Append(parts[i].IsList && !parts[i - 1].IsList ? "\n" : "\n\n");
            joined.Append(parts[i].Text);
        }
        return joined.ToString().Replace("\n", "\n" + indent);
    }

    private static void EmitTableMarkdown(StringBuilder sb, Table table)
    {
        var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        if (rows.Count == 0) return;

        var cellTexts = rows
            .Select(r => r.Cells.Select(CellMarkdown).ToList())
            .ToList();
        int columns = cellTexts.Max(r => r.Count);
        if (columns == 0) return;

        string RowLine(List<string> cells) =>
            "| " + string.Join(" | ", Enumerable.Range(0, columns)
                .Select(i => i < cells.Count ? cells[i] : string.Empty)) + " |";

        // First row is the header (pipe tables require one); its alignment
        // drives the delimiter row.
        sb.Append(RowLine(cellTexts[0])).Append('\n');
        var delimiters = Enumerable.Range(0, columns).Select(i =>
        {
            var cell = i < rows[0].Cells.Count ? rows[0].Cells[i] : null;
            return CellInfo(cell).Align switch
            {
                "center" => ":---:",
                "right" => "---:",
                "left" => ":---",
                _ => "---",
            };
        });
        sb.Append("| ").Append(string.Join(" | ", delimiters)).Append(" |\n");
        for (int r = 1; r < cellTexts.Count; r++)
            sb.Append(RowLine(cellTexts[r])).Append('\n');
        sb.Append('\n');
    }

    private static string CellMarkdown(TableCell cell)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var block in cell.Blocks)
        {
            if (block is not Paragraph p) continue;
            if (!first) sb.Append(' ');
            EmitInlinesMarkdown(sb, p.Inlines, BaselineOf(p), string.Empty);
            first = false;
        }
        return sb.ToString().Replace("\n", " ").Replace("|", "\\|").Trim();
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

                case Run mdImg when mdImg.Tag is string mdImgTag
                                    && mdImgTag.StartsWith(ImageTagPrefix, StringComparison.Ordinal):
                    var mdAlt = mdImg.Text == ImageAltPlaceholder ? string.Empty : mdImg.Text;
                    sb.Append("![").Append(mdAlt.Replace("]", @"\]")).Append("](")
                      .Append(mdImgTag[ImageTagPrefix.Length..]).Append(')');
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
                    var url = link.Tag as string ?? link.NavigateUri?.ToString() ?? string.Empty;
                    if (url.Length == 0)
                        sb.Append(inner);
                    else if (string.Equals(inner.ToString(), url, StringComparison.Ordinal))
                        sb.Append(url); // bare autolink stays bare
                    else
                    {
                        var escapedUrl = url.Replace(")", "%29");
                        sb.Append('[').Append(inner).Append("](").Append(escapedUrl).Append(')');
                    }
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
        public string? Src;
        public string? Class;
        public string? Style;
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
                node.Src = ExtractAttr(attrs, "src");
                node.Class = ExtractAttr(attrs, "class");
                node.Style = ExtractAttr(attrs, "style");
                stack.Peek().Children.Add(node);

                if (!selfClosed && !VoidTags.Contains(name))
                    stack.Push(node);
            }

            return root;
        }

        private static void AppendText(HtmlNode parent, string text)
        {
            // All text is kept; whitespace handling is contextual and happens
            // when blocks/inlines are built (whitespace between inline elements
            // is significant, whitespace between block elements is not).
            if (text.Length == 0) return;
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
