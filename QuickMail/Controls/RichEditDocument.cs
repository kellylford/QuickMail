using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using QuickMail.Helpers;
using QuickMail.Services;

namespace QuickMail.Controls;

/// <summary>
/// A formatted message body in the hosted WinUI editor (#1099, HTML mode): writing a body in,
/// reading it back, and the formatting commands. Bodies pass in and out as
/// <see cref="RichParagraph"/> lists (<see cref="RichBody"/>).
///
/// <para><b>What the editor holds natively</b>, so a screen reader meets the real thing: bold,
/// italic, underline and strikethrough; links (a RichEdit "friendly name" link, announced as a
/// link); bulleted and numbered lists with levels; pictures with alternative text (announced as
/// a graphic with that text); and the Heading 1 to 6 paragraph styles.</para>
///
/// <para><b>What it has no notion of</b> is written in paragraph properties that RichEdit keeps
/// but does not draw, checked in the Stage 3 spike to survive editing and a round trip:
/// a quote's depth is the paragraph's right indent in points (1 per level, too small to see; the
/// visible indent is on the left), and a code block is "keep together". A rule is a paragraph
/// holding <see cref="RichBody.RuleText"/>.</para>
///
/// <para><b>Tables</b> are not RichEdit's own. A screen reader reads one of those a row at a
/// time with the cells run together ("Cell A1Cell B1"). Here each cell is a paragraph of its
/// own, so it reads as its text, and QuickMail can say which row and column it is. A cell is a
/// paragraph marked "keep with next"; "no line number" alternates from one row to the next, and
/// "widow control" from one table to the next, which is how rows and tables are told apart
/// again. What is sent is a real table.</para>
///
/// <para><b>Pictures.</b> RichEdit will not say which picture an object is, so each inserted
/// picture is remembered with a range on its character. A TOM range moves with the text around
/// it, so it still finds its picture after edits; a deleted picture leaves an empty range.</para>
/// </summary>
internal sealed class RichEditDocument
{
    /// <summary>Left indent, in points, for each level of quote or list.</summary>
    private const float LevelIndent = 18f;

    private const char ImagePlaceholder = (char)0xE001;
    private const char RowStart = (char)0xFFF9;
    private const char RowEnd = (char)0xFFFB;
    private const char CellEnd = (char)0x0007;
    private const char ObjectChar = (char)0xFFFC;
    private const string CodeFontName = "Consolas";

    private readonly RichEditBox _editor;
    private readonly Func<string, BitmapSource?> _resolveImage;
    private readonly List<(ITextRange Range, ComposeImage Info)> _images = new();

    // Where inline code was loaded, so its font can be put back (see MendFonts). Ranges move
    // with the text around them.
    private readonly List<ITextRange> _codeRanges = new();
    private bool _loading;

    /// <param name="resolveImage">The picture for a <c>src</c> as the editor draws it, or null for a placeholder.</param>
    public RichEditDocument(RichEditBox editor, Func<string, BitmapSource?> resolveImage)
    {
        _editor = editor;
        _resolveImage = resolveImage;
    }

    private RichEditTextDocument Doc => _editor.Document;

    /// <summary>Body text size in points: RichEdit sizes text in points, WinUI in pixels.</summary>
    private float BaseSize => (float)(_editor.FontSize * 0.75);

    private string BaseFont => _editor.FontFamily?.Source ?? "Segoe UI";

    private static float HeadingSize(int level) => (float)(RichTextDocumentConverter.HeadingFontSize(level) * 0.75);

    private string Raw()
    {
        Doc.GetText(TextGetOptions.None, out var raw);
        return raw ?? string.Empty;
    }

    // ─────────────────────────────── Loading ─────────────────────────────────

    /// <summary>Replaces the whole body. Not an edit: the undo history is cleared.</summary>
    public void Load(IReadOnlyList<RichParagraph> paragraphs)
    {
        _images.Clear();
        _codeRanges.Clear();
        _typingFormat = null;
        _loading = true;
        Doc.BatchDisplayUpdates();
        try
        {
            int length = Raw().Length;
            // After a table that ends the body, a line to type on: Enter in a cell is a line
            // break in the cell, so there would be no other way past the table.
            if (paragraphs.Count > 0 && paragraphs[^1].IsCell)
                paragraphs = paragraphs.Append(new RichParagraph { QuoteDepth = paragraphs[^1].QuoteDepth }).ToList();
            InsertAt(Doc.GetRange(0, length), paragraphs, wholeDocument: true);
            Doc.Selection.SetRange(0, 0);
            if (Raw().Length <= 1)
            {
                // An empty body has no text to take a format from, so the caret keeps whatever
                // the last body left it: after bold text, a new empty body started out bold, and
                // Ctrl+B then turned bold off while saying nothing was wrong.
                var caret = Doc.Selection.CharacterFormat;
                caret.Bold = FormatEffect.Off;
                caret.Italic = FormatEffect.Off;
                caret.Underline = UnderlineType.None;
                caret.Strikethrough = FormatEffect.Off;
                caret.Size = BaseSize;
                caret.Name = BaseFont;
            }
        }
        finally
        {
            _loading = false;
            Doc.ApplyDisplayUpdates();
        }
        Doc.ClearUndoRedoHistory();
    }

    /// <summary>
    /// The control sets every character to its own font and size when it finishes loading, which
    /// is after a body loaded as the window opened. That loses what a font or a size stands for
    /// here: a heading's size, a code block's font, and inline code, which is known only by its
    /// font and would be sent as plain text. This puts them back from what survives (paragraph
    /// styles, the code block mark, and the remembered inline code).
    ///
    /// <para>It replaces loading the body a second time, which also threw away anything done
    /// in the moment between: Ctrl+B pressed straight after switching to HTML was undone with
    /// nothing said, and the text typed next went in plain.</para>
    /// </summary>
    public void MendFonts()
    {
        EnsureTypingFormat();

        var raw = Raw();
        if (raw.Length == 0) return;
        // With nothing to undo yet (the usual case: the body was loaded a moment ago), the
        // history is cleared again afterwards. Otherwise one Ctrl+Z too many would take the
        // code font back off, and inline code would be sent as plain text.
        bool nothingToUndo = !Doc.CanUndo();
        Doc.BatchDisplayUpdates();
        try
        {
            foreach (var slot in Slots(raw, 0, raw.Length))
            {
                var paragraph = Doc.GetRange(slot.Start, slot.Start + slot.Length);
                var format = paragraph.ParagraphFormat;
                var style = format.Style;
                if (style is >= ParagraphStyle.Heading1 and <= ParagraphStyle.Heading6)
                {
                    paragraph.CharacterFormat.Size = HeadingSize(style - ParagraphStyle.Heading1 + 1);
                    paragraph.CharacterFormat.Bold = FormatEffect.On;
                }
                else if (format.KeepTogether == FormatEffect.On && format.KeepWithNext != FormatEffect.On && slot.Length > 0)
                {
                    paragraph.CharacterFormat.Name = CodeFontName;
                }
            }
            foreach (var code in _codeRanges)
            {
                if (code.EndPosition > code.StartPosition) code.CharacterFormat.Name = CodeFontName;
            }
        }
        finally
        {
            Doc.ApplyDisplayUpdates();
        }
        if (nothingToUndo) Doc.ClearUndoRedoHistory();
    }

    /// <summary>True when the selection takes in the whole body (as after Ctrl+A).</summary>
    public bool EverythingIsSelected()
    {
        var selection = Doc.Selection;
        int last = Math.Max(0, Doc.GetRange(0, 0).StoryLength - 1);
        return last > 0 && selection.StartPosition == 0 && selection.EndPosition >= last;
    }

    /// <summary>
    /// Deletes the whole body and leaves it plain text, as one undo step. Deleting everything
    /// the control's way leaves one empty paragraph that keeps the kind of the last one (a
    /// heading, a list item, a code block, a quote) and whatever was switched on for typing;
    /// that empty paragraph was then sent as an empty heading or bullet, and counted as
    /// "formatting" that switching to Plain Text would lose.
    /// </summary>
    public void DeleteEverything()
    {
        Doc.BeginUndoGroup();
        try
        {
            var all = Doc.GetRange(0, Math.Max(0, Doc.GetRange(0, 0).StoryLength - 1));
            all.SetText(TextSetOptions.None, string.Empty);
            var range = Doc.GetRange(0, 1);
            var format = range.ParagraphFormat;
            format.Style = ParagraphStyle.Normal;
            format.ListType = MarkerType.None;
            format.KeepTogether = FormatEffect.Off;
            format.KeepWithNext = FormatEffect.Off;
            format.NoLineNumber = FormatEffect.Off;
            format.Alignment = ParagraphAlignment.Left;
            format.SetIndents(0, 0, 0);
            Doc.Selection.SetRange(0, 0);
            foreach (var characters in new[] { range.CharacterFormat, Doc.Selection.CharacterFormat })
            {
                characters.Bold = FormatEffect.Off;
                characters.Italic = FormatEffect.Off;
                characters.Underline = UnderlineType.None;
                characters.Strikethrough = FormatEffect.Off;
                characters.Size = BaseSize;
                characters.Name = BaseFont;
            }
            _typingFormat = null;
        }
        finally { Doc.EndUndoGroup(); }
    }

    /// <summary>The caret moved: a format switched on for typing somewhere else no longer applies.</summary>
    public void ForgetTypingFormatIfMoved()
    {
        if (_typingFormat is not { } wanted) return;
        var selection = Doc.Selection;
        if (selection.StartPosition != selection.EndPosition || selection.StartPosition != wanted.Position)
            _typingFormat = null;
    }

    /// <summary>
    /// -1 when the caret is at the start of a table cell, 1 when at its end, 2 when the cell is
    /// empty (both), and 0 otherwise or outside a table or with text selected. Backspace at a
    /// cell's start and Delete at its end would join two cells.
    /// </summary>
    public int TableCellEdge()
    {
        var selection = Doc.Selection;
        if (selection.StartPosition != selection.EndPosition) return 0;
        var block = BlockAtCaret();
        if (!block.InTable) return 0;
        bool atStart = selection.StartPosition == block.Start;
        bool atEnd = selection.StartPosition >= block.End - 1;
        return atStart && atEnd ? 2 : atStart ? -1 : atEnd ? 1 : 0;
    }

    /// <summary>
    /// Puts paragraphs in place of the selection, as one undo step: a paste, or a template. One
    /// paragraph goes in as text within the line the caret is on; several bring their own
    /// paragraph formats. The caret ends after what was inserted.
    /// </summary>
    public void InsertAtSelection(IReadOnlyList<RichParagraph> paragraphs, bool formatLast = true)
    {
        if (paragraphs.Count == 0) return;
        if (paragraphs.Count > 1)
        {
            var block = BlockAt(Doc.Selection.StartPosition);
            if (block.InTable)
            {
                // A paragraph break in a cell would be a new cell: the paragraphs go in as lines.
                var merged = new RichParagraph();
                for (int i = 0; i < paragraphs.Count; i++)
                {
                    if (i > 0) merged.Runs.Add(new RichRun(RichBody.LineBreak));
                    merged.Runs.AddRange(paragraphs[i].Runs);
                }
                paragraphs = new[] { merged };
            }
            else if (Doc.Selection.EndPosition < block.End - 1)
            {
                // Text follows the caret on its line. It ends up on the last pasted paragraph's
                // line and keeps the format it had (its quote, its list), as it would in Word.
                formatLast = false;
            }
        }
        Doc.BeginUndoGroup();
        Doc.BatchDisplayUpdates();
        try
        {
            var selection = Doc.Selection;
            int end = InsertAt(Doc.GetRange(selection.StartPosition, selection.EndPosition), paragraphs,
                wholeDocument: false, formatLast);
            Doc.Selection.SetRange(end, end);
        }
        finally
        {
            Doc.ApplyDisplayUpdates();
            Doc.EndUndoGroup();
        }
    }

    /// <summary>
    /// Puts lines of plain text (a template) in at the caret as plain paragraphs of their own:
    /// never glued to the line the caret is on, nor taking on its heading or quote. The text
    /// after the caret moves to a line of its own and keeps its format. The caret ends after the
    /// last line put in.
    /// </summary>
    public void InsertLines(IReadOnlyList<string> lines)
    {
        var selection = Doc.Selection;
        var block = BlockAt(selection.StartPosition);
        bool atStart = selection.StartPosition == block.Start;
        bool atEnd = selection.EndPosition >= block.End - 1;

        var paragraphs = new List<RichParagraph>();
        if (!atStart) paragraphs.Add(new RichParagraph());
        foreach (var line in lines)
        {
            var paragraph = new RichParagraph();
            if (line.Length > 0) paragraph.Runs.Add(new RichRun(RichBody.Clean(line)));
            paragraphs.Add(paragraph);
        }
        int tail = 0;
        if (!atEnd)
        {
            paragraphs.Add(new RichParagraph());
            tail = 1; // the break that starts the tail's line
        }
        InsertAtSelection(paragraphs, formatLast: atEnd);
        if (tail > 0)
        {
            int caret = Math.Max(0, Doc.Selection.StartPosition - tail);
            Doc.Selection.SetRange(caret, caret);
        }
    }

    /// <summary>Writes <paramref name="paragraphs"/> over <paramref name="target"/>; returns where they end.</summary>
    /// <param name="formatLast">
    /// False when the last paragraph is only there to push the text after the caret onto a line
    /// of its own: that text keeps the format it had.
    /// </param>
    private int InsertAt(ITextRange target, IReadOnlyList<RichParagraph> paragraphs, bool wholeDocument, bool formatLast = true)
    {
        int start = target.StartPosition;
        int lengthBefore = Raw().Length;
        int replaced = target.EndPosition - target.StartPosition;

        // 1. The text, with a placeholder character for each picture.
        var text = new StringBuilder();
        for (int i = 0; i < paragraphs.Count; i++)
        {
            // Between paragraphs, whatever they hold: an empty first paragraph (the blank line a
            // reply starts with) still needs its break.
            if (i > 0) text.Append('\r');
            AppendText(text, paragraphs[i]);
        }
        target.SetText(TextSetOptions.None, text.ToString());

        // Text set over other text takes the format of what was there (a heading's size and
        // bold, a quote's indent). Paragraphs that bring their own formats start from plain.
        bool ownFormats = wholeDocument || paragraphs.Count > 1;
        if (ownFormats)
        {
            var written0 = Doc.GetRange(start, start + text.Length);
            var characters = written0.CharacterFormat;
            characters.Bold = FormatEffect.Off;
            characters.Italic = FormatEffect.Off;
            characters.Underline = UnderlineType.None;
            characters.Strikethrough = FormatEffect.Off;
            characters.Size = BaseSize;
            characters.Name = BaseFont;
            if (wholeDocument)
            {
                var paragraphFormat = written0.ParagraphFormat;
                paragraphFormat.Style = ParagraphStyle.Normal;
                paragraphFormat.ListType = MarkerType.None;
                paragraphFormat.KeepTogether = FormatEffect.Off;
                paragraphFormat.KeepWithNext = FormatEffect.Off;
                paragraphFormat.NoLineNumber = FormatEffect.Off;
                paragraphFormat.Alignment = ParagraphAlignment.Left;
                paragraphFormat.SetIndents(0, 0, 0);
            }
        }

        // 2. Where each paragraph ended up, read back from the document, not assumed.
        var raw = Raw();
        int regionEnd = Math.Min(raw.Length, start + (raw.Length - lengthBefore + replaced));
        if (wholeDocument) regionEnd = raw.Length;
        var slots = Slots(raw, start, regionEnd);
        // An empty last paragraph (a blank line copied with the line before it, a template put
        // in front of other text) is the line the text after the region now starts.
        if (!wholeDocument && slots.Count == paragraphs.Count - 1 && (text.Length == 0 || text[^1] == (char)13))
            slots.Add(new Slot(regionEnd, 0, -1, 0));
        if (slots.Count != paragraphs.Count)
        {
            LogService.Log($"New editor: {paragraphs.Count} paragraphs were written but {slots.Count} were found; " +
                           "the text is in place without its formatting.");
            // Never leave a picture's stand-in character in a message.
            for (int i = Math.Min(regionEnd, raw.Length) - 1; i >= start; i--)
                if (raw[i] == ImagePlaceholder) Doc.GetRange(i, i + 1).SetText(TextSetOptions.None, string.Empty);
            return Math.Min(Raw().Length, start + (Raw().Length - lengthBefore + replaced));
        }

        // 3. Formats, last paragraph first: setting a link puts its address into the text, which
        //    moves everything after it.
        bool paragraphFormats = wholeDocument || paragraphs.Count > 1;
        for (int i = slots.Count - 1; i >= 0; i--)
            ApplyFormats(slots[i].Start, paragraphs[i],
                paragraphFormats
                && (i > 0 || wholeDocument || IsParagraphStart(raw, start))
                && (formatLast || i < slots.Count - 1));

        return Math.Min(Raw().Length, start + (Raw().Length - lengthBefore + replaced));
    }

    private static bool IsParagraphStart(string raw, int position) =>
        position == 0 || raw[position - 1] is '\r' or CellEnd;

    private static void AppendText(StringBuilder text, RichParagraph p)
    {
        foreach (var run in p.Runs)
            text.Append(run.Image is null ? run.Text : ImagePlaceholder.ToString());
    }

    private void ApplyFormats(int start, RichParagraph p, bool paragraphFormat)
    {
        // Runs, last first. Runs in a row with one address are one link, set once the first of
        // them has been formatted.
        var offsets = new int[p.Runs.Count + 1];
        for (int i = 0; i < p.Runs.Count; i++)
            offsets[i + 1] = offsets[i] + (p.Runs[i].Image is null ? p.Runs[i].Text.Length : 1);

        for (int i = p.Runs.Count - 1; i >= 0; i--)
        {
            var run = p.Runs[i];
            int from = start + offsets[i], to = start + offsets[i + 1];
            if (run.Image is { } image)
            {
                InsertImageAt(from, to, image);
            }
            else if (to > from)
            {
                var format = Doc.GetRange(from, to).CharacterFormat;
                if (run.Bold) format.Bold = FormatEffect.On;
                if (run.Italic) format.Italic = FormatEffect.On;
                if (run.Underline) format.Underline = UnderlineType.Single;
                if (run.Strike) format.Strikethrough = FormatEffect.On;
                if (run.Code || p.Pre) format.Name = CodeFontName;
                if (_loading && run.Code && !p.Pre) _codeRanges.Add(Doc.GetRange(from, to));
            }

            bool firstOfLink = run.Link is not null
                               && (i == 0 || !string.Equals(p.Runs[i - 1].Link, run.Link, StringComparison.Ordinal));
            if (firstOfLink)
            {
                int last = i;
                while (last + 1 < p.Runs.Count && string.Equals(p.Runs[last + 1].Link, run.Link, StringComparison.Ordinal)) last++;
                SetLink(Doc.GetRange(from, start + offsets[last + 1]), run.Link!);
            }
        }

        if (!paragraphFormat) return;

        if (p.IsCell)
        {
            var cell = WholeParagraph(start);
            if (p.HeaderCell) cell.CharacterFormat.Bold = FormatEffect.On;
            var cellFormat = cell.ParagraphFormat;
            cellFormat.Style = ParagraphStyle.Normal;
            cellFormat.KeepTogether = FormatEffect.Off;
            cellFormat.ListType = MarkerType.None;
            cellFormat.KeepWithNext = FormatEffect.On;
            cellFormat.NoLineNumber = p.Row % 2 == 1 ? FormatEffect.On : FormatEffect.Off;
            cellFormat.WidowControl = p.Table % 2 == 1 ? FormatEffect.On : FormatEffect.Off;
            cellFormat.Alignment = p.CellAlign switch
            {
                "C" => ParagraphAlignment.Center,
                "R" => ParagraphAlignment.Right,
                _ => ParagraphAlignment.Left,
            };
            // One level in, so a table stands apart from the text around it.
            cellFormat.SetIndents(0, LevelIndent * (Math.Clamp(p.QuoteDepth, 0, 20) + 1), Math.Clamp(p.QuoteDepth, 0, 20));
            return;
        }

        var paragraph = WholeParagraph(start);
        // A paragraph being written has its runs' formats already; only a heading adds its own.
        if (p.Heading > 0) SetHeading(paragraph, p.Heading);
        else paragraph.ParagraphFormat.Style = ParagraphStyle.Normal;
        paragraph.ParagraphFormat.KeepTogether = p.Pre ? FormatEffect.On : FormatEffect.Off;
        paragraph.ParagraphFormat.KeepWithNext = FormatEffect.Off;
        SetQuoteAndList(paragraph, p.QuoteDepth, p.List, p.ListLevel);
    }

    private static void SetLink(ITextRange range, string address)
    {
        // RichEdit takes the address in quotes; a quote inside it would end it early.
        try { range.Link = "\"" + address.Replace("\"", "%22", StringComparison.Ordinal) + "\""; }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            LogService.Debug($"New editor: could not set a link to {address}: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes a paragraph a heading, or with 0 normal text. Either way its text takes the size
    /// and weight of what it now is, which is what the command means; a body being loaded sets
    /// only the style on a normal paragraph, so bold text in it stays bold.
    /// </summary>
    private void SetHeading(ITextRange paragraph, int level)
    {
        level = Math.Clamp(level, 0, 6);
        paragraph.ParagraphFormat.Style = level == 0 ? ParagraphStyle.Normal : ParagraphStyle.Heading1 + (level - 1);
        if (level > 0)
        {
            // "Keep with next" and "keep together" mean a table cell and a code block here; a
            // heading style must not bring either with it.
            paragraph.ParagraphFormat.KeepWithNext = FormatEffect.Off;
            paragraph.ParagraphFormat.KeepTogether = FormatEffect.Off;
        }
        var format = paragraph.CharacterFormat;
        format.Size = level == 0 ? BaseSize : HeadingSize(level);
        format.Bold = level == 0 ? FormatEffect.Off : FormatEffect.On;
    }

    /// <summary>
    /// Quote depth goes in the right indent, one point a level, where it is kept but not seen;
    /// what is seen is a left indent for each level of quote and of list.
    /// </summary>
    private static void SetQuoteAndList(ITextRange paragraph, int quoteDepth, RichListKind list, int listLevel)
    {
        var format = paragraph.ParagraphFormat;
        quoteDepth = Math.Clamp(quoteDepth, 0, 20);
        listLevel = list == RichListKind.None ? 0 : Math.Clamp(listLevel, 0, 8);
        switch (list)
        {
            case RichListKind.Bullet:
                format.ListType = MarkerType.Bullet;
                break;
            case RichListKind.Numbered:
                format.ListType = MarkerType.Arabic;
                format.ListStyle = MarkerStyle.Period;
                format.ListStart = 1;
                break;
            default:
                format.ListType = MarkerType.None;
                break;
        }
        if (list != RichListKind.None) format.ListLevelIndex = listLevel;
        format.SetIndents(0, LevelIndent * (quoteDepth + listLevel), quoteDepth);
    }

    private void InsertImageAt(int from, int to, ComposeImage info)
    {
        var bitmap = _resolveImage(info.Src);
        double width = bitmap?.PixelWidth ?? 64, height = bitmap?.PixelHeight ?? 48;
        if (bitmap is not null && info.Width is > 0)
        {
            height = height * info.Width.Value / Math.Max(1, width);
            width = info.Width.Value;
        }
        if (width > RichTextDocumentConverter.MaxEditorImageWidth)
        {
            height = height * RichTextDocumentConverter.MaxEditorImageWidth / width;
            width = RichTextDocumentConverter.MaxEditorImageWidth;
        }
        // Never too small to see: a picture sized 1 by 1 would go out unnoticed (#729).
        width = Math.Max(width, RichTextDocumentConverter.MinEditorImageSize);
        height = Math.Max(height, RichTextDocumentConverter.MinEditorImageSize);

        var range = Doc.GetRange(from, to);
        range.SetText(TextSetOptions.None, string.Empty);
        using var png = new MemoryStream(EncodePng(bitmap ?? Placeholder()));
        range.InsertImage((int)Math.Round(width), (int)Math.Round(height), 0, VerticalCharacterAlignment.Baseline,
            info.AccessibleName, png.AsRandomAccessStream());
        _images.Add((Doc.GetRange(from, from + 1), info));
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource? s_placeholder;

    /// <summary>A plain grey frame, drawn for a picture whose bytes are not here yet.</summary>
    private static BitmapSource Placeholder()
    {
        if (s_placeholder != null) return s_placeholder;
        const int w = 64, h = 48;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                byte shade = edge ? (byte)128 : (byte)220;
                int at = (y * w + x) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = shade;
                pixels[at + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return s_placeholder = bitmap;
    }

    // ─────────────────────────────── Reading ─────────────────────────────────

    private readonly record struct Slot(int Start, int Length, int Table, int Row);

    /// <summary>
    /// The paragraphs and table cells of raw document text between two positions, in order. A table
    /// row is a row-start mark and a break, its cells each ended by a cell mark, then a row-end
    /// mark and a break.
    /// </summary>
    internal static List<(int Start, int Length, int Table, int Row)> SlotsOf(string raw, int from, int to)
    {
        var slots = new List<(int, int, int, int)>();
        int table = -1, row = 0;
        bool inTable = false;
        int pos = from;
        while (pos < to)
        {
            if (raw[pos] == RowStart)
            {
                if (!inTable) { table++; row = 0; inTable = true; }
                pos += pos + 1 < to && raw[pos + 1] == '\r' ? 2 : 1;
                while (pos < to && raw[pos] != RowEnd)
                {
                    int cellEnd = raw.IndexOf(CellEnd, pos, to - pos);
                    if (cellEnd < 0) { cellEnd = to; }
                    slots.Add((pos, cellEnd - pos, table, row));
                    pos = Math.Min(to, cellEnd + 1);
                }
                pos += pos + 1 < to && raw[pos + 1] == '\r' ? 2 : 1;
                row++;
                continue;
            }
            inTable = false;
            int end = raw.IndexOf('\r', pos, to - pos);
            if (end < 0) end = to;
            slots.Add((pos, end - pos, -1, 0));
            pos = end + 1;
        }
        return slots;
    }

    private static List<Slot> Slots(string raw, int from, int to) =>
        SlotsOf(raw, from, to).Select(s => new Slot(s.Start, s.Length, s.Table, s.Row)).ToList();

    private readonly record struct FormatRun(int Start, int End, bool Bold, bool Italic, bool Underline, bool Strike,
        bool Code, LinkType LinkType, string? Link);

    /// <summary>The stretches of one character format between two positions.</summary>
    private List<FormatRun> FormatRuns(int from, int to)
    {
        var runs = new List<FormatRun>();
        int at = from;
        while (at < to)
        {
            var range = Doc.GetRange(at, at);
            range.MoveEnd(TextRangeUnit.CharacterFormat, 1);
            int end = Math.Min(Math.Max(range.EndPosition, at + 1), to);
            range = Doc.GetRange(at, end);
            var format = range.CharacterFormat;
            var linkType = format.LinkType;
            string? link = null;
            if (linkType is LinkType.FriendlyLinkName or LinkType.ClientLink)
                link = range.Link?.Trim('"').Replace("%22", "\"", StringComparison.Ordinal);
            runs.Add(new FormatRun(at, end,
                format.Bold == FormatEffect.On, format.Italic == FormatEffect.On,
                format.Underline is not (UnderlineType.None or UnderlineType.Undefined),
                format.Strikethrough == FormatEffect.On,
                IsCodeFontName(format.Name), linkType, link));
            at = end;
        }
        return runs;
    }

    private static bool IsCodeFontName(string? name) =>
        !string.IsNullOrEmpty(name)
        && (name.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || name.Contains("Cascadia", StringComparison.OrdinalIgnoreCase));

    /// <summary>The paragraphs the selection takes in, cut down to what is selected: for Copy.</summary>
    public List<RichParagraph> ReadSelection()
    {
        var selection = Doc.Selection;
        return selection.EndPosition > selection.StartPosition
            ? Read(selection.StartPosition, selection.EndPosition)
            : new List<RichParagraph>();
    }

    /// <summary>The body as it stands now, or the part of it between two positions.</summary>
    public List<RichParagraph> Read(int rangeStart = 0, int rangeEnd = int.MaxValue)
    {
        var raw = Raw();
        var slots = Slots(raw, 0, raw.Length);

        var formats = FormatRuns(0, raw.Length);
        var pictures = PicturePositions(raw);
        var result = new List<RichParagraph>(slots.Count);
        int formatIndex = 0;

        int table = -1, row = 0;
        bool previousWasCell = false;
        FormatEffect rowMark = FormatEffect.Off, tableMark = FormatEffect.Off;

        foreach (var slot in slots)
        {
            var paragraph = new RichParagraph();
            var paragraphFormat = Doc.GetRange(slot.Start, slot.Start).ParagraphFormat;
            paragraph.QuoteDepth = Math.Clamp((int)Math.Round(paragraphFormat.RightIndent), 0, 20);

            // A cell is a paragraph marked "keep with next". A new row is where "no line number"
            // changes; a new table is where "widow control" changes, or after anything not a cell.
            bool isCell = paragraphFormat.KeepWithNext == FormatEffect.On;
            if (isCell)
            {
                var thisRow = paragraphFormat.NoLineNumber;
                var thisTable = paragraphFormat.WidowControl;
                if (!previousWasCell || thisTable != tableMark)
                {
                    table++;
                    row = 0;
                }
                else if (thisRow != rowMark)
                {
                    row++;
                }
                rowMark = thisRow;
                tableMark = thisTable;
                paragraph.Table = table;
                paragraph.Row = row;
            }
            previousWasCell = isCell;

            if (slot.Start + slot.Length < rangeStart || slot.Start >= rangeEnd) continue;
            int begin = Math.Max(slot.Start, rangeStart);
            int end = Math.Min(slot.Start + slot.Length, rangeEnd);
            while (formatIndex < formats.Count && formats[formatIndex].End <= slot.Start) formatIndex++;
            bool allBold = slot.Length > 0;
            for (int f = formatIndex; f < formats.Count && formats[f].Start < end; f++)
            {
                var run = formats[f];
                // A link's address sits in the text, out of sight, in front of its name.
                if (run.LinkType == LinkType.FriendlyLinkAddress) continue;
                int from = Math.Max(run.Start, begin), to = Math.Min(run.End, end);
                if (to <= from) continue;
                string? link = run.LinkType switch
                {
                    LinkType.FriendlyLinkName or LinkType.ClientLink => run.Link,
                    LinkType.AutoLink or LinkType.AutoLinkEmail => AutoLinkAddress(raw[from..to], run.LinkType),
                    _ => null,
                };
                AddRuns(paragraph, raw, from, to, run, string.IsNullOrEmpty(link) ? null : link, pictures);
                if (!run.Bold) allBold = false;
            }

            if (isCell)
            {
                paragraph.HeaderCell = allBold;
                paragraph.CellAlign = paragraphFormat.Alignment switch
                {
                    ParagraphAlignment.Center => "C",
                    ParagraphAlignment.Right => "R",
                    _ => null,
                };
            }
            else
            {
                var style = paragraphFormat.Style;
                if (style is >= ParagraphStyle.Heading1 and <= ParagraphStyle.Heading6)
                    paragraph.Heading = style - ParagraphStyle.Heading1 + 1;
                paragraph.Pre = paragraphFormat.KeepTogether == FormatEffect.On;
                paragraph.Rule = !paragraph.Pre && paragraph.Text == RichBody.RuleText;
                switch (paragraphFormat.ListType)
                {
                    case MarkerType.None or MarkerType.Undefined:
                        break;
                    case MarkerType.Bullet:
                        paragraph.List = RichListKind.Bullet;
                        break;
                    default:
                        paragraph.List = RichListKind.Numbered;
                        break;
                }
                if (paragraph.List != RichListKind.None)
                    paragraph.ListLevel = Math.Clamp(paragraphFormat.ListLevelIndex, 0, 8);
                if (paragraph.Pre)
                {
                    // A code block is its text; fonts inside it are not separate formatting.
                    var text = paragraph.Text;
                    paragraph.Runs.Clear();
                    if (text.Length > 0) paragraph.Runs.Add(new RichRun(text, Code: true));
                }
            }
            result.Add(paragraph);
        }
        // The line Load puts after a table that ends the body is not part of the body.
        if (rangeStart == 0 && rangeEnd == int.MaxValue && result.Count >= 2
            && result[^1] is { IsCell: false, Runs.Count: 0 } && result[^2].IsCell)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static string AutoLinkAddress(string text, LinkType type) =>
        type == LinkType.AutoLinkEmail && !text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            ? "mailto:" + text
            : text;

    private static void AddRuns(RichParagraph paragraph, string raw, int from, int to, FormatRun format, string? link,
        Dictionary<int, ComposeImage> pictures)
    {
        int textStart = from;
        void Flush(int upTo)
        {
            if (upTo <= textStart) return;
            var run = new RichRun(raw[textStart..upTo], format.Bold, format.Italic, format.Underline, format.Strike,
                format.Code, link);
            if (paragraph.Runs.Count > 0 && paragraph.Runs[^1] is { Image: null } last && last.SameFormatAs(run))
                paragraph.Runs[^1] = last with { Text = last.Text + run.Text };
            else
                paragraph.Runs.Add(run);
        }

        for (int i = from; i < to; i++)
        {
            if (raw[i] != ObjectChar) continue;
            Flush(i);
            textStart = i + 1;
            // A picture QuickMail did not put there (it has no record of it) is left out.
            if (pictures.TryGetValue(i, out var info))
                paragraph.Runs.Add(new RichRun(RichBody.ObjectChar, Link: link, Image: info));
        }
        Flush(to);
    }

    /// <summary>
    /// Where each remembered picture is now. A deleted picture leaves its range empty but is not
    /// forgotten: Undo puts the object back, and the picture must then be in the message that is
    /// sent, not only on screen. Every object in the body is a picture QuickMail put there, so
    /// one that no range answers for is a deleted picture come back. It is matched to the nearest
    /// empty range at or before it (a deleted selection leaves its pictures' ranges at its start),
    /// in the order the pictures were remembered.
    /// </summary>
    private Dictionary<int, ComposeImage> PicturePositions(string raw)
    {
        var positions = new Dictionary<int, ComposeImage>();
        foreach (var (range, info) in _images)
        {
            int at = range.StartPosition;
            if (range.EndPosition == at + 1 && at < raw.Length && raw[at] == ObjectChar)
                positions[at] = info;
        }
        if (positions.Count == _images.Count) return positions;
        var orphans = new List<int>();
        for (int i = raw.IndexOf(ObjectChar); i >= 0; i = raw.IndexOf(ObjectChar, i + 1))
            if (!positions.ContainsKey(i)) orphans.Add(i);
        for (int i = 0; i < _images.Count && orphans.Count > 0; i++)
        {
            var (range, info) = _images[i];
            int at = range.StartPosition;
            if (range.EndPosition == at + 1 && at < raw.Length && raw[at] == ObjectChar) continue;
            // The first orphan at or after the empty range (one before it, for a range left
            // just past its picture); failing that, the last one before it.
            int pick = orphans.FindIndex(o => o >= at - 1);
            if (pick < 0) pick = orphans.Count - 1;
            int found = orphans[pick];
            orphans.RemoveAt(pick);
            positions[found] = info;
            _images[i] = (Doc.GetRange(found, found + 1), info);
        }
        return positions;
    }

    /// <summary>The Content-IDs and other sources of the pictures in the body now.</summary>
    public IEnumerable<ComposeImage> Pictures() => PicturePositions(Raw()).Values;

    // ─────────────────────────────── Pictures ────────────────────────────────

    /// <summary>Puts a picture in place of the selection; the caret ends after it.</summary>
    public void InsertImage(ComposeImage info)
    {
        Doc.BeginUndoGroup();
        try
        {
            var selection = Doc.Selection;
            int at = selection.StartPosition;
            InsertImageAt(at, selection.EndPosition, info);
            Doc.Selection.SetRange(at + 1, at + 1);
        }
        finally { Doc.EndUndoGroup(); }
    }

    /// <summary>
    /// The picture just after the caret, else just before it, with where it is; null when there
    /// is none. Just after is where the caret sits when a screen reader reads the picture.
    /// </summary>
    public (int Position, ComposeImage Info)? ImageAtCaret()
    {
        var raw = Raw();
        var pictures = PicturePositions(raw);
        int caret = Doc.Selection.StartPosition;
        if (pictures.TryGetValue(caret, out var after)) return (caret, after);
        if (caret > 0 && pictures.TryGetValue(caret - 1, out var before)) return (caret - 1, before);
        return null;
    }

    /// <summary>True while the picture that was at <paramref name="position"/> is still that picture.</summary>
    public bool IsImageAt(int position, ComposeImage info) =>
        PicturePositions(Raw()).TryGetValue(position, out var now) && ReferenceEquals(now, info);

    public void RemoveImage(int position)
    {
        // The record stays, with an empty range: Undo brings the picture back (PicturePositions).
        Doc.GetRange(position, position + 1).SetText(TextSetOptions.None, string.Empty);
        Doc.Selection.SetRange(position, position);
    }

    /// <summary>
    /// Changes a picture's description. The object cannot be renamed in place, so it is drawn
    /// again with the new alternative text, as one undo step.
    /// </summary>
    public void ReplaceImage(int position, ComposeImage info)
    {
        Doc.BeginUndoGroup();
        try
        {
            // The old record is kept, in front of the new one and with an empty range, so that
            // Undo, which puts the old object back, finds the picture it was.
            int old = _images.FindIndex(i => i.Range.StartPosition == position && i.Range.EndPosition == position + 1);
            var before = old >= 0 ? _images[old].Info : null;
            if (old >= 0) _images.RemoveAt(old);
            InsertImageAt(position, position + 1, info);
            if (before is not null) _images.Insert(_images.Count - 1, (Doc.GetRange(position, position), before));
            Doc.Selection.SetRange(position, position);
        }
        finally { Doc.EndUndoGroup(); }
    }

    /// <summary>Draws pictures whose bytes have arrived since the body was loaded (a reply's quoted pictures).</summary>
    public void RedrawPictures(Func<ComposeImage, bool> arrived)
    {
        var raw = Raw();
        var selection = Doc.Selection;
        int start = selection.StartPosition, end = selection.EndPosition;
        foreach (var (position, info) in PicturePositions(raw).OrderByDescending(p => p.Key).ToList())
        {
            if (!arrived(info)) continue;
            _images.RemoveAll(i => i.Range.StartPosition == position && i.Range.EndPosition == position + 1);
            InsertImageAt(position, position + 1, info);
        }
        Doc.Selection.SetRange(start, end);
    }

    // ─────────────────────────── Character formatting ────────────────────────

    // What has been switched on for the next typing (Ctrl+B with nothing selected), and where
    // the caret was. The control resets the caret's format on its own schedule: when it finishes
    // loading, and some time after a body has been loaded into it. A command given in that time
    // was undone with nothing said, and the text typed next went in plain. So the wish is
    // remembered, and put back just before the next letter is typed if the caret has not moved.
    private (int Position, FormatEffect Bold, FormatEffect Italic, UnderlineType Underline, FormatEffect Strike)? _typingFormat;

    private void RememberTypingFormat()
    {
        var selection = Doc.Selection;
        if (selection.StartPosition != selection.EndPosition)
        {
            _typingFormat = null;
            return;
        }
        var format = selection.CharacterFormat;
        _typingFormat = (selection.StartPosition, format.Bold, format.Italic, format.Underline, format.Strikethrough);
    }

    /// <summary>
    /// Call before a typed character goes in. If a formatting command was given at this caret
    /// position and the control has since dropped it, it is put back.
    /// </summary>
    public void EnsureTypingFormat()
    {
        if (_typingFormat is not { } wanted) return;
        var selection = Doc.Selection;
        if (selection.StartPosition != selection.EndPosition || selection.StartPosition != wanted.Position)
        {
            _typingFormat = null; // the caret moved: what is there now decides
            return;
        }
        var format = selection.CharacterFormat;
        if (format.Bold != wanted.Bold) format.Bold = wanted.Bold;
        if (format.Italic != wanted.Italic) format.Italic = wanted.Italic;
        if (format.Underline != wanted.Underline) format.Underline = wanted.Underline;
        if (format.Strikethrough != wanted.Strike) format.Strikethrough = wanted.Strike;
    }

    public bool ToggleBold()
    {
        var format = Doc.Selection.CharacterFormat;
        bool on = format.Bold != FormatEffect.On;
        format.Bold = on ? FormatEffect.On : FormatEffect.Off;
        RememberTypingFormat();
        return on;
    }

    public bool ToggleItalic()
    {
        var format = Doc.Selection.CharacterFormat;
        bool on = format.Italic != FormatEffect.On;
        format.Italic = on ? FormatEffect.On : FormatEffect.Off;
        RememberTypingFormat();
        return on;
    }

    public bool ToggleUnderline()
    {
        var format = Doc.Selection.CharacterFormat;
        bool on = format.Underline is UnderlineType.None or UnderlineType.Undefined;
        format.Underline = on ? UnderlineType.Single : UnderlineType.None;
        RememberTypingFormat();
        return on;
    }

    public bool ToggleStrikethrough()
    {
        var format = Doc.Selection.CharacterFormat;
        bool on = format.Strikethrough != FormatEffect.On;
        format.Strikethrough = on ? FormatEffect.On : FormatEffect.Off;
        RememberTypingFormat();
        return on;
    }

    // ─────────────────────────── Paragraph formatting ────────────────────────

    /// <summary>What a paragraph is, as the commands and announcements need it.</summary>
    /// <param name="TableRow">For a cell, its row counted from 1; otherwise 0.</param>
    /// <param name="TableColumn">For a cell, its column counted from 1; otherwise 0.</param>
    internal readonly record struct BlockInfo(int Start, int End, int Heading, bool Pre, RichListKind List, int ListLevel,
        int QuoteDepth, bool InTable, int TableRow = 0, int TableColumn = 0);

    /// <summary>The paragraph the caret is in.</summary>
    public BlockInfo BlockAtCaret() => BlockAt(Doc.Selection.StartPosition);

    private BlockInfo BlockAt(int position)
    {
        var range = WholeParagraph(position);
        var format = range.ParagraphFormat;
        var style = format.Style;
        int heading = style is >= ParagraphStyle.Heading1 and <= ParagraphStyle.Heading6 ? style - ParagraphStyle.Heading1 + 1 : 0;
        var list = format.ListType switch
        {
            MarkerType.None or MarkerType.Undefined => RichListKind.None,
            MarkerType.Bullet => RichListKind.Bullet,
            _ => RichListKind.Numbered,
        };
        bool inTable = format.KeepWithNext == FormatEffect.On;
        return new BlockInfo(range.StartPosition, range.EndPosition, heading, format.KeepTogether == FormatEffect.On,
            list, list == RichListKind.None ? 0 : Math.Clamp(format.ListLevelIndex, 0, 8),
            Math.Clamp((int)Math.Round(format.RightIndent), 0, 20), inTable);
    }

    /// <summary>
    /// The whole paragraph at a position. RichEdit's own paragraph unit stops at a line break
    /// inside a paragraph (a second line in a table cell or a code block), which made that line
    /// count as a cell of its own and ending a code block act on its last line alone.
    /// </summary>
    private ITextRange WholeParagraph(int position)
    {
        var range = Doc.GetRange(position, position);
        range.Expand(TextRangeUnit.Paragraph);
        int start = range.StartPosition, end = range.EndPosition;
        for (int guard = 0; start > 0 && guard < 10_000 && CharAt(start - 1) == '\v'; guard++)
        {
            var before = Doc.GetRange(start - 1, start - 1);
            before.Expand(TextRangeUnit.Paragraph);
            if (before.StartPosition >= start) break;
            start = before.StartPosition;
        }
        for (int guard = 0; end > start && guard < 10_000 && CharAt(end - 1) == '\v'; guard++)
        {
            var after = Doc.GetRange(end, end);
            after.Expand(TextRangeUnit.Paragraph);
            if (after.EndPosition <= end) break;
            end = after.EndPosition;
        }
        return Doc.GetRange(start, end);
    }

    private char CharAt(int position)
    {
        Doc.GetRange(position, position + 1).GetText(TextGetOptions.None, out var text);
        return string.IsNullOrEmpty(text) ? '\0' : text[0];
    }

    /// <summary>
    /// The caret's paragraph with, for a table cell, its row and column: found by walking back
    /// through the cells before it, so only asked where it will be spoken.
    /// </summary>
    public BlockInfo BlockAtCaretWithTablePosition()
    {
        var block = BlockAtCaret();
        if (!block.InTable) return block;

        var here = Doc.GetRange(block.Start, block.Start).ParagraphFormat;
        var tableMark = here.WidowControl;
        var rowMark = here.NoLineNumber;
        int row = 1, column = 1;
        bool inFirstRow = true;
        int at = block.Start;
        for (int guard = 0; at > 0 && guard < 5000; guard++)
        {
            var previous = WholeParagraph(at - 1);
            var format = previous.ParagraphFormat;
            if (format.KeepWithNext != FormatEffect.On || format.WidowControl != tableMark) break;
            var mark = format.NoLineNumber;
            if (mark != rowMark)
            {
                row++;
                rowMark = mark;
                inFirstRow = false;
            }
            else if (inFirstRow)
            {
                column++;
            }
            if (previous.StartPosition >= at) break;
            at = previous.StartPosition;
        }
        return block with { TableRow = row, TableColumn = column };
    }

    /// <summary>The paragraphs the selection touches.</summary>
    public List<BlockInfo> SelectedBlocks()
    {
        var selection = Doc.Selection;
        int end = Math.Max(selection.EndPosition, selection.StartPosition);
        var blocks = new List<BlockInfo>();
        int at = selection.StartPosition;
        while (blocks.Count < 10_000)
        {
            var block = BlockAt(at);
            blocks.Add(block);
            // A selection that ends at the very start of a paragraph does not take that paragraph in.
            if (block.End >= end || block.End <= at) break;
            at = block.End;
        }
        return blocks;
    }

    /// <summary>Runs a change to several paragraphs as one undo step, keeping the selection.</summary>
    private void Edit(Action change)
    {
        var selection = Doc.Selection;
        int start = selection.StartPosition, end = selection.EndPosition;
        Doc.BeginUndoGroup();
        try { change(); }
        finally { Doc.EndUndoGroup(); }
        Doc.Selection.SetRange(start, end);
    }

    private ITextRange RangeOf(BlockInfo block) => Doc.GetRange(block.Start, block.End);

    /// <summary>Toggles a heading level on the selected paragraphs; returns true when it was turned on.</summary>
    public bool ToggleHeading(int level)
    {
        var blocks = SelectedBlocks().Where(b => !b.InTable).ToList();
        if (blocks.Count == 0) return false;
        bool turnOff = blocks.All(b => b.Heading == level);
        Edit(() => { foreach (var block in blocks) SetHeading(RangeOf(block), turnOff ? 0 : level); });
        return !turnOff;
    }

    /// <summary>Sets a heading level (0 for none) on the selected paragraphs, without toggling.</summary>
    public void SetHeadingLevel(int level)
    {
        var blocks = SelectedBlocks().Where(b => !b.InTable).ToList();
        Edit(() =>
        {
            foreach (var block in blocks)
                if (level > 0 || block.Heading > 0) SetHeading(RangeOf(block), level);
        });
    }

    /// <summary>
    /// Puts the selected paragraphs one level further into a quote, or, when they are all in one
    /// already, takes them one level out. Returns the first paragraph's depth afterwards and
    /// whether the command was the "out" kind.
    /// </summary>
    public (int Depth, bool TurnedOff) ToggleQuote()
    {
        var blocks = SelectedBlocks();
        if (blocks.Count == 0) return (0, false);
        bool turnOff = blocks.All(b => b.QuoteDepth > 0);
        Edit(() =>
        {
            foreach (var block in blocks)
                SetQuoteAndList(RangeOf(block), block.QuoteDepth + (turnOff ? -1 : 1), block.List, block.ListLevel);
        });
        return (blocks[0].QuoteDepth + (turnOff ? -1 : 1), turnOff);
    }

    public void SetQuoteDepth(int depth, bool onlyWhenShallower = false)
    {
        var blocks = SelectedBlocks();
        Edit(() =>
        {
            foreach (var block in blocks)
            {
                if (onlyWhenShallower && block.QuoteDepth >= depth) continue;
                SetQuoteAndList(RangeOf(block), depth, block.List, block.ListLevel);
            }
        });
    }

    /// <summary>Makes the selected paragraphs plain: no heading, no code block, out of every quote. Lists stay.</summary>
    public void SetNormal()
    {
        var blocks = SelectedBlocks().Where(b => !b.InTable).ToList();
        Edit(() =>
        {
            foreach (var block in blocks)
            {
                var range = RangeOf(block);
                if (block.Heading > 0) SetHeading(range, 0);
                if (block.Pre)
                {
                    range.ParagraphFormat.KeepTogether = FormatEffect.Off;
                    range.CharacterFormat.Name = BaseFont;
                }
                SetQuoteAndList(range, 0, block.List, block.ListLevel);
            }
        });
    }

    /// <summary>Turns a list of one kind on for the selected paragraphs, or off when the first is one already.</summary>
    public bool ToggleList(bool ordered)
    {
        var kind = ordered ? RichListKind.Numbered : RichListKind.Bullet;
        var blocks = SelectedBlocks().Where(b => !b.InTable).ToList();
        if (blocks.Count == 0) return false;
        bool turnOff = blocks[0].List == kind;
        Edit(() =>
        {
            foreach (var block in blocks)
                SetQuoteAndList(RangeOf(block), block.QuoteDepth, turnOff ? RichListKind.None : kind,
                    block.List == RichListKind.None ? 0 : block.ListLevel);
        });
        return !turnOff;
    }

    /// <summary>Moves the caret's list item one level in or out. False when the caret is not in a list.</summary>
    public bool IndentListItem(int step)
    {
        var blocks = SelectedBlocks();
        if (blocks.Count == 0 || blocks[0].List == RichListKind.None) return false;
        Edit(() =>
        {
            foreach (var block in blocks.Where(b => b.List != RichListKind.None))
                SetQuoteAndList(RangeOf(block), block.QuoteDepth, block.List, block.ListLevel + step);
        });
        return true;
    }

    /// <summary>Character formatting off across the selection, and its paragraphs made plain.</summary>
    public void ClearFormatting()
    {
        Doc.BeginUndoGroup();
        try
        {
            var format = Doc.Selection.CharacterFormat;
            format.Bold = FormatEffect.Off;
            format.Italic = FormatEffect.Off;
            format.Underline = UnderlineType.None;
            format.Strikethrough = FormatEffect.Off;
            format.Name = BaseFont;
            SetNormal();
        }
        finally { Doc.EndUndoGroup(); }
    }

    // ─────────────────────────────── Links ───────────────────────────────────

    public string SelectedText()
    {
        Doc.Selection.GetText(TextGetOptions.NoHidden, out var text);
        return text ?? string.Empty;
    }

    /// <summary>Puts a link in place of the selection; the caret ends after it.</summary>
    public void InsertLink(string displayText, string address)
    {
        Doc.BeginUndoGroup();
        try
        {
            var selection = Doc.Selection;
            int at = selection.StartPosition;
            var range = Doc.GetRange(at, selection.EndPosition);
            range.SetText(TextSetOptions.None, RichBody.Clean(displayText));
            range = Doc.GetRange(at, at + displayText.Length);
            SetLink(range, address);
            // The address is now in the text in front of the name; the range has grown to hold it.
            int end = Math.Max(range.EndPosition, at + displayText.Length);
            Doc.Selection.SetRange(end, end);
            // What is typed next is not part of the link.
            Doc.Selection.CharacterFormat.Underline = UnderlineType.None;
        }
        finally { Doc.EndUndoGroup(); }
    }

    /// <summary>The address of the link the caret is in, or null.</summary>
    public string? LinkAtCaret()
    {
        int caret = Doc.Selection.StartPosition;
        var raw = Raw();
        foreach (int at in new[] { caret, caret - 1 })
        {
            if (at < 0 || at >= raw.Length) continue;
            var range = Doc.GetRange(at, at + 1);
            var type = range.CharacterFormat.LinkType;
            if (type is LinkType.FriendlyLinkName or LinkType.ClientLink)
                return range.Link?.Trim('"').Replace("%22", "\"", StringComparison.Ordinal);
            if (type is LinkType.AutoLink or LinkType.AutoLinkEmail)
            {
                range.Expand(TextRangeUnit.Link);
                range.GetText(TextGetOptions.NoHidden, out var text);
                return AutoLinkAddress(text ?? string.Empty, type);
            }
        }
        return null;
    }

    // ─────────────────────────── Formatting, described ───────────────────────

    /// <summary>"on", "off" or "mixed" for one character format across the selection.</summary>
    public (string Bold, string Italic, string Underline, string Strike, bool Code) CharacterState()
    {
        var format = Doc.Selection.CharacterFormat;
        static string Effect(FormatEffect e) => e switch { FormatEffect.On => "on", FormatEffect.Off => "off", _ => "mixed" };
        var underline = format.Underline switch
        {
            UnderlineType.None => "off",
            UnderlineType.Undefined => "mixed",
            _ => "on",
        };
        return (Effect(format.Bold), Effect(format.Italic), underline, Effect(format.Strikethrough), IsCodeFontName(format.Name));
    }

    // ─────────────────────────────── Enter ───────────────────────────────────

    /// <summary>What <see cref="HandleEnter"/> did, for the caller to announce.</summary>
    internal enum EnterResult { NotHandled, LeftQuote, HeadingBreak, CodeLine, LeftCode, CellLine, LeftList }

    /// <summary>
    /// Enter where RichEdit's own paragraph break is wrong. Call with nothing selected.
    /// <list type="bullet">
    /// <item>On an empty line in a quote, Enter takes the line one level out of the quote.</item>
    /// <item>At the end of a heading, the new line is normal text, as in Word; a heading split
    /// elsewhere leaves both halves headings. Left alone, RichEdit carries the heading on.</item>
    /// <item>In a code block, Enter starts a new line of the same block; on an empty last line it
    /// ends the block.</item>
    /// <item>In a table cell, Enter starts a new line in the cell.</item>
    /// </list>
    /// </summary>
    public EnterResult HandleEnter()
    {
        var selection = Doc.Selection;
        if (selection.EndPosition != selection.StartPosition) return EnterResult.NotHandled;
        int caret = selection.StartPosition;
        var block = BlockAt(caret);
        if (block.InTable)
        {
            // A new paragraph here would be a new cell. A line break keeps the text in this one.
            Doc.GetRange(caret, caret).SetText(TextSetOptions.None, RichBody.LineBreak);
            Doc.Selection.SetRange(caret + 1, caret + 1);
            return EnterResult.CellLine;
        }
        bool empty = block.End - block.Start <= 1;
        bool atEnd = caret >= block.End - 1;

        if (block.Pre)
        {
            var raw = Raw();
            bool onEmptyLastLine = atEnd && caret > block.Start && raw[caret - 1] == '\v';
            Doc.BeginUndoGroup();
            try
            {
                if (onEmptyLastLine || empty)
                {
                    // End the block: the empty line becomes a plain paragraph after it.
                    if (onEmptyLastLine) Doc.GetRange(caret - 1, caret).SetText(TextSetOptions.None, "\r");
                    var after = Doc.GetRange(caret, caret);
                    after.Expand(TextRangeUnit.Paragraph);
                    after.ParagraphFormat.KeepTogether = FormatEffect.Off;
                    after.CharacterFormat.Name = BaseFont;
                    Doc.Selection.SetRange(caret, caret);
                    Doc.Selection.CharacterFormat.Name = BaseFont;
                    return EnterResult.LeftCode;
                }
                Doc.GetRange(caret, caret).SetText(TextSetOptions.None, "\v");
                Doc.Selection.SetRange(caret + 1, caret + 1);
                return EnterResult.CodeLine;
            }
            finally { Doc.EndUndoGroup(); }
        }

        if (empty && block.List != RichListKind.None)
        {
            // Enter on an empty list item ends the list there. The control's own Enter does that
            // and adds a paragraph as well, which left a blank line after every list.
            if (block.ListLevel > 0)
                SetQuoteAndList(RangeOf(block), block.QuoteDepth, block.List, block.ListLevel - 1);
            else
                SetQuoteAndList(RangeOf(block), block.QuoteDepth, RichListKind.None, 0);
            return EnterResult.LeftList;
        }

        if (empty && block.QuoteDepth > 0)
        {
            SetQuoteAndList(RangeOf(block), block.QuoteDepth - 1, block.List, block.ListLevel);
            return EnterResult.LeftQuote;
        }

        if (block.Heading == 0) return EnterResult.NotHandled;
        bool atStart = !atEnd && caret == block.Start;
        Doc.BeginUndoGroup();
        try
        {
            Doc.GetRange(caret, caret).SetText(TextSetOptions.None, "\r");
            var before = Doc.GetRange(block.Start, block.Start);
            before.Expand(TextRangeUnit.Paragraph);
            var after = Doc.GetRange(caret + 1, caret + 1);
            after.Expand(TextRangeUnit.Paragraph);
            SetHeading(after, atEnd ? 0 : block.Heading);
            SetHeading(before, atStart ? 0 : block.Heading);
            Doc.Selection.SetRange(caret + 1, caret + 1);
            if (atEnd)
            {
                // The new line is empty; what is typed on it takes the caret's format.
                var typing = Doc.Selection.CharacterFormat;
                typing.Size = BaseSize;
                typing.Bold = FormatEffect.Off;
            }
        }
        finally { Doc.EndUndoGroup(); }
        return EnterResult.HeadingBreak;
    }

    // ─────────────────────────────── Text for spelling ───────────────────────

    /// <summary>
    /// The document's text with everything that is not the user's words blanked out, character
    /// for character, so positions still line up: a link's address (it sits in the text, out of
    /// sight), table marks, and picture characters.
    /// </summary>
    public string MaskedText()
    {
        var raw = Raw();
        if (raw.Length == 0) return raw;
        char[]? masked = null;
        void Blank(int at)
        {
            masked ??= raw.ToCharArray();
            masked[at] = ' ';
        }

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c is RowStart or RowEnd or ObjectChar) Blank(i);
            else if (c == CellEnd) { masked ??= raw.ToCharArray(); masked[i] = '\r'; }
        }

        // A link's address is 'HYPERLINK "..."' in the text, found by its character format.
        int search = 0;
        while (search < raw.Length)
        {
            int at = raw.IndexOf("HYPERLINK \"", search, StringComparison.Ordinal);
            if (at < 0) break;
            if (Doc.GetRange(at, at + 1).CharacterFormat.LinkType == LinkType.FriendlyLinkAddress)
            {
                int close = raw.IndexOf('"', at + 11);
                int end = close < 0 ? raw.Length : close + 1;
                for (int i = at; i < end; i++) Blank(i);
                search = end;
            }
            else
            {
                search = at + 11;
            }
        }
        return masked is null ? raw : new string(masked);
    }
}
