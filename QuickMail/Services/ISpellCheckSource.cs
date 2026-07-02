using System.Collections.Generic;

namespace QuickMail.Services;

/// <summary>One spelling error surfaced by an <see cref="ISpellCheckSource"/>.</summary>
public sealed record SpellingErrorInfo(string Word, IReadOnlyList<string> Suggestions);

/// <summary>
/// A spell-checkable text region walked by the Check Spelling dialog — one per
/// editor (body, subject). Implementations live in the View layer because they
/// wrap WPF editor controls; this contract is UI-free so the dialog ViewModel
/// can drive a session (and tests can stub it) without touching View types.
///
/// A source is a single-use forward scan: it starts at the editor's caret,
/// wraps once past the end back to the start position, and then reports
/// exhaustion. It re-queries the live editor on every call, so replacements
/// and even user edits between calls cannot leave it pointing at stale text.
/// </summary>
public interface ISpellCheckSource
{
    /// <summary>Short name used in progress announcements ("body", "subject").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Advances to the next spelling error in the wrapped scan. Returns null when
    /// the source is exhausted (the scan has covered the whole region once).
    /// </summary>
    SpellingErrorInfo? MoveToNextError();

    /// <summary>Replaces the word most recently returned by <see cref="MoveToNextError"/>.</summary>
    void ReplaceCurrent(string replacement);

    /// <summary>Selects the current word in the editor and scrolls it into view.</summary>
    void SelectCurrent();

    /// <summary>The full line (or paragraph) containing the current word, for context read-back.</summary>
    string GetContextLine();
}
