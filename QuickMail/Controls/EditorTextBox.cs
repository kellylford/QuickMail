using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using QuickMail.Helpers;

namespace QuickMail.Controls;

/// <summary>
/// The plain-text and Markdown compose editor: a <see cref="TextBox"/> with paragraphs.
///
/// <para>A WPF TextBox treats its whole text as one paragraph, so Ctrl+Down jumped to the end of
/// the message and Ctrl+Up to its start, and a screen reader asked for the paragraph got the
/// whole message (issue #1077). Here Ctrl+Up and Ctrl+Down move by hard line breaks, as in
/// Notepad and Word (<see cref="PlainTextParagraphs"/>), and the automation peer's text provider
/// (<see cref="EditorTextProvider"/>) reports the same paragraphs and reads an empty line as
/// blank (#891).</para>
///
/// <para>The keys are taken over with command bindings on the box, which WPF checks before the
/// TextBox's own, so every key handler on the box and its window still sees the key.
/// Ctrl+Shift+Up and Ctrl+Shift+Down (select by paragraph) are still WPF's.</para>
///
/// <para>WPF keys implicit styles on the exact type, so a use of this sets
/// <c>Style="{DynamicResource {x:Type TextBox}}"</c>, as <see cref="ControllingTextBox"/> does.</para>
/// </summary>
public class EditorTextBox : TextBox
{
    public EditorTextBox()
    {
        CommandBindings.Add(new CommandBinding(EditingCommands.MoveDownByParagraph,
            (_, e) => { MoveCaretTo(PlainTextParagraphs.CaretDown(Text, CaretIndex)); e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(EditingCommands.MoveUpByParagraph,
            (_, e) => { MoveCaretTo(PlainTextParagraphs.CaretUp(Text, CaretIndex)); e.Handled = true; }));
    }

    private void MoveCaretTo(int index)
    {
        Select(index, 0);
        int line = GetLineIndexFromCharacterIndex(index);
        if (line >= 0 && (line < GetFirstVisibleLineIndex() || line > GetLastVisibleLineIndex()))
            ScrollToLine(line);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EditorTextBoxPeer(this);

    private sealed class EditorTextBoxPeer : TextBoxAutomationPeer
    {
        private readonly EditorTextBox _owner;
        private EditorTextProvider? _provider;

        public EditorTextBoxPeer(EditorTextBox owner) : base(owner) => _owner = owner;

        public override object? GetPattern(PatternInterface patternInterface)
        {
            var pattern = base.GetPattern(patternInterface);
            if (patternInterface != PatternInterface.Text || pattern is not ITextProvider inner) return pattern;
            if (_provider?.Inner != inner) _provider = new EditorTextProvider(inner, () => _owner.Text);
            return _provider;
        }
    }
}

/// <summary>
/// The HTML compose editor: a <see cref="RichTextBox"/> whose text provider reads an empty
/// paragraph as blank instead of as the paragraph after it (issue #891). See
/// <see cref="EditorTextProvider"/>.
///
/// <para>A use of this sets <c>Style="{DynamicResource {x:Type RichTextBox}}"</c>, since WPF keys
/// implicit styles on the exact type.</para>
/// </summary>
public class EditorRichTextBox : RichTextBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new EditorRichTextBoxPeer(this);

    private sealed class EditorRichTextBoxPeer : RichTextBoxAutomationPeer
    {
        private EditorTextProvider? _provider;

        public EditorRichTextBoxPeer(RichTextBox owner) : base(owner) { }

        public override object? GetPattern(PatternInterface patternInterface)
        {
            var pattern = base.GetPattern(patternInterface);
            if (patternInterface != PatternInterface.Text || pattern is not ITextProvider inner) return pattern;
            if (_provider?.Inner != inner) _provider = new EditorTextProvider(inner, plainText: null);
            return _provider;
        }
    }
}
