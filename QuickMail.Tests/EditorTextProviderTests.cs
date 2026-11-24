using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using QuickMail.Controls;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// What the compose editors tell a screen reader about lines and paragraphs.
/// <para>#891: WPF's text provider gives an empty line as the empty line plus the whole next line,
/// so the blank line was read as the next line and that line was heard twice.</para>
/// <para>#1077: a WPF TextBox's Paragraph is the whole text, and Ctrl+Down jumped to the end.</para>
/// These ask the editors' own providers, as a screen reader does through UI Automation, with the
/// controls laid out in a real (off-screen, never activated) window: lines need layout.
/// </summary>
[Collection("WpfTests")]
public class EditorTextProviderTests
{
    private const string Body = "Alpha line\r\n\r\nBeta line\r\n\r\n\r\nGamma\r\nOn Monday, someone wrote:\r\n> quoted";

    private static Window Host(UIElement content)
    {
        var window = new Window
        {
            Content = content, Width = 400, Height = 600, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static ITextProvider Provider(UIElement editor) =>
        (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(editor).GetPattern(PatternInterface.Text);

    private static ITextRangeProvider CaretRange(ITextProvider provider)
    {
        var range = provider.GetSelection()[0].Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        return range;
    }

    private static string Read(ITextProvider provider, TextUnit unit)
    {
        var range = CaretRange(provider);
        range.ExpandToEnclosingUnit(unit);
        return range.GetText(-1);
    }

    // What a screen reader says for a line: the text, or "blank" when it is only line breaks.
    private static string Spoken(string text) => text.Trim('\r', '\n').Length == 0 ? "blank" : text.Trim('\r', '\n');

    [StaFact]
    public void PlainEditor_EveryLineIsReadOnce_AndEmptyLinesAreBlank()
    {
        var box = new EditorTextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = Body };
        var window = Host(box);
        try
        {
            var provider = Provider(box);
            Assert.IsType<EditorTextProvider>(provider);
            var heard = new List<string>();
            box.CaretIndex = 0;
            heard.Add(Spoken(Read(provider, TextUnit.Line)));
            for (int i = 0; i < 7; i++)
            {
                EditingCommands.MoveDownByLine.Execute(null, box);
                heard.Add(Spoken(Read(provider, TextUnit.Line)));
            }
            Assert.Equal(new[] { "Alpha line", "blank", "Beta line", "blank", "blank", "Gamma",
                                 "On Monday, someone wrote:", "> quoted" }, heard);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void PlainEditor_UpThroughEmptyLines_ReadsEachOnce()
    {
        // #1042: Up from the signature read the signature again instead of the blank line above.
        var box = new EditorTextBox { AcceptsReturn = true, Text = "Thanks again\r\n\r\nAlex" };
        var window = Host(box);
        try
        {
            var provider = Provider(box);
            box.CaretIndex = box.Text.Length;
            var heard = new List<string> { Spoken(Read(provider, TextUnit.Line)) };
            for (int i = 0; i < 2; i++)
            {
                EditingCommands.MoveUpByLine.Execute(null, box);
                heard.Add(Spoken(Read(provider, TextUnit.Line)));
            }
            Assert.Equal(new[] { "Alex", "blank", "Thanks again" }, heard);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void PlainEditor_ParagraphIsTheHardLine_NotTheWholeMessage()
    {
        var box = new EditorTextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 200,
            Text = "First paragraph, long enough to wrap onto more than one line in a narrow box.\r\n\r\nSecond." };
        var window = Host(box);
        try
        {
            var provider = Provider(box);
            box.CaretIndex = 40; // on the wrapped second line of the first paragraph
            Assert.Equal("First paragraph, long enough to wrap onto more than one line in a narrow box.\r\n",
                Read(provider, TextUnit.Paragraph));
            box.CaretIndex = box.Text.IndexOf("Second", StringComparison.Ordinal);
            Assert.Equal("Second.", Read(provider, TextUnit.Paragraph));
            box.CaretIndex = box.Text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 2;
            Assert.Equal("blank", Spoken(Read(provider, TextUnit.Paragraph)));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void PlainEditor_MoveByParagraph_StepsThroughEachOne()
    {
        var box = new EditorTextBox { AcceptsReturn = true, Text = "One.\r\n\r\nTwo.\r\nThree." };
        var window = Host(box);
        try
        {
            var provider = Provider(box);
            var range = provider.DocumentRange;
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            range.ExpandToEnclosingUnit(TextUnit.Paragraph);
            var read = new List<string> { Spoken(range.GetText(-1)) };
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(1, range.Move(TextUnit.Paragraph, 1));
                read.Add(Spoken(range.GetText(-1)));
            }
            Assert.Equal(0, range.Move(TextUnit.Paragraph, 1)); // no paragraph after the last
            Assert.Equal(new[] { "One.", "blank", "Two.", "Three." }, read);
            Assert.Equal(-2, range.Move(TextUnit.Paragraph, -2));
            Assert.Equal("blank", Spoken(range.GetText(-1)));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void PlainEditor_MoveEndpointByParagraph_ExtendsOneParagraphAtATime()
    {
        var box = new EditorTextBox { AcceptsReturn = true, Text = "One.\r\nTwo.\r\nThree." };
        var window = Host(box);
        try
        {
            var range = Provider(box).DocumentRange;
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            Assert.Equal(2, range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Paragraph, 2));
            Assert.Equal("One.\r\nTwo.\r\n", range.GetText(-1));
            Assert.Equal(1, range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Paragraph, 5));
            Assert.Equal(box.Text, range.GetText(-1));
            // Moving the start past the end takes the end along.
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Paragraph, -2);
            Assert.Equal(2, range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Paragraph, 2));
            Assert.Equal("", range.GetText(-1));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void PlainEditor_CtrlDownAndCtrlUp_MoveByParagraph()
    {
        var box = new EditorTextBox { AcceptsReturn = true, Text = "One.\r\n\r\nTwo.\r\nThree." };
        var window = Host(box);
        try
        {
            box.CaretIndex = 0;
            var stops = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                EditingCommands.MoveDownByParagraph.Execute(null, box);
                stops.Add(box.CaretIndex);
            }
            Assert.Equal(new[] { 6, 8, 14, 20 }, stops); // empty line, Two., Three., the end
            EditingCommands.MoveUpByParagraph.Execute(null, box);
            Assert.Equal(14, box.CaretIndex); // to the start of its own paragraph first
            EditingCommands.MoveUpByParagraph.Execute(null, box);
            Assert.Equal(8, box.CaretIndex);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void RichEditor_EmptyParagraphsAreBlank()
    {
        var box = new EditorRichTextBox();
        box.Document.Blocks.Clear();
        foreach (var text in new[] { "Alpha", "", "Beta", "", "", "Gamma" })
            box.Document.Blocks.Add(new Paragraph(new Run(text)));
        var window = Host(box);
        try
        {
            var provider = Provider(box);
            Assert.IsType<EditorTextProvider>(provider);
            var lines = new List<string>();
            var paragraphs = new List<string>();
            foreach (var block in box.Document.Blocks)
            {
                box.CaretPosition = block.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                lines.Add(Spoken(Read(provider, TextUnit.Line)));
                paragraphs.Add(Spoken(Read(provider, TextUnit.Paragraph)));
            }
            var expected = new[] { "Alpha", "blank", "Beta", "blank", "blank", "Gamma" };
            Assert.Equal(expected, lines);
            Assert.Equal(expected, paragraphs);
        }
        finally { window.Close(); }
    }

    [StaTheory]
    [InlineData("Hi \U0001F600")]       // an emoji: a surrogate pair, one WPF character
    [InlineData("Café time")] // e plus a combining accent, one WPF character
    [InlineData("\U0001F44D\U0001F3FD ok")] // an emoji with a skin-tone modifier
    public void PlainEditor_ParagraphAfterEmojiOrAccents_IsExact(string first)
    {
        var box = new EditorTextBox { AcceptsReturn = true, Text = first + "\r\nSee you\r\nBye" };
        var window = Host(box);
        try
        {
            var provider = Provider(box);
            box.CaretIndex = box.Text.IndexOf("See", StringComparison.Ordinal);
            Assert.Equal("See you\r\n", Read(provider, TextUnit.Paragraph));
            var range = CaretRange(provider);
            range.ExpandToEnclosingUnit(TextUnit.Paragraph);
            Assert.Equal(1, range.Move(TextUnit.Paragraph, 1));
            Assert.Equal("Bye", range.GetText(-1));
            Assert.Equal(-2, range.Move(TextUnit.Paragraph, -2));
            Assert.Equal(first + "\r\n", range.GetText(-1));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void PlainEditor_ParagraphNearTheEndOfALongMessage_IsQuick()
    {
        // A reply with a long quoted history: each paragraph query used to rebuild its range from
        // the top, one WPF character at a time, which took seconds on a message this size.
        var body = string.Concat(Enumerable.Repeat("> A quoted line of an earlier message in the thread.\r\n", 1000)) + "Last line.";
        var box = new EditorTextBox { AcceptsReturn = true, Text = body };
        var window = Host(box);
        try
        {
            var provider = Provider(box);
            box.CaretIndex = body.Length - 3;
            Read(provider, TextUnit.Paragraph); // warm up
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var range = CaretRange(provider);
            range.ExpandToEnclosingUnit(TextUnit.Paragraph);
            Assert.Equal("Last line.", range.GetText(-1));
            Assert.Equal(-1, range.Move(TextUnit.Paragraph, -1));
            Assert.Equal("> A quoted line of an earlier message in the thread.\r\n", range.GetText(-1));
            timer.Stop();
            Assert.True(timer.ElapsedMilliseconds < 2000, $"took {timer.ElapsedMilliseconds} ms");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void BothEditors_MoveByLineOnALineRange_ReadsEachLineOnce()
    {
        // A screen reader's say-all and line review move a line-sized range rather than the caret.
        var plain = new EditorTextBox { AcceptsReturn = true, Text = "Alpha\r\n\r\nBeta\r\n\r\n\r\nGamma" };
        var rich = new EditorRichTextBox();
        rich.Document.Blocks.Clear();
        foreach (var text in new[] { "Alpha", "", "Beta", "", "", "Gamma" })
            rich.Document.Blocks.Add(new Paragraph(new Run(text)));
        var panel = new StackPanel();
        panel.Children.Add(plain);
        panel.Children.Add(rich);
        var window = Host(panel);
        try
        {
            foreach (UIElement editor in new UIElement[] { plain, rich })
            {
                var range = Provider(editor).DocumentRange;
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                range.ExpandToEnclosingUnit(TextUnit.Line);
                var read = new List<string> { Spoken(range.GetText(-1)) };
                while (range.Move(TextUnit.Line, 1) == 1) read.Add(Spoken(range.GetText(-1)));
                Assert.Equal(new[] { "Alpha", "blank", "Beta", "blank", "blank", "Gamma" }, read);
            }
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void PlainEditor_LineBuiltFromTheCaretByMovingItsEnd_IsBlankOnAnEmptyLine()
    {
        var box = new EditorTextBox { AcceptsReturn = true, Text = "Alpha\r\n\r\nBeta" };
        var window = Host(box);
        try
        {
            box.CaretIndex = 7; // the empty line
            var range = CaretRange(Provider(box));
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Line, 1);
            Assert.Equal("blank", Spoken(range.GetText(-1)));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ThroughUiAutomation_TheBlankLineIsBlank()
    {
        // The same question asked from another thread through UI Automation, crossing WPF's
        // provider bridge as NVDA's and JAWS's queries do: the wrapper has to survive it.
        var box = new EditorTextBox { AcceptsReturn = true, Text = "Alpha\r\n\r\nBeta" };
        AutomationProperties.SetAutomationId(box, "probeBody");
        var window = Host(box);
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            box.CaretIndex = 7; // the empty line
            string? line = null;
            Exception? failure = null;
            var client = new Thread(() =>
            {
                try
                {
                    var element = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "probeBody"));
                    var range = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).GetSelection()[0].Clone();
                    range.ExpandToEnclosingUnit(TextUnit.Line);
                    line = range.GetText(-1);
                }
                catch (Exception e) { failure = e; }
            }) { IsBackground = true };
            client.SetApartmentState(ApartmentState.MTA);
            client.Start();
            // The provider answers on this thread, so keep its dispatcher running until the client
            // is done, or 20 seconds have passed: a hung query must fail the test, not hang the run.
            var frame = new System.Windows.Threading.DispatcherFrame();
            var giveUp = DateTime.UtcNow.AddSeconds(20);
            var poll = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(20),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => { if (!client.IsAlive || DateTime.UtcNow > giveUp) frame.Continue = false; },
                window.Dispatcher);
            poll.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            poll.Stop();
            Assert.False(client.IsAlive, "the UI Automation query didn't finish in 20 seconds");
            Assert.Null(failure);
            Assert.Equal("blank", Spoken(line!));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ComposeWindow_BothEditorsUseTheCorrectedProvider()
    {
        WpfTestHost.EnsureApplication(); // the app's styles, so the implicit TextBox style exists
        var vm = new ComposeViewModel(new StubSmtpService(), new StubAccountService(),
            new StubCredentialService(), new StubImapMailService(), new StubTemplateService());
        var window = new ComposeWindow(vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ConfirmSaveOnClose = null,
            Left = -20000, Top = -20000,
        };
        try
        {
            window.Show();
            vm.SetMode(ComposeMode.PlainText);
            vm.Body = "Hello\r\n\r\nOn Monday, someone wrote:";
            window.UpdateLayout();
            var plain = Assert.IsType<EditorTextBox>(window.FindName("BodyBox"));
            var rich = Assert.IsType<EditorRichTextBox>(window.FindName("RichBodyBox"));
            // The implicit TextBox and RichTextBox styles still reach the subclasses.
            Assert.NotNull(plain.Style);
            Assert.Same(plain.TryFindResource(typeof(TextBox)), plain.Style);
            Assert.Same(rich.TryFindResource(typeof(RichTextBox)), rich.Style);
            plain.CaretIndex = 7;
            Assert.Equal("blank", Spoken(Read(Provider(plain), TextUnit.Line)));
        }
        finally { window.Close(); }
    }
}
