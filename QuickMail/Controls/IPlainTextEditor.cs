using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;

namespace QuickMail.Controls;

/// <summary>
/// What the compose window needs from a plain-text editor: the text, the selection, focus, and
/// where the misspellings are. It is shaped after <see cref="TextBox"/> so the window's code reads
/// the same whichever editor is behind it: the WPF <see cref="EditorTextBox"/>
/// (<see cref="WpfTextEditor"/>) or the hosted WinUI editor (<see cref="WinUiEditorHost"/>, the
/// #1099 experiment).
///
/// <para>Every index is a character index into <see cref="Text"/>, whose line breaks are
/// "\r\n".</para>
/// </summary>
internal interface IPlainTextEditor
{
    string Text { get; }
    int CaretIndex { get; set; }
    int SelectionStart { get; set; }
    int SelectionLength { get; set; }

    /// <summary>
    /// Setting this replaces the selection as one undo step and leaves the new text selected, as
    /// <see cref="TextBox.SelectedText"/> does.
    /// </summary>
    string SelectedText { get; set; }

    void Select(int start, int length);
    bool Focus();
    bool IsKeyboardFocusWithin { get; }

    /// <summary>Scrolls the line holding <paramref name="index"/> into view.</summary>
    void ScrollTo(int index);

    /// <summary>
    /// The start of the nearest misspelling from <paramref name="charIndex"/>, or -1. Forward, the
    /// misspelling <paramref name="charIndex"/> is in counts; backward, the nearest one starting at
    /// or before it. As <see cref="TextBoxBase"/>'s method of the same name.
    /// </summary>
    int GetNextSpellingErrorCharacterIndex(int charIndex, LogicalDirection direction);

    /// <summary>The start of the misspelling <paramref name="charIndex"/> is in, or -1.</summary>
    int GetSpellingErrorStart(int charIndex);

    /// <summary>The length of the misspelling <paramref name="charIndex"/> is in, or 0.</summary>
    int GetSpellingErrorLength(int charIndex);

    /// <summary>
    /// Suggestions for the misspelling <paramref name="charIndex"/> is in (possibly none), or null
    /// when it is not in one.
    /// </summary>
    IReadOnlyList<string>? GetSpellingSuggestions(int charIndex);
}

/// <summary>A WPF <see cref="TextBox"/> as an <see cref="IPlainTextEditor"/>.</summary>
internal sealed class WpfTextEditor : IPlainTextEditor
{
    private readonly TextBox _box;

    public WpfTextEditor(TextBox box) => _box = box;

    public string Text => _box.Text;
    public int CaretIndex { get => _box.CaretIndex; set => _box.CaretIndex = value; }
    public int SelectionStart { get => _box.SelectionStart; set => _box.SelectionStart = value; }
    public int SelectionLength { get => _box.SelectionLength; set => _box.SelectionLength = value; }
    public string SelectedText { get => _box.SelectedText; set => _box.SelectedText = value; }
    public void Select(int start, int length) => _box.Select(start, length);
    public bool Focus() => _box.Focus();
    public bool IsKeyboardFocusWithin => _box.IsKeyboardFocusWithin;

    public void ScrollTo(int index)
    {
        try
        {
            _box.ScrollToLine(_box.GetLineIndexFromCharacterIndex(index));
        }
        catch (System.ArgumentOutOfRangeException)
        {
            // Layout not measured yet (headless tests): the selection alone is enough.
        }
    }

    public int GetNextSpellingErrorCharacterIndex(int charIndex, LogicalDirection direction) =>
        _box.GetNextSpellingErrorCharacterIndex(charIndex, direction);

    public int GetSpellingErrorStart(int charIndex) => _box.GetSpellingErrorStart(charIndex);
    public int GetSpellingErrorLength(int charIndex) => _box.GetSpellingErrorLength(charIndex);

    public IReadOnlyList<string>? GetSpellingSuggestions(int charIndex) =>
        _box.GetSpellingError(charIndex)?.Suggestions.ToList();
}
