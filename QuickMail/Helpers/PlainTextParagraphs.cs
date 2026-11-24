namespace QuickMail.Helpers;

/// <summary>
/// Paragraphs in plain text: the runs of text between hard line breaks (<c>\r\n</c>, <c>\n</c>
/// or <c>\r</c>), as Notepad and Word treat them. An empty line is a paragraph of its own.
///
/// <para>A WPF <see cref="System.Windows.Controls.TextBox"/> has no paragraphs: Ctrl+Down jumps
/// to the end of the text, and its UI Automation Paragraph unit is the whole text, so a screen
/// reader asked to read the paragraph gets everything (issue #1077). The plain-text and
/// Markdown compose editor uses these instead, for both the caret and the text provider.</para>
///
/// <para>All positions are string indexes into the text. A position between the <c>\r</c> and
/// <c>\n</c> of a <c>\r\n</c> is never produced.</para>
/// </summary>
public static class PlainTextParagraphs
{
    private static bool IsBreak(char c) => c is '\r' or '\n';

    /// <summary>Where the paragraph holding <paramref name="index"/> starts.</summary>
    public static int Start(string text, int index)
    {
        int i = Math.Clamp(index, 0, text.Length);
        while (i > 0 && !IsBreak(text[i - 1])) i--;
        return i;
    }

    /// <summary>Where the paragraph starting at <paramref name="start"/> ends, before its line break.</summary>
    public static int TextEnd(string text, int start)
    {
        int i = Math.Clamp(start, 0, text.Length);
        while (i < text.Length && !IsBreak(text[i])) i++;
        return i;
    }

    /// <summary>The length of the line break at <paramref name="index"/>: 2 for <c>\r\n</c>, 1, or 0 for none.</summary>
    public static int BreakLength(string text, int index)
    {
        if (index >= text.Length || !IsBreak(text[index])) return 0;
        return text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
    }

    /// <summary>
    /// The start of the paragraph after the one holding <paramref name="index"/>, or null when it
    /// is the last. Text that ends with a line break has an empty last paragraph after it.
    /// </summary>
    public static int? NextStart(string text, int index)
    {
        int end = TextEnd(text, Start(text, index));
        return end < text.Length ? end + BreakLength(text, end) : null;
    }

    /// <summary>
    /// The start of the paragraph before the one starting at <paramref name="start"/>, or null
    /// when it is the first.
    /// </summary>
    public static int? PreviousStart(string text, int start)
    {
        if (start <= 0) return null;
        int i = start - 1;
        if (text[i] == '\n' && i > 0 && text[i - 1] == '\r') i--;
        return Start(text, i);
    }

    /// <summary>
    /// The range a screen reader should be given for the paragraph holding
    /// <paramref name="index"/>: its text without the line break, or, for an empty paragraph,
    /// just its line break, which screen readers read as "blank". An empty last paragraph is an
    /// empty range.
    /// </summary>
    public static (int Start, int End) Enclosing(string text, int index)
    {
        int start = Start(text, index);
        int end = TextEnd(text, start);
        return end == start ? (start, start + BreakLength(text, start)) : (start, end);
    }

    /// <summary>Where Ctrl+Down puts the caret: the next paragraph's start, or the end of the text.</summary>
    public static int CaretDown(string text, int caret) => NextStart(text, caret) ?? text.Length;

    /// <summary>
    /// Where Ctrl+Up puts the caret: the start of its own paragraph if it isn't there already,
    /// otherwise the start of the paragraph before.
    /// </summary>
    public static int CaretUp(string text, int caret)
    {
        int start = Start(text, caret);
        return start < caret ? start : PreviousStart(text, start) ?? 0;
    }
}
