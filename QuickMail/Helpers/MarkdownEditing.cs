using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QuickMail.Helpers;

/// <summary>
/// A single text replacement to apply to the Markdown editor, expressed so the
/// View can apply it through TextBox.SelectedText (one undo unit) instead of
/// rewriting the whole Text property.
/// </summary>
/// <param name="ReplaceStart">Start index of the text being replaced.</param>
/// <param name="ReplaceLength">Length of the text being replaced.</param>
/// <param name="Replacement">The replacement text.</param>
/// <param name="SelectionStart">Where the selection/caret should land afterwards.</param>
/// <param name="SelectionLength">Selection length afterwards (0 = caret).</param>
/// <param name="TurnedOn">True when the operation added formatting, false when it removed it.</param>
public readonly record struct MarkdownEdit(
    int ReplaceStart, int ReplaceLength, string Replacement,
    int SelectionStart, int SelectionLength, bool TurnedOn);

/// <summary>
/// Pure text operations implementing the formatting commands for Markdown
/// compose mode: toggling inline emphasis, headings, and list prefixes on
/// Markdown source. No UI dependencies — fully unit-testable.
/// </summary>
public static class MarkdownEditing
{
    // ── Inline emphasis (**bold**, *italic*, ~~strikethrough~~, `code`) ───────

    public static MarkdownEdit ToggleInline(string text, int selStart, int selLen, string marker)
    {
        int m = marker.Length;

        if (selLen > 0)
        {
            var sel = text.Substring(selStart, selLen);

            // Selection includes the markers: **bold** selected entirely.
            if (selLen >= 2 * m
                && sel.StartsWith(marker, StringComparison.Ordinal)
                && sel.EndsWith(marker, StringComparison.Ordinal))
            {
                var inner = sel[m..^m];
                return new MarkdownEdit(selStart, selLen, inner, selStart, inner.Length, TurnedOn: false);
            }

            // Markers immediately surround the selection: **|bold|**.
            if (selStart >= m
                && selStart + selLen + m <= text.Length
                && text.Substring(selStart - m, m) == marker
                && text.Substring(selStart + selLen, m) == marker)
            {
                return new MarkdownEdit(selStart - m, selLen + 2 * m, sel, selStart - m, selLen, TurnedOn: false);
            }

            // Wrap the selection.
            return new MarkdownEdit(selStart, selLen, marker + sel + marker, selStart + m, selLen, TurnedOn: true);
        }

        // No selection: caret between an empty marker pair removes it (toggle off
        // while typing); otherwise insert a pair and put the caret in the middle.
        if (selStart >= m
            && selStart + m <= text.Length
            && text.Substring(selStart - m, m) == marker
            && text.Substring(selStart, m) == marker)
        {
            return new MarkdownEdit(selStart - m, 2 * m, string.Empty, selStart - m, 0, TurnedOn: false);
        }

        return new MarkdownEdit(selStart, 0, marker + marker, selStart + m, 0, TurnedOn: true);
    }

    // ── Headings ──────────────────────────────────────────────────────────────

    public static MarkdownEdit ToggleHeading(string text, int caretIndex, int level)
    {
        var (lineStart, lineEnd) = LineBoundsAt(text, caretIndex);
        var line = text[lineStart..lineEnd];
        var existing = HeadingPrefixLength(line, out int existingLevel);
        var newPrefix = existingLevel == level ? string.Empty : new string('#', level) + " ";

        int caretInContent = Math.Max(0, caretIndex - lineStart - existing);
        int newCaret = lineStart + newPrefix.Length + caretInContent;
        return new MarkdownEdit(lineStart, existing, newPrefix, newCaret, 0,
            TurnedOn: newPrefix.Length > 0);
    }

    // ── Lists ─────────────────────────────────────────────────────────────────

    public static MarkdownEdit ToggleListPrefix(string text, int selStart, int selLen, bool ordered)
    {
        var (blockStart, blockEnd) = LineBoundsAt(text, selStart);
        if (selLen > 0)
            (_, blockEnd) = LineBoundsAt(text, Math.Min(selStart + selLen, text.Length));

        var block = text[blockStart..blockEnd];
        var lines = block.Split('\n');

        bool allHaveRequested = lines.Where(l => l.Length > 0)
            .All(l => ListPrefixLength(l, out bool isOrdered) > 0 && isOrdered == ordered);

        var sb = new StringBuilder();
        int number = 1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            var line = lines[i];
            if (line.Length == 0) { continue; }
            var stripped = line[ListPrefixLength(line, out _)..];
            if (allHaveRequested)
                sb.Append(stripped);
            else
                sb.Append(ordered ? $"{number++}. " : "- ").Append(stripped);
        }

        var replacement = sb.ToString();
        return new MarkdownEdit(blockStart, blockEnd - blockStart, replacement,
            blockStart, replacement.Length, TurnedOn: !allHaveRequested);
    }

    /// <summary>
    /// Adds one level of indentation (2 spaces) to the current list line.
    /// Returns null when the caret is not on a list line.
    /// </summary>
    public static MarkdownEdit? IndentListItem(string text, int caretIndex)
    {
        var (lineStart, _) = LineBoundsAt(text, caretIndex);
        var line = text[lineStart..];
        var endIdx = line.IndexOf('\n');
        var lineContent = endIdx >= 0 ? line[..endIdx] : line;
        if (ListPrefixLength(lineContent.TrimStart(' '), out _) == 0) return null;
        return new MarkdownEdit(lineStart, 0, "  ", caretIndex + 2, 0, TurnedOn: true);
    }

    /// <summary>
    /// Removes one level of indentation (2 leading spaces) from the current list line.
    /// Returns null when the caret is not on an indented list line.
    /// </summary>
    public static MarkdownEdit? DedentListItem(string text, int caretIndex)
    {
        var (lineStart, lineEnd) = LineBoundsAt(text, caretIndex);
        var line = text[lineStart..lineEnd];
        if (line.Length < 2 || line[0] != ' ' || line[1] != ' ') return null;
        var trimmed = line.TrimStart(' ');
        if (ListPrefixLength(trimmed, out _) == 0) return null;
        int remove = Math.Min(2, line.Length - trimmed.Length);
        if (remove == 0) return null;
        return new MarkdownEdit(lineStart, remove, "", Math.Max(lineStart, caretIndex - remove), 0, TurnedOn: false);
    }

    // ── Links ─────────────────────────────────────────────────────────────────

    public static MarkdownEdit InsertLink(string text, int selStart, int selLen, string display, string url)
    {
        if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            url = string.Empty;
        var link = $"[{display}]({url})";
        return new MarkdownEdit(selStart, selLen, link, selStart + link.Length, 0, TurnedOn: true);
    }

    // ── Clear formatting ──────────────────────────────────────────────────────

    /// <summary>
    /// Strips Markdown formatting from the selection (or the caret's line when
    /// nothing is selected): inline emphasis markers and leading heading, list,
    /// and quote prefixes on each covered line.
    /// </summary>
    public static MarkdownEdit ClearFormatting(string text, int selStart, int selLen)
    {
        int start = selStart, length = selLen;
        if (length == 0)
            (start, length) = LineBoundsAt(text, selStart) is var (ls, le) ? (ls, le - ls) : (0, 0);

        var region = text.Substring(start, length);
        var lines = region.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            line = line[HeadingPrefixLength(line, out _)..];
            line = line[ListPrefixLength(line, out _)..];
            while (line.StartsWith("> ", StringComparison.Ordinal)) line = line[2..];
            line = line.Replace("**", "").Replace("~~", "").Replace("`", "").Replace("*", "");
            lines[i] = line;
        }
        var replacement = string.Join("\n", lines);
        return new MarkdownEdit(start, length, replacement, start, replacement.Length, TurnedOn: false);
    }

    // ── Formatting description (announce formatting state) ───────────────────

    /// <summary>
    /// Describes the Markdown formatting at the caret, e.g.
    /// "Heading 2. Bold on, Italic off, Strikethrough off, Code off."
    /// </summary>
    public static string DescribeFormatting(string text, int caretIndex)
    {
        var parts = DescribeFormattingParts(text, caretIndex);
        return $"{parts[0]}. {string.Join(", ", parts.Skip(1))}.";
    }

    /// <summary>
    /// The formatting at the caret as one fact per entry — block type first,
    /// then each inline attribute ("Bold on", …). Shared by the spoken
    /// announcement and the Show Formatting list window. Marker detection is a
    /// line-local heuristic: an odd number of marker occurrences before the
    /// caret means the caret is inside that emphasis.
    /// </summary>
    public static List<string> DescribeFormattingParts(string text, int caretIndex)
    {
        var (lineStart, lineEnd) = LineBoundsAt(text, caretIndex);
        var line = text[lineStart..lineEnd];
        int caretInLine = Math.Clamp(caretIndex - lineStart, 0, line.Length);

        string block;
        if (IsInsideFencedCodeBlock(text, lineStart))
            block = "Code block";
        else if (HeadingPrefixLength(line, out int level) > 0)
            block = $"Heading {level}";
        else if (ListPrefixLength(line, out bool isOrdered) > 0)
            block = isOrdered ? "Numbered list item" : "Bullet list item";
        else if (line.StartsWith("> ", StringComparison.Ordinal))
            block = "Quote";
        else
            block = "Normal text";

        var bold = OddCountBefore(line, caretInLine, "**");
        var withoutBold = line.Replace("**", "\x01\x01"); // placeholder, keeps indices
        var italic = OddCountBefore(withoutBold, caretInLine, "*");
        var strike = OddCountBefore(line, caretInLine, "~~");
        var code = OddCountBefore(line, caretInLine, "`");

        return
        [
            block,
            $"Bold {OnOff(bold)}",
            $"Italic {OnOff(italic)}",
            $"Strikethrough {OnOff(strike)}",
            $"Code {OnOff(code)}",
        ];
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private static bool OddCountBefore(string line, int caretInLine, string marker)
    {
        int count = 0, idx = 0;
        while ((idx = line.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0 && idx < caretInLine)
        {
            count++;
            idx += marker.Length;
        }
        return count % 2 == 1;
    }

    private static bool IsInsideFencedCodeBlock(string text, int lineStart)
    {
        int fences = 0, idx = 0;
        while (idx < lineStart)
        {
            int lineEnd = text.IndexOf('\n', idx);
            if (lineEnd < 0 || lineEnd >= lineStart) break;
            if (text[idx..lineEnd].TrimStart().StartsWith("```", StringComparison.Ordinal))
                fences++;
            idx = lineEnd + 1;
        }
        return fences % 2 == 1;
    }

    // ── Line helpers ──────────────────────────────────────────────────────────

    private static (int Start, int End) LineBoundsAt(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        int start = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        int end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;
        // A caret sitting on the '\n' itself belongs to the line before it.
        if (end < start) end = start;
        return (start, end);
    }

    /// <summary>Length of a leading "#... " heading prefix, with its level; 0 when none.</summary>
    private static int HeadingPrefixLength(string line, out int level)
    {
        level = 0;
        int i = 0;
        while (i < line.Length && line[i] == '#' && i < 6) i++;
        if (i == 0 || i >= line.Length || line[i] != ' ') { level = 0; return 0; }
        level = i;
        return i + 1;
    }

    /// <summary>Length of a leading "- " / "* " / "+ " / "1. " list prefix; 0 when none.</summary>
    private static int ListPrefixLength(string line, out bool ordered)
    {
        ordered = false;
        if (line.Length >= 2 && (line[0] is '-' or '*' or '+') && line[1] == ' ')
            return 2;
        int i = 0;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i > 0 && i + 1 < line.Length && line[i] == '.' && line[i + 1] == ' ')
        {
            ordered = true;
            return i + 2;
        }
        return 0;
    }
}
