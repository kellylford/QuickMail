using System;
using System.Collections.Generic;

namespace QuickMail.Helpers;

/// <summary>Checks the spelling of a piece of text. Windows' spell checker in the app; a fake in tests.</summary>
internal interface IWordSpeller
{
    /// <summary>The misspellings in <paramref name="text"/>, in order, as start and length.</summary>
    IReadOnlyList<(int Start, int Length)> Check(string text);

    IReadOnlyList<string> Suggest(string word);
}

/// <summary>
/// Where the misspellings are in a plain-text body, for an editor that has no spelling API of its
/// own to ask (the hosted WinUI editor, #1099). The text is checked a line at a time and each
/// line's result is kept, so moving the caret through a long reply checks only the lines it
/// visits, and typing rechecks only the line being typed.
/// </summary>
internal sealed class PlainTextSpelling
{
    private const int MaxCachedLines = 4000;

    private readonly IWordSpeller _speller;
    private readonly Func<string, bool>? _isKnownWord;
    private readonly Dictionary<string, IReadOnlyList<(int Start, int Length)>> _lines = new(StringComparer.Ordinal);

    /// <param name="isKnownWord">Words the user has added to QuickMail's own dictionary.</param>
    public PlainTextSpelling(IWordSpeller speller, Func<string, bool>? isKnownWord = null)
    {
        _speller = speller;
        _isKnownWord = isKnownWord;
    }

    /// <summary>The misspelling <paramref name="index"/> is in, or null.</summary>
    public (int Start, int Length)? ErrorAt(string text, int index)
    {
        if (index < 0 || index >= text.Length) return null;
        var (lineStart, lineEnd) = LineAround(text, index);
        foreach (var (start, length) in ErrorsIn(text, lineStart, lineEnd))
        {
            if (start > index) break;
            if (index < start + length) return (start, length);
        }
        return null;
    }

    /// <summary>
    /// The start of the nearest misspelling from <paramref name="index"/>, or -1. Forward, one that
    /// <paramref name="index"/> is in counts; backward, the nearest starting at or before it.
    /// </summary>
    public int NextErrorStart(string text, int index, bool forward)
    {
        if (text.Length == 0) return -1;
        index = Math.Clamp(index, 0, text.Length - 1);
        var (lineStart, lineEnd) = LineAround(text, index);

        if (forward)
        {
            while (true)
            {
                foreach (var (start, length) in ErrorsIn(text, lineStart, lineEnd))
                    if (start + length > index) return start;
                if (lineEnd >= text.Length) return -1;
                lineStart = NextLineStart(text, lineEnd);
                if (lineStart >= text.Length) return -1;
                lineEnd = LineEnd(text, lineStart);
            }
        }

        while (true)
        {
            var errors = ErrorsIn(text, lineStart, lineEnd);
            for (int i = errors.Count - 1; i >= 0; i--)
                if (errors[i].Start <= index) return errors[i].Start;
            if (lineStart == 0) return -1;
            // The line before: its end is the line break just before this line's start.
            int previousEnd = lineStart - 1;
            if (previousEnd > 0 && text[previousEnd] == '\n' && text[previousEnd - 1] == '\r') previousEnd--;
            lineEnd = previousEnd;
            lineStart = LineStart(text, lineEnd);
        }
    }

    public IReadOnlyList<string> Suggestions(string word) => _speller.Suggest(word);

    /// <summary>A line's misspellings, with positions in the whole text, less the user's own words.</summary>
    private List<(int Start, int Length)> ErrorsIn(string text, int lineStart, int lineEnd)
    {
        var result = new List<(int Start, int Length)>();
        if (lineEnd <= lineStart) return result;
        var line = text[lineStart..lineEnd];
        if (!_lines.TryGetValue(line, out var errors))
        {
            if (_lines.Count >= MaxCachedLines) _lines.Clear();
            errors = _speller.Check(line);
            _lines[line] = errors;
        }
        foreach (var (start, length) in errors)
        {
            if (start < 0 || length <= 0 || start + length > line.Length) continue;
            if (_isKnownWord != null && _isKnownWord(line.Substring(start, length))) continue;
            result.Add((lineStart + start, length));
        }
        return result;
    }

    private static (int Start, int End) LineAround(string text, int index)
    {
        // On a line break itself, the line is the one the break ends.
        int probe = index;
        if (probe < text.Length && text[probe] == '\n' && probe > 0 && text[probe - 1] == '\r') probe--;
        int start = LineStart(text, probe);
        return (start, LineEnd(text, start));
    }

    private static int LineStart(string text, int index)
    {
        int at = index;
        while (at > 0 && text[at - 1] != '\n' && text[at - 1] != '\r') at--;
        return at;
    }

    private static int LineEnd(string text, int lineStart)
    {
        int at = lineStart;
        while (at < text.Length && text[at] != '\r' && text[at] != '\n') at++;
        return at;
    }

    private static int NextLineStart(string text, int lineEnd)
    {
        int at = lineEnd;
        if (at < text.Length && text[at] == '\r') at++;
        if (at < text.Length && text[at] == '\n') at++;
        return at;
    }
}

/// <summary>
/// Positions in a RichEdit document against indexes in the same text with "\r\n" line breaks.
/// RichEdit holds each paragraph mark as one character ('\r', or '\v' for a soft break), so a
/// document position is the text index less the line breaks before it.
/// </summary>
internal static class RichEditOffsets
{
    /// <summary>
    /// RichEdit's text as QuickMail's: the final paragraph mark RichEdit always has is dropped, and
    /// every other break becomes "\r\n".
    /// </summary>
    public static string ToText(string raw)
    {
        if (raw.Length > 0 && raw[^1] == '\r') raw = raw[..^1];
        if (raw.IndexOfAny(Breaks) < 0) return raw;
        var sb = new System.Text.StringBuilder(raw.Length + 16);
        foreach (char c in raw)
        {
            if (c is '\r' or '\v') sb.Append("\r\n");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>QuickMail's text as RichEdit takes it: one '\r' per line break.</summary>
    public static string ToRaw(string text) =>
        text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');

    /// <summary>The document position of a text index.</summary>
    public static int ToPosition(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        int breaks = 0;
        for (int i = 0; i < index; i++)
            if (text[i] == '\n') breaks++;
        return index - breaks;
    }

    /// <summary>The text index of a document position.</summary>
    public static int ToIndex(string text, int position)
    {
        int index = 0, at = 0;
        while (at < position && index < text.Length)
        {
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index += 2;
            else index++;
            at++;
        }
        return index;
    }

    private static readonly char[] Breaks = { '\r', '\v' };
}
