using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;
using QuickMail.Helpers;
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
    /// Uses <see cref="TextBoxBase.GetNextSpellingErrorCharacterIndex"/> — one
    /// engine call per error rather than one per character. The API returns the
    /// start of the error containing the probe index (inclusive), matching the
    /// old per-character walk's behavior of landing on the word under the probe.
    /// </summary>
    internal static int FindErrorIndex(TextBox box, int start, bool forward)
    {
        var text = box.Text;
        if (string.IsNullOrEmpty(text)) return -1;

        if (forward)
        {
            if (start < text.Length)
            {
                int idx = box.GetNextSpellingErrorCharacterIndex(start, LogicalDirection.Forward);
                if (idx >= 0) return idx;
            }
            if (start > 0)
            {
                int idx = box.GetNextSpellingErrorCharacterIndex(0, LogicalDirection.Forward);
                if (idx >= 0 && idx < start) return idx;
            }
        }
        else
        {
            if (start > 0)
            {
                int idx = box.GetNextSpellingErrorCharacterIndex(
                    Math.Min(start - 1, text.Length - 1), LogicalDirection.Backward);
                if (idx >= 0) return idx;
            }
            int wrapIdx = box.GetNextSpellingErrorCharacterIndex(text.Length - 1, LogicalDirection.Backward);
            // Accept a wrapped hit only if its extent reaches back to the start
            // position — mirrors the old walk over [start, end).
            if (wrapIdx >= 0 && wrapIdx + box.GetSpellingErrorLength(wrapIdx) > start) return wrapIdx;
        }
        return -1;
    }

    // Marks the start of the quoted/forwarded original that Reply/Forward seeds below the user's
    // own text — the reply attribution ("On <date>, <sender> wrote:") or the forward header
    // ("---------- Forwarded message ----------"). Spell check stops here so it doesn't walk into
    // the original message (issue #228). Matches QuickMail's own seeded markers (see
    // ComposeViewModel.CreateReply / CreateForward); a line the user did not write.
    private static readonly Regex QuoteMarker = new(
        @"^(On .+ wrote:|-{3,}\s*Forwarded message\s*-{3,})\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Character index at which the quoted/forwarded original begins in a plain-text body, or -1 if
    /// the body has no such marker (a fresh compose, or bottom-posted content). Everything before it
    /// is the user's own text.
    /// </summary>
    internal static int QuoteBoundaryIndex(string text)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        var m = QuoteMarker.Match(text);
        return m.Success ? m.Index : -1;
    }

    /// <summary>
    /// TextPointer at the start of the quoted/forwarded original in a rich (HTML) body — the first
    /// block that is a blockquote or a paragraph holding the reply/forward marker line — or null if
    /// none is present.
    /// </summary>
    internal static TextPointer? QuoteBoundaryPointer(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
        {
            if ((block.Tag as string) == RichTextDocumentConverter.TagBlockquote)
                return block.ContentStart;
            if (block is Paragraph p)
            {
                var line = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                if (QuoteMarker.IsMatch(line)) return block.ContentStart;
            }
        }
        return null;
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
    private readonly int _scanEnd;  // exclusive upper bound; excludes quoted/forwarded text (#228)
    private bool _started;
    private bool _wrapped;
    private int _scanIndex;
    private int _startIndex;        // session start; shifted when a replacement lands before it
    private int _currentWordStart = -1;
    private int _currentWordEnd = -1;

    public string DisplayName { get; }

    /// <param name="scanEndExclusive">
    /// Optional exclusive upper bound: the scan ignores errors at or beyond this index, used to keep
    /// the body scan out of the quoted/forwarded original (issue #228). Null scans the whole box.
    /// </param>
    public TextBoxSpellSource(TextBox box, string displayName, int? scanEndExclusive = null)
    {
        _box = box;
        DisplayName = displayName;
        _scanEnd = scanEndExclusive ?? int.MaxValue;
    }

    public SpellingErrorInfo? MoveToNextError()
    {
        var text = _box.Text;
        if (!_started)
        {
            _started = true;
            _startIndex = Math.Clamp(_box.CaretIndex, 0, Math.Min(text.Length, _scanEnd));
            _scanIndex = _startIndex;
        }

        while (true)
        {
            text = _box.Text;
            int effLen = Math.Min(text.Length, _scanEnd);
            int limit = _wrapped ? Math.Min(_startIndex, effLen) : effLen;

            if (text.Length > 0 && _scanIndex < limit)
            {
                // One engine call per error instead of one per character. The
                // API returns the start of the error containing the probe index
                // (inclusive), so a session started with the caret inside a
                // misspelled word presents that word first — same as the old
                // per-character walk.
                int idx = _box.GetNextSpellingErrorCharacterIndex(
                    Math.Min(_scanIndex, text.Length - 1), LogicalDirection.Forward);
                if (idx >= 0 && idx < limit)
                {
                    // Exact error extent (not whitespace-delimited expansion) so a
                    // replacement of "tomorow." touches only "tomorow", not the period.
                    int errStart = _box.GetSpellingErrorStart(idx);
                    int errLen = _box.GetSpellingErrorLength(idx);
                    if (errLen <= 0 || errStart < 0)
                        (errStart, errLen) = FallbackExtent(text, idx);

                    // Accept only an error extending past the scan position — a
                    // probe clamped to the last character can re-surface the word
                    // just handled at the end of the text.
                    if (errStart + errLen > _scanIndex)
                    {
                        // After wrapping, a word straddling the session start was
                        // already presented in the pre-wrap pass — the scan is done.
                        if (_wrapped && errStart + errLen > limit) return null;

                        _currentWordStart = errStart;
                        _currentWordEnd = errStart + errLen;
                        _scanIndex = _currentWordEnd;
                        var word = text[_currentWordStart.._currentWordEnd];
                        var error = _box.GetSpellingError(idx);
                        return new SpellingErrorInfo(word,
                            error?.Suggestions.ToList() ?? new List<string>());
                    }
                }
            }

            if (_wrapped) return null;
            _wrapped = true;
            _scanIndex = 0;
        }
    }

    private static (int Start, int Length) FallbackExtent(string text, int index)
    {
        var (start, end) = SpellScan.ExpandWord(text, index);
        return (start, end - start);
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
    private readonly TextPointer? _scanEnd;  // stop before quoted/forwarded text (#228)
    private bool _started;
    private bool _wrapped;
    private TextPointer? _scanPos;
    private TextPointer? _startPos;
    private TextRange? _currentRange;

    public string DisplayName { get; }

    /// <param name="scanEnd">
    /// Optional stop position: errors at or beyond it are ignored, keeping the body scan out of the
    /// quoted/forwarded original (issue #228). Null scans the whole document.
    /// </param>
    public RichTextBoxSpellSource(RichTextBox box, string displayName, TextPointer? scanEnd = null)
    {
        _box = box;
        DisplayName = displayName;
        _scanEnd = scanEnd;
    }

    public SpellingErrorInfo? MoveToNextError()
    {
        if (!_started)
        {
            _started = true;
            _startPos = _box.CaretPosition;
            // If the caret sits inside the quoted region, treat the region end as the session start
            // so the wrap boundary stays within the user's own text.
            if (_scanEnd != null && _startPos.CompareTo(_scanEnd) > 0) _startPos = _scanEnd;
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
                // Ignore an error that starts at or beyond the quoted-region boundary — there are no
                // more in-bounds errors ahead, so fall through to wrap/finish.
                if (range != null && !(_scanEnd != null && range.Start.CompareTo(_scanEnd) >= 0))
                {
                    // After wrapping, stop once the scan reaches the session start.
                    // Compare the error's END: a word straddling the start was
                    // already presented pre-wrap (GetNextSpellingErrorPosition is
                    // inclusive of the error containing its input position), so
                    // re-presenting it here would prompt for the same word twice.
                    if (_wrapped && _startPos != null && range.End.CompareTo(_startPos) > 0)
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
