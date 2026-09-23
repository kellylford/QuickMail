using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;

namespace QuickMail.Helpers;

/// <summary>
/// The paragraph styles offered by the compose editor's Paragraph style box, the
/// Format → Paragraph Style menu and their keys. The numeric value is the box's
/// item index.
/// </summary>
public enum ComposeParagraphStyle
{
    Normal = 0,
    Heading1 = 1,
    Heading2 = 2,
    Heading3 = 3,
    Heading4 = 4,
    Heading5 = 5,
    Heading6 = 6,
    Quote = 7,
}

/// <summary>
/// Block-level edits on the HTML-mode editor's <see cref="FlowDocument"/>:
/// headings, normal text, and putting paragraphs in and out of a quote. Pure
/// document operations with no window, so they are unit-testable; the compose
/// window supplies the selection and does the announcing.
///
/// A quote is a <see cref="Section"/> container (see
/// <see cref="RichTextDocumentConverter.NewQuoteSection"/>). Adding a quote wraps
/// the selected blocks in a new one; removing a quote moves them out of the
/// innermost quote that holds them all, splitting that quote in two when the
/// selection was in its middle.
/// </summary>
public static class RichTextBlockEditing
{
    /// <summary>Heading level 1–6 of the paragraph, or 0 when it is not a heading.</summary>
    public static int HeadingLevel(Paragraph? paragraph) =>
        paragraph?.Tag is string { Length: 2 } tag && tag[0] == 'H' && tag[1] is >= '1' and <= '6'
            ? tag[1] - '0'
            : 0;

    /// <summary>
    /// The style the Paragraph style box shows for this paragraph, or null for a
    /// block the box has no entry for (a code block).
    /// </summary>
    public static ComposeParagraphStyle? StyleOf(Paragraph? paragraph)
    {
        if (paragraph is null) return ComposeParagraphStyle.Normal;
        var level = HeadingLevel(paragraph);
        if (level > 0) return (ComposeParagraphStyle)level;
        if (RichTextDocumentConverter.IsPreTag(paragraph.Tag as string)) return null;
        return RichTextDocumentConverter.QuoteDepth(paragraph) > 0
            ? ComposeParagraphStyle.Quote
            : ComposeParagraphStyle.Normal;
    }

    /// <summary>
    /// Makes the paragraph a heading of <paramref name="level"/>, or with 0 a normal
    /// paragraph — which also turns a code block back into ordinary text.
    /// </summary>
    public static void SetHeading(Paragraph paragraph, int level)
    {
        if (level is >= 1 and <= 6)
        {
            paragraph.Tag = "H" + level;
            paragraph.FontSize = RichTextDocumentConverter.HeadingFontSize(level);
            paragraph.FontWeight = FontWeights.Bold;
            return;
        }
        if (paragraph.Tag is string tag && tag != RichTextDocumentConverter.TagHr)
            paragraph.Tag = null;
        paragraph.ClearValue(TextElement.FontSizeProperty);
        paragraph.ClearValue(TextElement.FontWeightProperty);
        paragraph.ClearValue(TextElement.FontFamilyProperty);
    }

    /// <summary>
    /// Puts heading tags back in step with the paragraphs' look after an Undo or
    /// Redo. WPF's undo restores a paragraph's font size and weight but not its
    /// <c>Tag</c>, so undoing "Heading 2" would leave normal-looking text that is
    /// still sent as a heading, and undoing "Normal text" the reverse. The size a
    /// heading command sets is the record of what the user sees: a heading tag
    /// whose size is gone is cleared, and a heading size and weight with no tag
    /// gets its tag back. Only headings are affected; quotes are whole elements,
    /// which undo restores intact.
    /// </summary>
    public static void ReconcileHeadingTags(FlowDocument document)
    {
        foreach (var paragraph in EnumerateBlocks(document.Blocks).OfType<Paragraph>())
        {
            var tag = paragraph.Tag as string;
            int level = HeadingLevel(paragraph);
            if (tag != null && level == 0) continue; // code block, rule — not ours to touch

            var size = paragraph.ReadLocalValue(TextElement.FontSizeProperty) as double?;
            bool bold = paragraph.ReadLocalValue(TextElement.FontWeightProperty) is FontWeight w && w == FontWeights.Bold;
            int levelFromLook = 0;
            if (size is { } s && bold)
                for (int n = 1; n <= 6 && levelFromLook == 0; n++)
                    if (RichTextDocumentConverter.HeadingFontSize(n) == s) levelFromLook = n;

            if (level != levelFromLook)
                paragraph.Tag = levelFromLook == 0 ? null : "H" + levelFromLook;
        }
    }

    /// <summary>
    /// Every paragraph from <paramref name="start"/> to <paramref name="end"/> in
    /// document order, at any depth of quote or list. With no selection this is
    /// the caret's paragraph. A selection that ends exactly at the start of a
    /// paragraph (Shift+Down to the next line) does not include that paragraph.
    /// </summary>
    public static List<Paragraph> ParagraphsInRange(FlowDocument document, TextPointer start, TextPointer end)
    {
        var startPara = start.Paragraph;
        if (startPara == null) return [];

        var endPara = end.Paragraph;
        if (endPara == null || startPara == endPara)
            return [startPara];

        // WPF places element-boundary positions between paragraphs, so the end can
        // sit in that structural gap (< 0) rather than exactly at ContentStart (== 0);
        // both mean the end paragraph was not selected.
        if (end.CompareTo(endPara.ContentStart) <= 0)
            return [startPara];

        var result = new List<Paragraph>();
        bool inRange = false;
        foreach (var block in EnumerateBlocks(document.Blocks))
        {
            if (block == startPara) inRange = true;
            if (inRange && block is Paragraph p) result.Add(p);
            if (block == endPara) break;
        }
        return result.Count > 0 ? result : [startPara];
    }

    /// <summary>Every block in document order, descending into quotes, sections, lists and table cells.</summary>
    public static IEnumerable<Block> EnumerateBlocks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            switch (block)
            {
                case Section section:
                    foreach (var inner in EnumerateBlocks(section.Blocks))
                        yield return inner;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var inner in EnumerateBlocks(item.Blocks))
                            yield return inner;
                    break;
                case Table table:
                    foreach (var cell in table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells))
                        foreach (var inner in EnumerateBlocks(cell.Blocks))
                            yield return inner;
                    break;
            }
        }
    }

    /// <summary>
    /// The innermost quote holding both paragraphs, or null when they share none.
    /// </summary>
    public static Section? CommonQuote(Paragraph first, Paragraph last)
    {
        var lastAncestors = new HashSet<DependencyObject>(Ancestors(last));
        foreach (var ancestor in Ancestors(first))
            if (ancestor is Section s && RichTextDocumentConverter.IsQuote(s) && lastAncestors.Contains(s))
                return s;
        return null;
    }

    /// <summary>
    /// Wraps the blocks from <paramref name="first"/>'s through <paramref name="last"/>'s
    /// in a new quote. A paragraph inside a list brings its whole list: the quote
    /// is placed around blocks that are siblings in one container. Returns false
    /// when nothing could be wrapped.
    /// </summary>
    public static bool AddQuote(Paragraph first, Paragraph last)
    {
        if (!TryFindSiblingUnits(first, last, out var container, out var firstUnit, out var lastUnit))
            return false;

        var quote = RichTextDocumentConverter.NewQuoteSection();
        container.InsertBefore(firstUnit, quote);
        foreach (var block in SiblingRange(firstUnit, lastUnit))
        {
            container.Remove(block);
            quote.Blocks.Add(block);
        }
        return true;
    }

    /// <summary>
    /// Takes the blocks from <paramref name="first"/>'s through <paramref name="last"/>'s
    /// out of the innermost quote that holds them all, one level. Quoted content
    /// before and after the selection stays quoted, in quotes of its own. Returns
    /// false when the paragraphs share no quote.
    /// </summary>
    public static bool RemoveQuote(Paragraph first, Paragraph last)
    {
        var quote = CommonQuote(first, last);
        // A received message can have a quote inside a list item; it comes out into the item.
        var outer = quote?.Parent is ListItem item ? item.Blocks : ContainerBlocks(quote?.Parent);
        if (quote is null || outer is null)
            return false;

        var firstUnit = ChildOf(quote, first);
        var lastUnit = ChildOf(quote, last);
        if (firstUnit is null || lastUnit is null) return false;

        // Quoted blocks after the selection move into a quote of their own.
        var after = RichTextDocumentConverter.NewQuoteSection();
        for (var block = lastUnit.NextBlock; block != null;)
        {
            var next = block.NextBlock;
            quote.Blocks.Remove(block);
            after.Blocks.Add(block);
            block = next;
        }

        Block insertAfter = quote;
        foreach (var block in SiblingRange(firstUnit, lastUnit))
        {
            quote.Blocks.Remove(block);
            outer.InsertAfter(insertAfter, block);
            insertAfter = block;
        }

        if (after.Blocks.Count > 0)
            outer.InsertAfter(insertAfter, after);
        if (quote.Blocks.Count == 0)
            outer.Remove(quote);
        return true;
    }

    /// <summary>Takes the paragraph out of every quote around it.</summary>
    public static void RemoveAllQuotes(Paragraph paragraph)
    {
        while (RemoveQuote(paragraph, paragraph)) { }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<DependencyObject> Ancestors(TextElement element)
    {
        for (DependencyObject? el = element.Parent; el != null; el = (el as TextElement)?.Parent)
            yield return el;
    }

    /// <summary>The block collection of a FlowDocument or Section; null for anything else.</summary>
    private static BlockCollection? ContainerBlocks(DependencyObject? container) => container switch
    {
        FlowDocument doc => doc.Blocks,
        Section section => section.Blocks,
        TableCell cell => cell.Blocks,
        _ => null,
    };

    /// <summary>The direct child of <paramref name="container"/> that holds <paramref name="element"/>.</summary>
    private static Block? ChildOf(DependencyObject container, TextElement element)
    {
        TextElement current = element;
        while (current.Parent is TextElement parent && parent != container)
            current = parent;
        return current.Parent == container ? current as Block : null;
    }

    /// <summary>
    /// The chain of (container, child block) pairs from the innermost container
    /// outward, where a container is the document or a Section. A paragraph in a
    /// list reaches its first container through the list.
    /// </summary>
    private static List<(DependencyObject Container, Block Unit)> Containers(TextElement element)
    {
        var chain = new List<(DependencyObject, Block)>();
        TextElement current = element;
        while (current.Parent is DependencyObject parent)
        {
            if (current is Block block && ContainerBlocks(parent) != null)
                chain.Add((parent, block));
            if (parent is not TextElement parentElement) break;
            current = parentElement;
        }
        return chain;
    }

    private static bool TryFindSiblingUnits(Paragraph first, Paragraph last,
        out BlockCollection container, out Block firstUnit, out Block lastUnit)
    {
        container = null!;
        firstUnit = lastUnit = null!;
        var lastChain = Containers(last);
        foreach (var (c, unit) in Containers(first))
        {
            var match = lastChain.FirstOrDefault(pair => pair.Container == c);
            if (match.Unit is null) continue;
            container = ContainerBlocks(c)!;
            firstUnit = unit;
            lastUnit = match.Unit;
            // A reversed range would never reach its end; treat it as one block.
            if (!SiblingRange(firstUnit, lastUnit).Contains(lastUnit))
                lastUnit = firstUnit;
            return true;
        }
        return false;
    }

    /// <summary>The sibling blocks from <paramref name="first"/> through <paramref name="last"/>, materialized so they can be moved.</summary>
    private static List<Block> SiblingRange(Block first, Block last)
    {
        var range = new List<Block>();
        for (var block = first; block != null; block = block.NextBlock)
        {
            range.Add(block);
            if (block == last) break;
        }
        return range;
    }
}
