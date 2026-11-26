using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuickMail.Helpers;

internal enum RichListKind { None, Bullet, Numbered }

/// <summary>
/// A stretch of text with one set of character formatting. A line break inside a paragraph is a
/// run whose text is <see cref="RichBody.LineBreak"/>; a picture is a run whose
/// <see cref="Image"/> is set (its text is <see cref="RichBody.ObjectChar"/>).
/// </summary>
internal sealed record RichRun(
    string Text, bool Bold = false, bool Italic = false, bool Underline = false,
    bool Strike = false, bool Code = false, string? Link = null, ComposeImage? Image = null)
{
    public bool IsLineBreak => Image is null && Text == RichBody.LineBreak;

    public bool SameFormatAs(RichRun other) =>
        Bold == other.Bold && Italic == other.Italic && Underline == other.Underline
        && Strike == other.Strike && Code == other.Code
        && string.Equals(Link, other.Link, StringComparison.Ordinal);
}

/// <summary>
/// One paragraph of a message body, with everything that makes it more than text written on it:
/// heading level, code block, rule, list and level, how many quotes it is inside, and, for a table
/// cell, which table and row.
/// </summary>
internal sealed class RichParagraph
{
    public List<RichRun> Runs { get; } = new();

    /// <summary>1 to 6, or 0 for no heading.</summary>
    public int Heading { get; set; }

    public bool Pre { get; set; }
    public string? PreLanguage { get; set; }
    public bool Rule { get; set; }

    public RichListKind List { get; set; }

    /// <summary>0 for a top-level list item.</summary>
    public int ListLevel { get; set; }

    public int QuoteDepth { get; set; }

    /// <summary>The table this cell is in (tables are numbered in document order), or -1.</summary>
    public int Table { get; set; } = -1;

    public int Row { get; set; }
    public bool HeaderCell { get; set; }

    /// <summary>"L", "C" or "R" for a cell with an alignment, else null.</summary>
    public string? CellAlign { get; set; }

    public bool IsCell => Table >= 0;

    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// A message body as a flat list of paragraphs: the form in which it passes between QuickMail's
/// document model (a <see cref="FlowDocument"/>, which <see cref="RichTextDocumentConverter"/>
/// turns into HTML, Markdown and plain text) and the hosted WinUI editor (#1099), which is a flat
/// run of paragraphs itself. Structure that nests in the document (a quote holding a list holding
/// paragraphs) is written on each paragraph, and rebuilt from that.
///
/// <para>Nothing here needs a window, so the two conversions are tested on their own: HTML that
/// goes in must come back out the same.</para>
/// </summary>
internal static class RichBody
{
    /// <summary>A line break inside a paragraph: RichEdit's soft break character.</summary>
    public const string LineBreak = "\v";

    /// <summary>Where a picture sits in a paragraph's text.</summary>
    public static readonly string ObjectChar = ((char)0xFFFC).ToString();

    public const string RuleText = "──────────";

    // ─────────────────────────── FlowDocument → paragraphs ───────────────────

    public static List<RichParagraph> Flatten(FlowDocument doc)
    {
        var output = new List<RichParagraph>();
        int tables = 0;
        FlattenBlocks(doc.Blocks, quote: 0, RichListKind.None, listLevel: -1, output, ref tables);
        return output;
    }

    private static void FlattenBlocks(IEnumerable<Block> blocks, int quote, RichListKind list, int listLevel,
        List<RichParagraph> output, ref int tables)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    var paragraph = FromParagraph(p);
                    paragraph.QuoteDepth = quote;
                    if (listLevel >= 0)
                    {
                        paragraph.List = list;
                        paragraph.ListLevel = listLevel;
                    }
                    output.Add(paragraph);
                    break;

                case List l:
                    var kind = l.MarkerStyle == TextMarkerStyle.Decimal ? RichListKind.Numbered : RichListKind.Bullet;
                    foreach (var item in l.ListItems)
                        FlattenBlocks(item.Blocks, quote, kind, listLevel + 1, output, ref tables);
                    break;

                case Section section when RichTextDocumentConverter.IsQuote(section):
                    // A quote starts its own structure: the editor has no list item that holds a quote.
                    FlattenBlocks(section.Blocks, quote + 1, RichListKind.None, -1, output, ref tables);
                    break;

                case Section section:
                    FlattenBlocks(section.Blocks, quote, list, listLevel, output, ref tables);
                    break;

                case Table table:
                    int id = tables++;
                    int rowIndex = 0;
                    foreach (var row in table.RowGroups.SelectMany(g => g.Rows))
                    {
                        foreach (var cell in row.Cells)
                        {
                            var cellParagraph = new RichParagraph { Table = id, Row = rowIndex, QuoteDepth = quote };
                            var tag = (cell.Tag as string ?? string.Empty).Split(':');
                            cellParagraph.HeaderCell = tag[0] == "TH";
                            cellParagraph.CellAlign = tag.Length > 1 && tag[1] is "L" or "C" or "R" ? tag[1] : null;
                            bool first = true;
                            foreach (var cellBlock in cell.Blocks.OfType<Paragraph>())
                            {
                                if (!first) cellParagraph.Runs.Add(new RichRun(LineBreak));
                                AddRuns(cellParagraph.Runs, cellBlock.Inlines, Baseline(cellBlock), link: null);
                                first = false;
                            }
                            output.Add(cellParagraph);
                        }
                        rowIndex++;
                    }
                    break;
            }
        }
    }

    private static RichParagraph FromParagraph(Paragraph p)
    {
        var result = new RichParagraph();
        var tag = p.Tag as string;
        if (tag == RichTextDocumentConverter.TagHr)
        {
            result.Rule = true;
            result.Runs.Add(new RichRun(RuleText));
            return result;
        }
        if (RichTextDocumentConverter.IsPreTag(tag))
        {
            result.Pre = true;
            result.PreLanguage = RichTextDocumentConverter.PreLanguageOf(tag);
            var text = PlainText(p.Inlines);
            if (text.Length > 0) result.Runs.Add(new RichRun(text, Code: true));
            return result;
        }
        if (tag is { Length: 2 } && tag[0] == 'H' && tag[1] is >= '1' and <= '6')
            result.Heading = tag[1] - '0';
        AddRuns(result.Runs, p.Inlines, Baseline(p), link: null);
        return result;
    }

    /// <summary>Formatting the paragraph gives all its text, which its runs do not repeat (a heading's bold).</summary>
    private static (bool Bold, bool Italic) Baseline(Paragraph p) =>
        (p.FontWeight >= FontWeights.Bold, p.FontStyle == FontStyles.Italic);

    private static void AddRuns(List<RichRun> target, InlineCollection inlines, (bool Bold, bool Italic) baseline, string? link)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case System.Windows.Documents.LineBreak:
                    target.Add(new RichRun(LineBreak));
                    break;

                case InlineUIContainer container when RichTextDocumentConverter.ImageOf(container) is { } image:
                    target.Add(new RichRun(ObjectChar, Link: link, Image: image));
                    break;

                case Run run when run.Text.Length > 0:
                    var decorations = run.TextDecorations;
                    bool Has(TextDecorationLocation where) => decorations != null && decorations.Any(d => d.Location == where);
                    Append(target, new RichRun(
                        Clean(run.Text),
                        Bold: run.FontWeight >= FontWeights.Bold && !baseline.Bold,
                        Italic: run.FontStyle == FontStyles.Italic && !baseline.Italic,
                        Underline: Has(TextDecorationLocation.Underline),
                        Strike: Has(TextDecorationLocation.Strikethrough),
                        // The run's own font, as the HTML writer reads it, never an inherited one.
                        Code: RichTextDocumentConverter.IsCodeFont(run.ReadLocalValue(TextElement.FontFamilyProperty) as FontFamily),
                        Link: link));
                    break;

                case Hyperlink hyperlink:
                    var href = hyperlink.Tag as string ?? hyperlink.NavigateUri?.ToString();
                    AddRuns(target, hyperlink.Inlines, baseline, string.IsNullOrEmpty(href) ? link : href);
                    break;

                case Span span:
                    AddRuns(target, span.Inlines, baseline, link);
                    break;
            }
        }
    }

    /// <summary>Adds a run, joining it to the one before when nothing but the text differs.</summary>
    private static void Append(List<RichRun> target, RichRun run)
    {
        if (target.Count > 0 && target[^1] is { Image: null, IsLineBreak: false } last && last.SameFormatAs(run))
            target[^1] = last with { Text = last.Text + run.Text };
        else
            target.Add(run);
    }

    /// <summary>
    /// Text as the editor can hold it in one paragraph: no paragraph breaks, and none of the
    /// characters the editor gives a meaning of its own (cell and row marks, the object character).
    /// </summary>
    internal static string Clean(string text)
    {
        if (text.AsSpan().IndexOfAny(Reserved) < 0) return text;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(Array.IndexOf(Reserved, c) >= 0 ? ' ' : c);
        return sb.ToString();
    }

    private static readonly char[] Reserved = { '\r', '\n', '\v', '\f', '\u0007', (char)0xFFF9, (char)0xFFFA, (char)0xFFFB, (char)0xFFFC };

    private static string PlainText(InlineCollection inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case System.Windows.Documents.LineBreak: sb.Append('\v'); break;
                case Run run: sb.Append(Clean(run.Text)); break;
                case Span span: sb.Append(PlainText(span.Inlines)); break;
            }
        }
        return sb.ToString();
    }

    // ─────────────────────────── paragraphs → FlowDocument ───────────────────

    /// <summary>A detached document for <see cref="RichTextDocumentConverter"/> to turn into HTML, Markdown or plain text.</summary>
    public static FlowDocument ToDocument(IReadOnlyList<RichParagraph> paragraphs)
    {
        var doc = new FlowDocument();
        int index = 0;
        Build(paragraphs, ref index, depth: 0, doc.Blocks);
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
        return doc;
    }

    private static void Build(IReadOnlyList<RichParagraph> ps, ref int i, int depth, BlockCollection target)
    {
        while (i < ps.Count)
        {
            var p = ps[i];
            if (p.QuoteDepth < depth) return;
            if (p.QuoteDepth > depth)
            {
                var quote = RichTextDocumentConverter.NewQuoteSection();
                Build(ps, ref i, depth + 1, quote.Blocks);
                if (quote.Blocks.Count > 0) target.Add(quote);
                continue;
            }
            if (p.IsCell) target.Add(BuildTable(ps, ref i, depth));
            else if (p.List != RichListKind.None) target.Add(BuildList(ps, ref i, depth, level: 0));
            else
            {
                target.Add(BuildParagraph(p));
                i++;
            }
        }
    }

    private static List BuildList(IReadOnlyList<RichParagraph> ps, ref int i, int depth, int level)
    {
        var kind = ps[i].List;
        var list = new List
        {
            MarkerStyle = kind == RichListKind.Numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        };
        while (i < ps.Count)
        {
            var p = ps[i];
            if (p.QuoteDepth != depth || p.IsCell || p.List == RichListKind.None || p.ListLevel < level) break;
            if (p.ListLevel > level)
            {
                // A deeper item belongs inside the item before it.
                var nested = BuildList(ps, ref i, depth, level + 1);
                if (list.ListItems.LastListItem is null) list.ListItems.Add(new ListItem(new Paragraph()));
                list.ListItems.LastListItem!.Blocks.Add(nested);
                continue;
            }
            if (p.List != kind) break; // a bulleted list straight after a numbered one is its own list
            list.ListItems.Add(new ListItem(BuildParagraph(p)));
            i++;
        }
        return list;
    }

    private static Table BuildTable(IReadOnlyList<RichParagraph> ps, ref int i, int depth)
    {
        int id = ps[i].Table;
        var table = new Table { CellSpacing = 0 };
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        int columns = 0;
        while (i < ps.Count && ps[i].Table == id && ps[i].QuoteDepth == depth)
        {
            int rowIndex = ps[i].Row;
            var row = new TableRow();
            while (i < ps.Count && ps[i].Table == id && ps[i].Row == rowIndex && ps[i].QuoteDepth == depth)
            {
                var p = ps[i];
                var cell = new TableCell(BuildParagraph(p))
                {
                    Tag = (p.HeaderCell ? "TH" : "TD") + (p.CellAlign is null ? string.Empty : ":" + p.CellAlign),
                };
                if (p.HeaderCell) cell.FontWeight = FontWeights.Bold;
                row.Cells.Add(cell);
                i++;
            }
            columns = Math.Max(columns, row.Cells.Count);
            group.Rows.Add(row);
        }
        for (int c = 0; c < columns; c++) table.Columns.Add(new TableColumn());
        return table;
    }

    private static readonly FontFamily CodeFont = new("Consolas");

    private static Paragraph BuildParagraph(RichParagraph p)
    {
        if (p.Rule)
            return new Paragraph(new Run(RuleText)) { Tag = RichTextDocumentConverter.TagHr };

        var paragraph = new Paragraph();
        if (p.Pre)
        {
            paragraph.Tag = string.IsNullOrEmpty(p.PreLanguage)
                ? RichTextDocumentConverter.TagPre
                : RichTextDocumentConverter.TagPre + ":" + p.PreLanguage;
            paragraph.FontFamily = CodeFont;
            var lines = p.Text.Split('\v');
            for (int n = 0; n < lines.Length; n++)
            {
                if (n > 0) paragraph.Inlines.Add(new System.Windows.Documents.LineBreak());
                paragraph.Inlines.Add(new Run(lines[n]));
            }
            return paragraph;
        }

        if (p.Heading is >= 1 and <= 6)
        {
            paragraph.Tag = "H" + p.Heading;
            paragraph.FontSize = RichTextDocumentConverter.HeadingFontSize(p.Heading);
            paragraph.FontWeight = FontWeights.Bold;
        }

        int at = 0;
        while (at < p.Runs.Count)
        {
            var run = p.Runs[at];
            if (run.Link is null)
            {
                AddInline(paragraph.Inlines, run, p);
                at++;
                continue;
            }
            // Everything in a row with the same address is one link.
            var hyperlink = new Hyperlink { Tag = run.Link };
            if (Uri.TryCreate(run.Link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                hyperlink.NavigateUri = uri;
            while (at < p.Runs.Count && string.Equals(p.Runs[at].Link, run.Link, StringComparison.Ordinal))
                AddInline(hyperlink.Inlines, p.Runs[at++], p);
            paragraph.Inlines.Add(hyperlink);
        }
        return paragraph;
    }

    private static void AddInline(InlineCollection target, RichRun run, RichParagraph owner)
    {
        if (run.Image is { } image)
        {
            target.Add(RichTextDocumentConverter.CreateImageElement(image, source: null));
            return;
        }
        // A run may hold line breaks (text typed with Shift+Enter comes back that way).
        var parts = run.Text.Split('\v');
        for (int n = 0; n < parts.Length; n++)
        {
            if (n > 0) target.Add(new System.Windows.Documents.LineBreak());
            if (parts[n].Length == 0) continue;
            var text = new Run(parts[n]);
            // A heading is bold as a whole; its runs do not say so again.
            if (run.Bold && owner.Heading == 0 && !owner.HeaderCell) text.FontWeight = FontWeights.Bold;
            if (run.Italic) text.FontStyle = FontStyles.Italic;
            if (run.Code) text.FontFamily = CodeFont;
            if (run.Underline || run.Strike)
            {
                var decorations = new TextDecorationCollection();
                if (run.Underline) decorations.Add(TextDecorations.Underline);
                if (run.Strike) decorations.Add(TextDecorations.Strikethrough);
                text.TextDecorations = decorations;
            }
            target.Add(text);
        }
    }
}
