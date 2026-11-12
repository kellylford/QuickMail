using System.Windows.Controls;
using System.Windows.Documents;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Exercises the session spell-scan sources against real WPF editors on an STA
/// thread. The WPF spell engine (ISpellChecker COM service) is available on the
/// Windows test runners this suite targets; every test seeds unmistakable
/// misspellings so engine vagaries about marginal words cannot cause flakes.
/// </summary>
[Collection("WpfTests")]
public class SpellCheckSourceTests
{
    private static TextBox MakeSpellCheckedTextBox(string text, int caret = 0)
    {
        var box = new TextBox { AcceptsReturn = true, Text = text };
        box.SpellCheck.IsEnabled = true;
        // The spell engine attaches when the control enters a live tree; forcing
        // measure/arrange plus an explicit enable is enough in a headless test.
        box.Measure(new System.Windows.Size(500, 500));
        box.Arrange(new System.Windows.Rect(0, 0, 500, 500));
        box.CaretIndex = caret;
        return box;
    }

    private static bool EngineAvailable(TextBox box)
    {
        // If the spell engine could not attach (stripped-down CI image), every
        // position reports no error; tests degrade to a no-op rather than fail.
        for (int i = 0; i < box.Text.Length; i++)
            if (box.GetSpellingError(i) != null) return true;
        return false;
    }

    [StaFact]
    public void TextBoxSource_WalksErrorsInOrder_AndExhausts()
    {
        var box = MakeSpellCheckedTextBox("I will recieve the pacakge tomorow.");
        if (!EngineAvailable(box)) return;

        var source = new TextBoxSpellSource(box, "body");

        Assert.Equal("recieve", source.MoveToNextError()!.Word);
        Assert.Equal("pacakge", source.MoveToNextError()!.Word);
        // Exact error extent: the trailing period is not part of the misspelling.
        Assert.Equal("tomorow", source.MoveToNextError()!.Word);
        Assert.Null(source.MoveToNextError());
    }

    [StaFact]
    public void TextBoxSource_WrapsFromCaretToStartPosition()
    {
        var text = "pacakge first then recieve";
        var box = MakeSpellCheckedTextBox(text, caret: text.IndexOf("then", System.StringComparison.Ordinal));
        if (!EngineAvailable(box)) return;

        var source = new TextBoxSpellSource(box, "body");

        // Scan starts at the caret: finds "recieve" first, wraps, finds "pacakge", then exhausts.
        Assert.Equal("recieve", source.MoveToNextError()!.Word);
        Assert.Equal("pacakge", source.MoveToNextError()!.Word);
        Assert.Null(source.MoveToNextError());
    }

    [StaFact]
    public void TextBoxSource_ReplaceCurrent_UpdatesTextAndContinues()
    {
        var box = MakeSpellCheckedTextBox("recieve the pacakge");
        if (!EngineAvailable(box)) return;

        var source = new TextBoxSpellSource(box, "body");

        Assert.Equal("recieve", source.MoveToNextError()!.Word);
        source.ReplaceCurrent("receive");
        Assert.StartsWith("receive the", box.Text);

        Assert.Equal("pacakge", source.MoveToNextError()!.Word);
        source.ReplaceCurrent("package");
        Assert.Equal("receive the package", box.Text);
        Assert.Null(source.MoveToNextError());
    }

    [StaFact]
    public void TextBoxSource_GetContextLine_ReturnsLineContainingWord()
    {
        var box = MakeSpellCheckedTextBox("First line fine.\r\nSecond line has recieve in it.");
        if (!EngineAvailable(box)) return;

        var source = new TextBoxSpellSource(box, "body");
        Assert.Equal("recieve", source.MoveToNextError()!.Word);
        Assert.Equal("Second line has recieve in it.", source.GetContextLine());
    }

    [StaFact]
    public void TextBoxSource_SelectCurrent_SelectsTheWord()
    {
        var box = MakeSpellCheckedTextBox("say recieve now");
        if (!EngineAvailable(box)) return;

        var source = new TextBoxSpellSource(box, "body");
        source.MoveToNextError();
        source.SelectCurrent();
        Assert.Equal("recieve", box.SelectedText);
    }

    [StaFact]
    public void RichTextBoxSource_WalksReplacesAndExhausts()
    {
        var box = new RichTextBox();
        box.SpellCheck.IsEnabled = true;
        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run("I will recieve the pacakge soon.")));
        box.Measure(new System.Windows.Size(500, 500));
        box.Arrange(new System.Windows.Rect(0, 0, 500, 500));
        box.CaretPosition = box.Document.ContentStart;

        // Engine availability probe for the rich editor.
        if (box.GetNextSpellingErrorPosition(box.Document.ContentStart, LogicalDirection.Forward) == null)
            return;

        var source = new RichTextBoxSpellSource(box, "body");

        Assert.Equal("recieve", source.MoveToNextError()!.Word);
        source.ReplaceCurrent("receive");

        Assert.Equal("pacakge", source.MoveToNextError()!.Word);
        source.ReplaceCurrent("package");

        Assert.Null(source.MoveToNextError());

        var text = new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
        Assert.Contains("receive the package", text);
    }

    [StaFact]
    public void SpellScan_FindErrorIndex_WrapsBothDirections()
    {
        var text = "pacakge middle recieve";
        var box = MakeSpellCheckedTextBox(text, caret: text.IndexOf("middle", System.StringComparison.Ordinal));
        if (!EngineAvailable(box)) return;

        int forward = SpellScan.FindErrorIndex(box, box.CaretIndex, forward: true);
        Assert.Equal("recieve", SpellScanWordAt(box.Text, forward));

        int backward = SpellScan.FindErrorIndex(box, box.CaretIndex, forward: false);
        Assert.Equal("pacakge", SpellScanWordAt(box.Text, backward));
    }

    private static string SpellScanWordAt(string text, int index)
    {
        Assert.True(index >= 0, "expected an error index");
        var (start, end) = SpellScan.ExpandWord(text, index);
        return text[start..end];
    }

    [StaFact]
    public void TextBoxSource_CaretInsideMisspelledWord_PresentsItOnlyOnce()
    {
        var box = MakeSpellCheckedTextBox("recieve then fine", caret: 3);   // caret inside "recieve"
        if (!EngineAvailable(box)) return;

        var source = new TextBoxSpellSource(box, "body");

        Assert.Equal("recieve", source.MoveToNextError()!.Word);
        // The wrapped pass covers [0, caret) — it must not re-present the word
        // that straddles the session start.
        Assert.Null(source.MoveToNextError());
    }

    [StaFact]
    public void RichTextBoxSource_CaretInsideMisspelledWord_PresentsItOnlyOnce()
    {
        var box = new RichTextBox();
        box.SpellCheck.IsEnabled = true;
        box.Document.Blocks.Clear();
        var run = new Run("recieve and pacakge");
        box.Document.Blocks.Add(new Paragraph(run));
        box.Measure(new System.Windows.Size(500, 500));
        box.Arrange(new System.Windows.Rect(0, 0, 500, 500));
        box.CaretPosition = run.ContentStart.GetPositionAtOffset(3)!;   // inside "recieve"

        if (box.GetNextSpellingErrorPosition(box.Document.ContentStart, LogicalDirection.Forward) == null)
            return;   // engine unavailable on this runner

        var source = new RichTextBoxSpellSource(box, "body");

        Assert.Equal("recieve", source.MoveToNextError()!.Word);
        Assert.Equal("pacakge", source.MoveToNextError()!.Word);
        Assert.Null(source.MoveToNextError());   // wrap stops before re-presenting "recieve"
    }

    // ── #902: web and email addresses are not misspellings ──────────────────────
    //
    // The addresses are built from misspelled words. A test only means something where the speller
    // flags a word inside an address — otherwise there is nothing to skip and it would pass with
    // the fix removed — so each one first checks that it does, and skips visibly if not.

    private const string AddressText =
        "See https://recieve.example/pacakge or mail tomorow@pacakge.example and teh rest";

    private const string OnlyAddressesText =
        "Plan at https://recieve.example/pacakge and tomorow@pacakge.example";

    private const string NoEngine = "No Windows spelling engine for the current language on this machine.";

    private const string NothingFlaggedInAddresses =
        "The speller does not flag words inside addresses on this machine, so there is nothing to skip.";

    private static RichTextBox MakeSpellCheckedRichTextBox(string text)
    {
        var box = new RichTextBox();
        box.SpellCheck.IsEnabled = true;
        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run(text)));
        box.Measure(new System.Windows.Size(800, 500));
        box.Arrange(new System.Windows.Rect(0, 0, 800, 500));
        box.CaretPosition = box.Document.ContentStart;
        return box;
    }

    /// <summary>Skips unless the speller works and flags at least one word inside an address.</summary>
    internal static void SkipUnlessFlagsInsideAnAddress(TextBox box)
    {
        Assert.SkipUnless(EngineAvailable(box), NoEngine);
        var text = box.Text;
        bool flagged = false;
        for (int i = 0; i < text.Length && !flagged; i++)
            flagged = box.GetSpellingError(i) != null
                      && SpellScan.IsInAddress(text, Math.Max(0, box.GetSpellingErrorStart(i)));
        Assert.SkipUnless(flagged, NothingFlaggedInAddresses);
    }

    /// <inheritdoc cref="SkipUnlessFlagsInsideAnAddress(TextBox)"/>
    internal static void SkipUnlessFlagsInsideAnAddress(RichTextBox box)
    {
        var position = box.GetNextSpellingErrorPosition(box.Document.ContentStart, LogicalDirection.Forward);
        Assert.SkipUnless(position != null, NoEngine);
        bool flagged = false;
        for (int guard = 0; position != null && !flagged && guard < 1000; guard++)
        {
            var range = box.GetSpellingErrorRange(position);
            if (range == null) break;
            flagged = SpellScan.IsInAddress(range);
            position = box.GetNextSpellingErrorPosition(range.End, LogicalDirection.Forward);
        }
        Assert.SkipUnless(flagged, NothingFlaggedInAddresses);
    }

    [StaFact]
    public void SpellScan_FindReportableErrorIndex_SkipsAddresses_BothDirections()
    {
        var box = MakeSpellCheckedTextBox(AddressText);
        SkipUnlessFlagsInsideAnAddress(box);
        int teh = AddressText.IndexOf("teh", System.StringComparison.Ordinal);

        Assert.Equal(teh, SpellScan.FindReportableErrorIndex(box, 0, forward: true));
        Assert.Equal(teh, SpellScan.FindReportableErrorIndex(box, AddressText.Length, forward: false));
        // From just past "teh", forward wraps round the addresses and comes back to it.
        Assert.Equal(teh, SpellScan.FindReportableErrorIndex(box, teh + 3, forward: true));
    }

    [StaFact]
    public void SpellScan_FindReportableErrorIndex_OnlyAddresses_FindsNothing()
    {
        var text = OnlyAddressesText;
        var box = MakeSpellCheckedTextBox(text);
        SkipUnlessFlagsInsideAnAddress(box);

        Assert.Equal(-1, SpellScan.FindReportableErrorIndex(box, 0, forward: true));
        Assert.Equal(-1, SpellScan.FindReportableErrorIndex(box, text.Length, forward: false));
        Assert.Equal(-1, SpellScan.FindReportableErrorIndex(box, 12, forward: true));   // inside the URL
    }

    [StaFact]
    public void SpellScan_FindErrorRange_SkipsAddresses_BothDirections()
    {
        var box = MakeSpellCheckedRichTextBox(AddressText);
        SkipUnlessFlagsInsideAnAddress(box);

        Assert.Equal("teh", SpellScan.FindErrorRange(box, forward: true)?.Range.Text);
        box.CaretPosition = box.Document.ContentEnd;
        Assert.Equal("teh", SpellScan.FindErrorRange(box, forward: false)?.Range.Text);
    }

    [StaFact]
    public void SpellScan_FindErrorRange_OnlyAddresses_FindsNothing()
    {
        var box = MakeSpellCheckedRichTextBox(OnlyAddressesText);
        SkipUnlessFlagsInsideAnAddress(box);

        Assert.Null(SpellScan.FindErrorRange(box, forward: true));
        box.CaretPosition = box.Document.ContentEnd;
        Assert.Null(SpellScan.FindErrorRange(box, forward: false));
    }

    [StaFact]
    public void TextBoxSource_SkipsAddresses()
    {
        var box = MakeSpellCheckedTextBox(AddressText);
        SkipUnlessFlagsInsideAnAddress(box);

        var source = new TextBoxSpellSource(box, "body");

        Assert.Equal("teh", source.MoveToNextError()!.Word);
        Assert.Null(source.MoveToNextError());
    }

    [StaFact]
    public void RichTextBoxSource_SkipsAddresses()
    {
        var box = MakeSpellCheckedRichTextBox(AddressText);
        SkipUnlessFlagsInsideAnAddress(box);

        var source = new RichTextBoxSpellSource(box, "body");

        Assert.Equal("teh", source.MoveToNextError()!.Word);
        Assert.Null(source.MoveToNextError());
    }

    [StaFact]
    public void SpellScan_TokenAround_IsTheWholeAddress()
    {
        var box = MakeSpellCheckedRichTextBox("See https://recieve.example/pacakge today");
        var start = box.Document.ContentStart.GetPositionAtOffset(
            "See https://".Length + 2, LogicalDirection.Forward)!;   // inside the Run, at "recieve"
        var range = new TextRange(start, start.GetPositionAtOffset("recieve".Length)!);
        Assert.Equal("recieve", range.Text);

        Assert.Equal(("https://recieve.example/pacakge", 8), SpellScan.TokenAround(range));
        Assert.True(SpellScan.IsInAddress(range));
    }

    [StaFact]
    public void SpellScan_TokenAround_CrossesInlineElements_AndStopsAtWhitespaceAndLineBreaks()
    {
        var box = new RichTextBox();
        box.Document.Blocks.Clear();
        var linked = new Run("https://recieve.example");
        box.Document.Blocks.Add(new Paragraph(new Run("Before"))
        {
            // "Before", a line break, then the address split across a hyperlink and a run.
            Inlines = { new LineBreak(), new Hyperlink(linked), new Run("/pacakge today") },
        });
        var start = linked.ContentStart.GetPositionAtOffset("https://".Length)!;
        var range = new TextRange(start, start.GetPositionAtOffset("recieve".Length)!);
        Assert.Equal("recieve", range.Text);

        Assert.Equal(("https://recieve.example/pacakge", 8), SpellScan.TokenAround(range));
    }

    [StaFact]
    public void SpellScan_TokenAround_StopsAtTheParagraphEdge()
    {
        var box = new RichTextBox();
        box.Document.Blocks.Clear();
        var word = new Run("recieve");
        box.Document.Blocks.Add(new Paragraph(new Run("first")));
        box.Document.Blocks.Add(new Paragraph(word));
        box.Document.Blocks.Add(new Paragraph(new Run("last")));

        Assert.Equal(("recieve", 0), SpellScan.TokenAround(new TextRange(word.ContentStart, word.ContentEnd)));
    }

    // The text of a Markdown link is the user's own and still checked; only the address is skipped.
    private const string MarkdownLinkText = "Read [recieve](https://pacakge.example/plan) now";

    [StaFact]
    public void MisspelledMarkdownLinkText_IsStillFound()
    {
        var box = MakeSpellCheckedTextBox(MarkdownLinkText);
        Assert.SkipUnless(EngineAvailable(box), NoEngine);
        int linkText = MarkdownLinkText.IndexOf("recieve", StringComparison.Ordinal);
        Assert.SkipUnless(box.GetSpellingError(linkText) != null,
            "The speller does not flag the text of a Markdown link on this machine.");

        int found = SpellScan.FindReportableErrorIndex(box, 0, forward: true);
        Assert.InRange(found, linkText - 1, linkText + "recieve".Length - 1);

        var source = new TextBoxSpellSource(box, "body");
        Assert.Contains("recieve", source.MoveToNextError()!.Word, StringComparison.Ordinal);
    }

    [StaFact]
    public void MisspelledMarkdownLinkText_IsStillFound_Html()
    {
        var box = MakeSpellCheckedRichTextBox(MarkdownLinkText);
        var first = box.GetNextSpellingErrorPosition(box.Document.ContentStart, LogicalDirection.Forward);
        Assert.SkipUnless(first != null, NoEngine);
        Assert.SkipUnless(box.GetSpellingErrorRange(first!)?.Text.Contains("recieve", StringComparison.Ordinal) == true,
            "The speller does not flag the text of a Markdown link on this machine.");

        Assert.Contains("recieve", SpellScan.FindErrorRange(box, forward: true)?.Range.Text ?? "", StringComparison.Ordinal);
        var source = new RichTextBoxSpellSource(box, "body");
        Assert.Contains("recieve", source.MoveToNextError()!.Word, StringComparison.Ordinal);
    }
}
