using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// The shared spell-scan cores. Both the inline F-key navigation in
/// <see cref="ComposeWindow"/> and the session sources below sit on these
/// helpers so there is exactly one implementation of "find the next
/// misspelling" per editor type.
/// </summary>
internal static class SpellScan
{
    /// <summary>
    /// Finds the character index of the nearest spelling error in a TextBox,
    /// scanning from <paramref name="start"/> in the given direction and
    /// wrapping once around the document. Returns -1 when the text has no errors.
    /// </summary>
    internal static int FindErrorIndex(TextBox box, int start, bool forward)
    {
        var text = box.Text;
        if (string.IsNullOrEmpty(text)) return -1;

        if (forward)
        {
            for (int i = start; i < text.Length; i++)
                if (box.GetSpellingError(i) != null) return i;
            if (start > 0)
                for (int i = 0; i < start; i++)
                    if (box.GetSpellingError(i) != null) return i;
        }
        else
        {
            for (int i = Math.Min(start - 1, text.Length - 1); i >= 0; i--)
                if (box.GetSpellingError(i) != null) return i;
            for (int i = text.Length - 1; i >= start; i--)
                if (box.GetSpellingError(i) != null) return i;
        }
        return -1;
    }

    /// <summary>Expands <paramref name="index"/> to the whitespace-delimited word around it.</summary>
    internal static (int WordStart, int WordEnd) ExpandWord(string text, int index)
    {
        int wordStart = index;
        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1])) wordStart--;
        int wordEnd = index;
        while (wordEnd < text.Length && !char.IsWhiteSpace(text[wordEnd])) wordEnd++;
        return (wordStart, wordEnd);
    }

    /// <summary>
    /// Finds the nearest spelling error in a RichTextBox from the caret in the
    /// given direction, wrapping once around the document. Returns the error
    /// position and its word range, or null when the document has no errors.
    /// </summary>
    internal static (TextPointer ErrorPos, TextRange Range)? FindErrorRange(RichTextBox box, bool forward)
    {
        var doc = box.Document;
        var direction = forward ? LogicalDirection.Forward : LogicalDirection.Backward;
        var errorPos = box.GetNextSpellingErrorPosition(box.CaretPosition, direction);

        if (errorPos == null)
        {
            var wrapFrom = forward ? doc.ContentStart : doc.ContentEnd;
            errorPos = box.GetNextSpellingErrorPosition(wrapFrom, direction);
        }

        if (errorPos == null) return null;
        var range = box.GetSpellingErrorRange(errorPos);
        return range == null ? null : (errorPos, range);
    }
}

/// <summary>
/// Check Spelling session source over a plain <see cref="TextBox"/>
/// (Plain Text / Markdown body, and the subject line).
/// Scan order: caret → end of text, wrap → the start position, then exhausted.
/// </summary>
internal sealed class TextBoxSpellSource : ISpellCheckSource
{
    private readonly TextBox _box;
    private bool _started;
    private bool _wrapped;
    private int _scanIndex;
    private int _startIndex;        // session start; shifted when a replacement lands before it
    private int _currentWordStart = -1;
    private int _currentWordEnd = -1;

    public string DisplayName { get; }

    public TextBoxSpellSource(TextBox box, string displayName)
    {
        _box = box;
        DisplayName = displayName;
    }

    public SpellingErrorInfo? MoveToNextError()
    {
        var text = _box.Text;
        if (!_started)
        {
            _started = true;
            _startIndex = Math.Clamp(_box.CaretIndex, 0, text.Length);
            _scanIndex = _startIndex;
        }

        while (true)
        {
            text = _box.Text;
            int limit = _wrapped ? Math.Min(_startIndex, text.Length) : text.Length;

            for (int i = _scanIndex; i < limit; i++)
            {
                var error = _box.GetSpellingError(i);
                if (error == null) continue;

                (_currentWordStart, _currentWordEnd) = SpellScan.ExpandWord(text, i);
                _scanIndex = _currentWordEnd;
                var word = text[_currentWordStart.._currentWordEnd];
                return new SpellingErrorInfo(word, error.Suggestions.ToList());
            }

            if (_wrapped) return null;
            _wrapped = true;
            _scanIndex = 0;
        }
    }

    public void ReplaceCurrent(string replacement)
    {
        if (_currentWordStart < 0 || _currentWordEnd <= _currentWordStart) return;

        // Select + SelectedText keeps the edit a single undo unit without
        // replacing the whole Text property.
        _box.Select(_currentWordStart, _currentWordEnd - _currentWordStart);
        _box.SelectedText = replacement;

        int delta = replacement.Length - (_currentWordEnd - _currentWordStart);
        _scanIndex = _currentWordStart + replacement.Length;
        if (_wrapped && _currentWordStart < _startIndex)
            _startIndex += delta;   // keep the wrap boundary aligned after in-segment edits
        _currentWordEnd = _currentWordStart + replacement.Length;
    }

    public void SelectCurrent()
    {
        if (_currentWordStart < 0) return;
        var length = Math.Max(0, Math.Min(_currentWordEnd, _box.Text.Length) - _currentWordStart);
        _box.Select(_currentWordStart, length);
        try
        {
            _box.ScrollToLine(_box.GetLineIndexFromCharacterIndex(_currentWordStart));
        }
        catch (ArgumentOutOfRangeException)
        {
            // Layout not measured yet (headless tests) — selection alone is enough.
        }
    }

    public string GetContextLine()
    {
        // Derived from the text itself rather than the line-layout APIs, which
        // require up-to-date layout information the editor may not have while
        // an owned dialog holds focus.
        var text = _box.Text;
        if (_currentWordStart < 0 || _currentWordStart > text.Length) return string.Empty;

        int lineStart = text.LastIndexOf('\n', Math.Max(0, Math.Min(_currentWordStart, text.Length - 1))) + 1;
        int lineEnd = text.IndexOf('\n', _currentWordStart);
        if (lineEnd < 0) lineEnd = text.Length;
        return text[lineStart..lineEnd].TrimEnd('\r');
    }
}

/// <summary>
/// Check Spelling session source over a <see cref="RichTextBox"/> (HTML body).
/// TextPointers adjust themselves across edits, so replacements and mid-session
/// user edits keep the scan and the wrap boundary valid without bookkeeping.
/// </summary>
internal sealed class RichTextBoxSpellSource : ISpellCheckSource
{
    private readonly RichTextBox _box;
    private bool _started;
    private bool _wrapped;
    private TextPointer? _scanPos;
    private TextPointer? _startPos;
    private TextRange? _currentRange;

    public string DisplayName { get; }

    public RichTextBoxSpellSource(RichTextBox box, string displayName)
    {
        _box = box;
        DisplayName = displayName;
    }

    public SpellingErrorInfo? MoveToNextError()
    {
        if (!_started)
        {
            _started = true;
            _startPos = _box.CaretPosition;
            _scanPos = _startPos;
        }

        while (true)
        {
            var errorPos = _scanPos == null
                ? null
                : _box.GetNextSpellingErrorPosition(_scanPos, LogicalDirection.Forward);

            if (errorPos != null)
            {
                var range = _box.GetSpellingErrorRange(errorPos);
                if (range != null)
                {
                    // After wrapping, stop once the scan reaches the session start.
                    if (_wrapped && _startPos != null && range.Start.CompareTo(_startPos) >= 0)
                        return null;

                    _currentRange = range;
                    _scanPos = range.End;
                    var suggestions = _box.GetSpellingError(errorPos)?.Suggestions.ToList()
                        ?? new List<string>();
                    return new SpellingErrorInfo(range.Text, suggestions);
                }
            }

            if (_wrapped) return null;
            _wrapped = true;
            _scanPos = _box.Document.ContentStart;
        }
    }

    public void ReplaceCurrent(string replacement)
    {
        if (_currentRange == null) return;
        // TextRange.Text assignment preserves the range's formatting.
        _currentRange.Text = replacement;
        _scanPos = _currentRange.End;
    }

    public void SelectCurrent()
    {
        if (_currentRange == null) return;
        _box.Selection.Select(_currentRange.Start, _currentRange.End);
        (_currentRange.Start.Parent as System.Windows.FrameworkContentElement)?.BringIntoView();
    }

    public string GetContextLine()
    {
        var paragraph = _currentRange?.Start.Paragraph;
        if (paragraph == null) return string.Empty;
        return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
    }
}
