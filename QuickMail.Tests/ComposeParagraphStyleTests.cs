using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #729 phase 1: headings 4–6, Normal text, Quote, nested quotes, and the
/// Enter behaviour around headings and quotes. Quotes are Section containers in
/// the rich editor, so these cover the document operations, the converter's
/// HTML / Markdown / plain-text output, and the compose window's commands.
/// </summary>
[Collection("WpfTests")]
public class ComposeParagraphStyleTests
{
    private static string Html(FlowDocument doc) => RichTextDocumentConverter.ToHtml(doc);

    private static Paragraph ParagraphWithText(FlowDocument doc, string text) =>
        RichTextBlockEditing.EnumerateBlocks(doc.Blocks).OfType<Paragraph>()
            .First(p => TextOf(p) == text);

    // The paragraph's own text: a TextRange over a list item includes its bullet.
    private static string TextOf(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // ── Converter: quotes are containers ─────────────────────────────────────

    [StaFact]
    public void NestedQuotes_SurviveHtmlRoundTrip()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            "<blockquote><p>outer</p><blockquote><p>inner</p></blockquote></blockquote>");

        Assert.Equal(1, RichTextDocumentConverter.QuoteDepth(ParagraphWithText(doc, "outer")));
        Assert.Equal(2, RichTextDocumentConverter.QuoteDepth(ParagraphWithText(doc, "inner")));
        Assert.Equal(
            "<blockquote>\n<p>outer</p>\n<blockquote>\n<p>inner</p>\n</blockquote>\n</blockquote>",
            Html(doc));
    }

    [StaFact]
    public void HeadingInsideQuote_StaysAHeading()
    {
        var doc = RichTextDocumentConverter.FromHtml("<blockquote><h2>Title</h2><p>body</p></blockquote>");

        Assert.Equal("<blockquote>\n<h2>Title</h2>\n<p>body</p>\n</blockquote>", Html(doc));
        Assert.Equal("> ## Title\n>\n> body", RichTextDocumentConverter.ToMarkdown(doc));
    }

    [StaFact]
    public void ListInsideQuote_KeepsTheQuoteInMarkdown()
    {
        var doc = RichTextDocumentConverter.FromHtml("<blockquote><ul><li>a</li><li>b</li></ul></blockquote>");

        Assert.Equal("> - a\n> - b", RichTextDocumentConverter.ToMarkdown(doc));
    }

    [StaFact]
    public void PlainText_MarksQuotedLines()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            "<p>Reply</p><blockquote><p>one</p><blockquote><p>two</p></blockquote></blockquote>");

        Assert.Equal("Reply\n\n> one\n>\n> > two", RichTextDocumentConverter.ToPlainText(doc));
    }

    [StaTheory]
    [InlineData("> - one\n> - two")]
    [InlineData("> ## Quoted heading")]
    [InlineData("> outer\n>\n> > inner")]
    public void QuoteMarkdown_RoundTripsExactly(string markdown)
    {
        var svc = new MarkdownService();
        var doc = RichTextDocumentConverter.FromHtml(svc.ToHtml(markdown));
        Assert.Equal(markdown, RichTextDocumentConverter.ToMarkdown(doc));
    }

    // ── Document operations ──────────────────────────────────────────────────

    private static FlowDocument Doc(string html) => RichTextDocumentConverter.FromHtml(html);

    [StaFact]
    public void AddQuote_WrapsTheSelectedParagraphsOnly()
    {
        var doc = Doc("<p>a</p><p>b</p><p>c</p><p>d</p>");

        Assert.True(RichTextBlockEditing.AddQuote(ParagraphWithText(doc, "b"), ParagraphWithText(doc, "c")));

        Assert.Equal("<p>a</p>\n<blockquote>\n<p>b</p>\n<p>c</p>\n</blockquote>\n<p>d</p>", Html(doc));
    }

    [StaFact]
    public void AddQuote_OnAListItem_QuotesTheWholeList()
    {
        var doc = Doc("<p>a</p><ul><li>one</li><li>two</li></ul>");

        RichTextBlockEditing.AddQuote(ParagraphWithText(doc, "two"), ParagraphWithText(doc, "two"));

        Assert.Equal("<p>a</p>\n<blockquote>\n<ul>\n<li>one</li>\n<li>two</li>\n</ul>\n</blockquote>", Html(doc));
    }

    [StaFact]
    public void AddQuote_InsideAQuote_Nests()
    {
        var doc = Doc("<blockquote><p>a</p><p>b</p></blockquote>");

        RichTextBlockEditing.AddQuote(ParagraphWithText(doc, "b"), ParagraphWithText(doc, "b"));

        Assert.Equal(2, RichTextDocumentConverter.QuoteDepth(ParagraphWithText(doc, "b")));
        Assert.Equal(1, RichTextDocumentConverter.QuoteDepth(ParagraphWithText(doc, "a")));
    }

    [StaFact]
    public void RemoveQuote_FromTheMiddle_SplitsTheQuote()
    {
        var doc = Doc("<blockquote><p>a</p><p>b</p><p>c</p></blockquote>");

        Assert.True(RichTextBlockEditing.RemoveQuote(ParagraphWithText(doc, "b"), ParagraphWithText(doc, "b")));

        Assert.Equal(
            "<blockquote>\n<p>a</p>\n</blockquote>\n<p>b</p>\n<blockquote>\n<p>c</p>\n</blockquote>",
            Html(doc));
    }

    [StaFact]
    public void RemoveQuote_TakesOffOneLevel()
    {
        var doc = Doc("<blockquote><blockquote><p>deep</p></blockquote></blockquote>");
        var deep = ParagraphWithText(doc, "deep");

        RichTextBlockEditing.RemoveQuote(deep, deep);

        Assert.Equal(1, RichTextDocumentConverter.QuoteDepth(deep));
        Assert.Equal("<blockquote>\n<p>deep</p>\n</blockquote>", Html(doc));
    }

    [StaFact]
    public void RemoveQuote_WhenNotQuoted_ReturnsFalse()
    {
        var doc = Doc("<p>a</p>");
        var a = ParagraphWithText(doc, "a");
        Assert.False(RichTextBlockEditing.RemoveQuote(a, a));
    }

    [StaFact]
    public void RemoveAllQuotes_LeavesNoEmptyQuotesBehind()
    {
        var doc = Doc("<blockquote><blockquote><p>deep</p></blockquote></blockquote>");

        RichTextBlockEditing.RemoveAllQuotes(ParagraphWithText(doc, "deep"));

        Assert.Equal("<p>deep</p>", Html(doc));
    }

    [StaFact]
    public void SetHeading_ZeroTurnsACodeBlockIntoNormalText()
    {
        var doc = Doc("<pre><code>x = 1</code></pre>");
        var code = doc.Blocks.OfType<Paragraph>().Single();

        RichTextBlockEditing.SetHeading(code, 0);

        Assert.Equal("<p>x = 1</p>", Html(doc));
    }

    [StaFact]
    public void StyleOf_ReportsHeadingQuoteAndNormal()
    {
        var doc = Doc("<h5>h</h5><blockquote><p>q</p></blockquote><p>n</p><pre><code>c</code></pre>");

        Assert.Equal(ComposeParagraphStyle.Heading5, RichTextBlockEditing.StyleOf(ParagraphWithText(doc, "h")));
        Assert.Equal(ComposeParagraphStyle.Quote, RichTextBlockEditing.StyleOf(ParagraphWithText(doc, "q")));
        Assert.Equal(ComposeParagraphStyle.Normal, RichTextBlockEditing.StyleOf(ParagraphWithText(doc, "n")));
        Assert.Null(RichTextBlockEditing.StyleOf(ParagraphWithText(doc, "c")));
    }

    [StaFact]
    public void ParagraphsInRange_ReachesIntoQuotes()
    {
        var doc = Doc("<p>a</p><blockquote><p>b</p><blockquote><p>c</p></blockquote></blockquote><p>d</p>");

        var range = RichTextBlockEditing.ParagraphsInRange(doc,
            ParagraphWithText(doc, "a").ContentStart, ParagraphWithText(doc, "d").ContentEnd);

        Assert.Equal(["a", "b", "c", "d"],
            range.Select(TextOf).ToArray());
    }

    // ── Labels ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Normal text", 0, "Normal text")]
    [InlineData("Normal text", 1, "Quote")]
    [InlineData("Normal text", 2, "Quote, level 2")]
    [InlineData("Heading 2", 1, "Heading 2, quote")]
    [InlineData("Bullet list item", 3, "Bullet list item, quote level 3")]
    public void BlockLabel_AddsTheQuote(string label, int depth, string expected) =>
        Assert.Equal(expected, BlockLabels.WithQuote(label, depth));

    // ── The compose window's commands ────────────────────────────────────────

    private static (ComposeWindow Window, RichTextBox Editor) HtmlWindow(string plainBody)
    {
        var vm = new ComposeViewModel(new StubSmtpService(), new StubAccountService(),
            new StubCredentialService(), new StubImapMailService(), new StubTemplateService());
        var window = new ComposeWindow(vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ConfirmSaveOnClose = null,
        };
        // Shown, because editing commands such as EnterParagraphBreak do nothing
        // in a RichTextBox that has never been laid out.
        window.Show();
        vm.Body = plainBody;
        vm.SetMode(ComposeMode.Html);
        window.UpdateLayout();
        var editor = window.FindName("RichBodyBox") as RichTextBox;
        Assert.NotNull(editor);
        return (window, editor!);
    }

    private static void PutCaret(RichTextBox editor, string paragraphText, bool atEnd)
    {
        var p = ParagraphWithText(editor.Document, paragraphText);
        editor.CaretPosition = atEnd ? p.ContentEnd : p.ContentStart;
    }

    [StaFact]
    public void Heading4To6_AreAppliedAndToggleOff()
    {
        var (window, editor) = HtmlWindow("Title");
        try
        {
            PutCaret(editor, "Title", atEnd: false);
            window.ApplyHeading(6);
            Assert.Equal("<h6>Title</h6>", Html(editor.Document));
            window.ApplyHeading(6);
            Assert.Equal("<p>Title</p>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ToggleQuote_OnThenOff()
    {
        var (window, editor) = HtmlWindow("one\n\ntwo");
        try
        {
            PutCaret(editor, "two", atEnd: true);
            window.ToggleQuote();
            Assert.Equal("<p>one</p>\n<blockquote>\n<p>two</p>\n</blockquote>", Html(editor.Document));
            // The caret stays in the text it was in.
            Assert.Equal("two", new TextRange(editor.CaretPosition.Paragraph!.ContentStart,
                editor.CaretPosition.Paragraph.ContentEnd).Text);

            window.ToggleQuote();
            Assert.Equal("<p>one</p>\n<p>two</p>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ToggleQuote_IsOneUndoStep()
    {
        var (window, editor) = HtmlWindow("one\n\ntwo");
        try
        {
            PutCaret(editor, "two", atEnd: true);
            window.ToggleQuote();
            Assert.True(editor.Undo());
            Assert.Equal("<p>one</p>\n<p>two</p>", Html(editor.Document));
            Assert.True(editor.Redo());
            Assert.Equal("<p>one</p>\n<blockquote>\n<p>two</p>\n</blockquote>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void UndoingAHeading_AlsoUndoesWhatIsSent()
    {
        // WPF's undo restores the heading's size but not the tag the converter
        // sends; the window puts the tag back in step after Undo and Redo.
        var (window, editor) = HtmlWindow("two");
        try
        {
            PutCaret(editor, "two", atEnd: true);
            window.ApplyHeading(2);
            Assert.True(editor.Undo());
            Assert.Equal("<p>two</p>", Html(editor.Document));
            Assert.True(editor.Redo());
            Assert.Equal("<h2>two</h2>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ReconcileHeadingTags_LeavesCodeBlocksAlone()
    {
        var doc = Doc("<pre><code>x</code></pre><h3>t</h3>");
        var before = Html(doc);
        RichTextBlockEditing.ReconcileHeadingTags(doc);
        Assert.Equal(before, Html(doc));
    }

    [StaFact]
    public void NormalText_RemovesHeadingAndQuote()
    {
        var (window, editor) = HtmlWindow("> quoted");
        try
        {
            PutCaret(editor, "quoted", atEnd: false);
            window.ApplyHeading(2);
            window.SetNormalText();
            Assert.Equal("<p>quoted</p>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ClearFormatting_ClearsEveryParagraphInTheSelection()
    {
        var (window, editor) = HtmlWindow("# one\n\n## two\n\n### three");
        try
        {
            // Markdown syntax is plain text here; make real headings first.
            foreach (var (text, level) in new[] { ("# one", 1), ("## two", 2), ("### three", 3) })
            {
                PutCaret(editor, text, atEnd: false);
                window.ApplyHeading(level);
            }
            editor.Selection.Select(ParagraphWithText(editor.Document, "# one").ContentStart,
                ParagraphWithText(editor.Document, "### three").ContentEnd);

            window.ClearFormatting();

            Assert.DoesNotContain("<h", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void EnterAtEndOfHeading_StartsNormalText()
    {
        var (window, editor) = HtmlWindow("Title");
        try
        {
            PutCaret(editor, "Title", atEnd: false);
            window.ApplyHeading(2);
            PutCaret(editor, "Title", atEnd: true);

            Assert.True(window.HandleRichEnter());
            editor.CaretPosition.InsertTextInRun("body");

            Assert.Equal("<h2>Title</h2>\n<p>body</p>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void EnterInMiddleOfHeading_KeepsBothHalvesHeadings()
    {
        var (window, editor) = HtmlWindow("TitleRest");
        try
        {
            PutCaret(editor, "TitleRest", atEnd: false);
            window.ApplyHeading(3);
            var p = ParagraphWithText(editor.Document, "TitleRest");
            editor.CaretPosition = p.ContentStart.GetPositionAtOffset(1 + "Title".Length)!;

            Assert.True(window.HandleRichEnter());

            Assert.Equal("<h3>Title</h3>\n<h3>Rest</h3>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void EnterOnEmptyQuotedLine_EndsTheQuote()
    {
        var (window, editor) = HtmlWindow("> quoted");
        try
        {
            editor.Focus(); // editing commands act only on a focused editor
            PutCaret(editor, "quoted", atEnd: true);
            // Enter inside a quote continues it — the default paragraph break.
            EditingCommands.EnterParagraphBreak.Execute(null, editor);
            Assert.Equal(1, RichTextDocumentConverter.QuoteDepth(editor.CaretPosition.Paragraph));

            // Enter again on the now-empty quoted line ends the quote.
            Assert.True(window.HandleRichEnter());
            Assert.Equal(0, RichTextDocumentConverter.QuoteDepth(editor.CaretPosition.Paragraph));
            Assert.Equal("<blockquote>\n<p>quoted</p>\n</blockquote>\n<p></p>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void EnterOnOrdinaryParagraph_IsLeftToTheEditor()
    {
        var (window, editor) = HtmlWindow("plain");
        try
        {
            PutCaret(editor, "plain", atEnd: true);
            Assert.False(window.HandleRichEnter());
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void FormattingParts_ReportTheLinkAndItsAddress()
    {
        var (window, editor) = HtmlWindow("x");
        try
        {
            RichTextDocumentConverter.LoadInto(editor, "<p><a href=\"https://example.com/\">site</a></p>");
            var link = RichTextBlockEditing.EnumerateBlocks(editor.Document.Blocks).OfType<Paragraph>()
                .First().Inlines.OfType<Hyperlink>().Single();
            editor.CaretPosition = link.ContentStart.GetPositionAtOffset(2)!;

            Assert.Contains("Link, https://example.com/", window.GetFormattingParts());
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ParagraphStyleBox_FollowsTheCaret()
    {
        var (window, editor) = HtmlWindow("> quoted\n\nplain");
        try
        {
            var combo = window.FindName("ParagraphStyleCombo") as ComboBox;
            Assert.NotNull(combo);

            PutCaret(editor, "quoted", atEnd: false);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal((int)ComposeParagraphStyle.Quote, combo!.SelectedIndex);

            PutCaret(editor, "plain", atEnd: false);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal((int)ComposeParagraphStyle.Normal, combo.SelectedIndex);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ParagraphStyleBox_ChoiceAppliesToTheParagraph()
    {
        var (window, editor) = HtmlWindow("text");
        try
        {
            var combo = window.FindName("ParagraphStyleCombo") as ComboBox;
            Assert.NotNull(combo);
            PutCaret(editor, "text", atEnd: false);

            // Arrowing through the box only chooses; nothing changes until it is committed.
            combo!.SelectedIndex = (int)ComposeParagraphStyle.Quote;
            combo.SelectedIndex = (int)ComposeParagraphStyle.Normal;
            combo.SelectedIndex = (int)ComposeParagraphStyle.Heading4;
            Assert.Equal("<p>text</p>", Html(editor.Document));

            window.CommitParagraphStyleChoice(returnToText: true);
            Assert.Equal("<h4>text</h4>", Html(editor.Document));

            combo.SelectedIndex = (int)ComposeParagraphStyle.Quote;
            window.CommitParagraphStyleChoice(returnToText: true);
            Assert.Equal("<blockquote>\n<p>text</p>\n</blockquote>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void EnterAtEndOfHeading_IsOneUndoStep()
    {
        var (window, editor) = HtmlWindow("Title");
        try
        {
            editor.Focus();
            PutCaret(editor, "Title", atEnd: false);
            window.ApplyHeading(2);
            PutCaret(editor, "Title", atEnd: true);
            Assert.True(window.HandleRichEnter());

            Assert.True(editor.Undo());
            Assert.Equal("<h2>Title</h2>", Html(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void EnterOnEmptyQuotedLine_InATableCell_LeavesTheQuote()
    {
        var (window, editor) = HtmlWindow("x");
        try
        {
            // Pasted content can put a quote in a table cell (the converter itself
            // flattens cells to text), so build one directly.
            var quote = RichTextDocumentConverter.NewQuoteSection();
            quote.Blocks.Add(new Paragraph(new Run("q")));
            var empty = new Paragraph();
            quote.Blocks.Add(empty);
            var row = new TableRow();
            row.Cells.Add(new TableCell(quote));
            var group = new TableRowGroup();
            group.Rows.Add(row);
            var table = new Table();
            table.RowGroups.Add(group);
            editor.Document.Blocks.Add(table);
            editor.CaretPosition = empty.ContentStart;

            // A quote in a table cell can be removed: the cell is a container too.
            Assert.True(window.HandleRichEnter());
            Assert.Equal(0, RichTextDocumentConverter.QuoteDepth(empty));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void DivWrappedMail_KeepsItsBlocks()
    {
        var doc = Doc("<div><h2>Agenda</h2><ul><li>one</li></ul><div>plain line</div></div>");
        Assert.Equal("<h2>Agenda</h2>\n<ul>\n<li>one</li>\n</ul>\n<p>plain line</p>", Html(doc));
    }
}
