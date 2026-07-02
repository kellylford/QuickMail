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
}
