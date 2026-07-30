using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Views;

/// <summary>
/// The prefix accumulator behind QuickMail's hand-rolled type-ahead (folder tree, message
/// list, conversation/sender/recipient trees, and the folder picker's tree view). Keystrokes
/// within <see cref="_resetDelay"/> of each other extend the prefix; a longer gap, or a move
/// to a different list (<c>scope</c>), starts a new one. Shipped in v0.5.5; extracted from
/// MainWindow so it can be tested without a window or synthesized input (issue #415).
///
/// <para>Deliberately free of WPF dependencies: callers check <c>Keyboard.Modifiers</c>
/// themselves before feeding text in, and time comes from <see cref="TimeProvider"/> so tests
/// control the clock.</para>
///
/// <para>Two entry paths exist per keystroke in MainWindow: a <c>PreviewKeyDown</c> route
/// (letters/digits via <c>TreeViewFocusHelper.TryGetTypeAheadKeyText</c>, so type-ahead can
/// preempt single-key command shortcuts) and the ordinary <c>PreviewTextInput</c> route. A
/// matched KeyDown marks the event handled, which suppresses the TextInput for that keystroke.
/// To keep an unmatched KeyDown from double-appending when its TextInput arrives, the KeyDown
/// route must use <see cref="TryPeek"/> and only <see cref="TryAppend"/> (commit) on a match.</para>
/// </summary>
public sealed class TypeAheadPrefixTracker
{
    private static readonly TimeSpan DefaultResetDelay = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _resetDelay;

    private string _buffer = string.Empty;
    private object? _scope;
    private DateTimeOffset _lastInputUtc = DateTimeOffset.MinValue;

    public TypeAheadPrefixTracker(TimeProvider? clock = null, TimeSpan? resetDelay = null)
    {
        _clock = clock ?? TimeProvider.System;
        _resetDelay = resetDelay ?? DefaultResetDelay;
    }

    /// <summary>
    /// Computes the prefix this keystroke would produce without recording it. Returns false
    /// (and leaves all state untouched) for null/whitespace text or text containing control
    /// characters — a rejected keystroke neither extends the prefix nor refreshes the window.
    /// </summary>
    public bool TryPeek(string? text, object scope, out string prefix)
        => TryCompute(text, scope, _clock.GetUtcNow(), out prefix);

    /// <summary>
    /// Computes the prefix and commits it: the buffer, scope, and last-input time all update,
    /// so the next keystroke within the window extends this prefix.
    /// </summary>
    public bool TryAppend(string? text, object scope, out string prefix)
    {
        var now = _clock.GetUtcNow();
        if (!TryCompute(text, scope, now, out prefix))
            return false;

        _buffer = prefix;
        _scope = scope;
        _lastInputUtc = now;
        return true;
    }

    private bool TryCompute(string? text, object scope, DateTimeOffset now, out string prefix)
    {
        prefix = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Any(char.IsControl))
            return false;

        // Strict '>' so input at exactly the reset delay still accumulates.
        prefix = !ReferenceEquals(_scope, scope) || now - _lastInputUtc > _resetDelay
            ? trimmed
            : _buffer + trimmed;
        return true;
    }
}

/// <summary>
/// The matching half of hand-rolled type-ahead: find the next item whose text starts with the
/// prefix, searching forward from the item after the current selection and wrapping around.
/// Searching the full count means a repeated single letter eventually revisits the current
/// item when it is the only match.
/// </summary>
public static class TypeAheadMatcher
{
    /// <param name="startIndex">Index of the current selection, or -1 for none; the search
    /// begins at the following item.</param>
    /// <returns>The index of the next match, or -1 if nothing matches.</returns>
    public static int FindNext<T>(IReadOnlyList<T> items, Func<T, string?> textOf, int startIndex, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return -1;

        for (int i = 1; i <= items.Count; i++)
        {
            var idx = (startIndex + i) % items.Count;
            if ((textOf(items[idx]) ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return idx;
        }

        return -1;
    }
}
