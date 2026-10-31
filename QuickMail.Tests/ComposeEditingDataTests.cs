// Edits in compose that changed the user's text without asking, found in the compose editing
// bug bash of 30 October 2026: stale Alt+1 replacement (#885), Alt+1 wiping undo (#886), the
// Plain → HTML → Plain round trip (#887), Insert Template (#888, #889), Markdown bold on a
// selection with a trailing space (#890), and Shift+Tab from the body landing on Send (#897).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ComposeEditingDataTests
{
    private static readonly MarkdownService Markdown = new();

    // ── #888: where a template goes in plain text ──────────────────────────────

    [Theory]
    [InlineData("Line one\nLine two", 9, "Sign off", "Line one\nSign off\nLine two", 17)]   // start of a line
    [InlineData("Line one", 8, "Sign off", "Line one\nSign off", 17)]                       // end of the last line
    [InlineData("Line one", 4, "Sign off", "Line\nSign off\n one", 13)]                     // part-way along
    [InlineData("", 0, "Sign off", "Sign off", 8)]                                          // empty body
    [InlineData("A\n\nB", 2, "T1\nT2", "A\nT1\nT2\nB", 7)]                                  // fills an empty line
    [InlineData("A\nB", 2, "T\n", "A\nT\nB", 4)]                                            // template ends with a break
    public void LinesAt_PutsTheBlockOnLinesOfItsOwn(string text, int caret, string block, string expected, int expectedCaret)
    {
        var (insert, caretAfter) = TextInsertion.LinesAt(text, caret, block);
        Assert.Equal(expected, text[..caret] + insert + text[caret..]);
        Assert.Equal(expectedCaret, caret + caretAfter);
    }

    [Fact]
    public void LinesAt_UsesTheBodysLineEnding()
    {
        var text = "Line one\r\nLine two";
        var (insert, _) = TextInsertion.LinesAt(text, 4, "T1\nT2");
        Assert.Equal("\r\nT1\r\nT2\r\n", insert);
    }

    // ── #890: Markdown emphasis keeps whitespace outside the markers ──────────

    [Fact]
    public void ToggleInline_TrailingSpaceStaysOutsideTheMarkers()
    {
        const string text = "make this bold please";
        var edit = MarkdownEditing.ToggleInline(text, 10, 5, "**"); // "bold " — Ctrl+Shift+Right
        var result = text[..edit.ReplaceStart] + edit.Replacement + text[(edit.ReplaceStart + edit.ReplaceLength)..];
        Assert.Equal("make this **bold** please", result);
        Assert.Equal("bold", result.Substring(edit.SelectionStart, edit.SelectionLength));
    }

    [Fact]
    public void ToggleInline_LeadingSpaceStaysOutsideTheMarkers()
    {
        const string text = "make this bold please";
        var edit = MarkdownEditing.ToggleInline(text, 9, 5, "*"); // " bold"
        var result = text[..edit.ReplaceStart] + edit.Replacement + text[(edit.ReplaceStart + edit.ReplaceLength)..];
        Assert.Equal("make this *bold* please", result);
    }

    // ── #887: Plain → HTML → Plain ─────────────────────────────────────────────

    [Fact]
    public void HtmlStripper_ABreakFollowedByASourceNewline_IsOneLineBreak()
    {
        Assert.Equal("one\ntwo\nthree", HtmlStripper.ToPlainText("<p>one<br />\ntwo<br />\nthree</p>\n"));
    }

    [Fact]
    public void HtmlStripper_KeepsBlankLinesInsidePre()
    {
        Assert.Equal("a\n\nb", HtmlStripper.ToPlainText("<pre><code>a\n\nb</code></pre>"));
    }

    [StaTheory]
    [InlineData("Line one\nLine two\nLine three")]
    [InlineData("Para one line one\nPara one line two\n\nPara two")]
    public void PlainToHtmlToPlain_GivesBackTheSameText(string plain)
    {
        var doc = RichTextDocumentConverter.FromHtml(Markdown.PlainTextToHtml(plain));
        Assert.Equal(plain, RichTextDocumentConverter.ToPlainText(doc).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    [Fact]
    public void HtmlToPlain_DoesNotAsk_WhenThereIsNoFormatting()
    {
        var vm = NewVm();
        vm.SetMode(ComposeMode.Html);
        vm.RichBodyProvider = () => new RichBodySnapshot("<p>one<br />\ntwo</p>\n", "one\ntwo", "one\ntwo");
        bool asked = false;
        vm.ConfirmationRequested = (_, _) => { asked = true; return true; };

        Assert.True(vm.SetMode(ComposeMode.PlainText));
        Assert.False(asked);
        Assert.Equal("one\ntwo", vm.Body);
    }

    [Fact]
    public void HtmlToPlain_Asks_WhenFormattingWouldBeLost()
    {
        var vm = NewVm();
        vm.SetMode(ComposeMode.Html);
        vm.RichBodyProvider = () => new RichBodySnapshot("<p><strong>hi</strong></p>", "**hi**", "hi");
        bool asked = false;
        vm.ConfirmationRequested = (_, _) => { asked = true; return true; };

        Assert.True(vm.SetMode(ComposeMode.PlainText));
        Assert.True(asked);
    }

    // ── #888/#889: Insert Template in the window ──────────────────────────────

    [StaFact]
    public void InsertTemplate_PlainText_GoesAtTheCaret_AsOneUndoStep()
    {
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        window.Show();
        try
        {
            body.Text = "Line one\nLine two";
            body.Select(9, 0);
            window.InsertTextAtCaret("Sign off");

            Assert.Equal("Line one\nSign off\nLine two", body.Text);
            Assert.Equal(17, body.CaretIndex);
            body.Undo();
            Assert.Equal("Line one\nLine two", body.Text);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void InsertTemplate_Html_GoesInAsParagraphsOfItsOwn_AsOneUndoStep()
    {
        var (window, vm) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var rich = (RichTextBox)window.FindName("RichBodyBox");
        window.Show();
        try
        {
            vm.Body = "Dear Sam,\n\nLine one is short.";
            vm.SetMode(ComposeMode.Html);
            window.UpdateLayout();
            var before = Paragraphs(rich);
            // Caret part-way along the last paragraph: "Line one| is short."
            var last = rich.Document.Blocks.LastBlock as Paragraph;
            Assert.NotNull(last);
            rich.CaretPosition = AfterCharacters(last!, 8);

            window.InsertTextAtCaret("Sign off\nKelly");

            var after = Paragraphs(rich);
            Assert.Equal([.. before[..^1], "Line one", "Sign off", "Kelly", " is short."], after);
            Assert.True(rich.CanUndo);
            rich.Undo();
            Assert.Equal(before, Paragraphs(rich));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void InsertTemplate_Html_AtTheStartOfAParagraph_AddsNoEmptyParagraph()
    {
        var (window, vm) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var rich = (RichTextBox)window.FindName("RichBodyBox");
        window.Show();
        try
        {
            vm.Body = "Line one\n\nLine two";
            vm.SetMode(ComposeMode.Html);
            window.UpdateLayout();
            var count = Paragraphs(rich).Count;
            var last = (Paragraph)rich.Document.Blocks.LastBlock;
            rich.CaretPosition = last.ContentStart;

            window.InsertTextAtCaret("Sign off");

            var after = Paragraphs(rich);
            Assert.Equal(count + 1, after.Count);
            Assert.Equal(["Sign off", "Line two"], after.TakeLast(2));
        }
        finally { window.Close(); }
    }

    // ── #885/#886: Alt+1/2/3 in plain text ─────────────────────────────────────

    [StaFact]
    public void SpellingReplace_AfterTyping_DoesNotTouchTheText()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        window.Show();
        try
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            body.Text = "I recieve mail";
            body.CaretIndex = 0;
            window.NavigateSpellingError(forward: true);   // "Misspelling: recieve"
            Assert.Equal("recieve", body.SelectedText);

            body.Select(2, 0);
            body.SelectedText = "xx";                       // typed since the announcement
            window.ReplaceCurrentPlainMisspelling("receive");

            Assert.Equal("I xxrecieve mail", body.Text);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void SpellingReplace_ReplacesTheWord_AndUndoBringsItBack()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        window.Show();
        try
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            body.Text = "I recieve mail";
            body.CaretIndex = 0;
            window.NavigateSpellingError(forward: true);

            window.ReplaceCurrentPlainMisspelling("receive");
            Assert.Equal("I receive mail", body.Text);
            Assert.Equal(9, body.CaretIndex);

            body.Undo();
            Assert.Equal("I recieve mail", body.Text);
        }
        finally { window.Close(); }
    }

    // ── #897: Shift+Tab from the body must not land on Send ───────────────────

    [StaFact]
    public void SendButton_DoesNotShareTheBodysTabIndex()
    {
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        try
        {
            var send = window.FindName("SendButton") as Button;
            var body = window.FindName("BodyBox") as TextBox;
            var rich = window.FindName("RichBodyBox") as RichTextBox;
            Assert.NotNull(send);
            Assert.NotNull(body);
            Assert.NotNull(rich);
            Assert.True(send!.TabIndex > body!.TabIndex);
            Assert.True(send.TabIndex > rich!.TabIndex);
        }
        finally { window.Close(); }
    }

    private static TextPointer AfterCharacters(Paragraph p, int count)
    {
        var pos = p.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        for (int i = 0; i < count; i++) pos = pos.GetNextInsertionPosition(LogicalDirection.Forward)!;
        return pos;
    }

    private static List<string> Paragraphs(RichTextBox rich) =>
        rich.Document.Blocks.OfType<Paragraph>()
            .Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text)
            .ToList();

    private static ComposeViewModel NewVm() => new(
        new StubSmtpService(),
        new StubAccountService(),
        new StubCredentialService(),
        new StubImapMailService(),
        new StubTemplateService());
}
