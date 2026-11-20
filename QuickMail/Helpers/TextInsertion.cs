namespace QuickMail.Helpers;

/// <summary>
/// Where inserted blocks of text (a template) go in the plain-text editor.
/// </summary>
public static class TextInsertion
{
    /// <summary>
    /// The text to put at <paramref name="caret"/> so that <paramref name="block"/> sits on
    /// lines of its own: a line break is added before it when the caret is part-way along a
    /// line, and after it when text follows on that line. Without them a template was glued
    /// to the line it landed on (#888). Breaks use the body's own line ending.
    /// <c>CaretAfter</c> is the offset within <c>Insert</c> just past the block itself, before
    /// any break added after it — where the caret belongs.
    /// </summary>
    public static (string Insert, int CaretAfter) LinesAt(string text, int caret, string block)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        block = block.Replace("\r\n", "\n").Replace("\n", newline);

        bool midLine = caret > 0 && text[caret - 1] != '\n';
        bool textFollows = caret < text.Length && text[caret] is not ('\r' or '\n');
        bool blockEndsLine = block.EndsWith('\n');

        var before = midLine ? newline : string.Empty;
        var after = textFollows && !blockEndsLine ? newline : string.Empty;
        return (before + block + after, before.Length + block.Length);
    }
}
