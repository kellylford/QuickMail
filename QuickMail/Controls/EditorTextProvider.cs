using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using QuickMail.Helpers;

namespace QuickMail.Controls;

/// <summary>
/// The compose editors' UI Automation text provider: WPF's own, passed through, with what it
/// tells screen readers about lines and paragraphs corrected.
///
/// <para><b>Empty lines (issue #891).</b> With the caret on an empty line, WPF's
/// <c>ExpandToEnclosingUnit(Line)</c> returns the empty line's break <i>and the whole next
/// line</i>, in a TextBox and a RichTextBox alike. A screen reader drops the leading break, so the
/// blank line is read as the next line, and the next Down reads that line again. Every range here
/// that is expanded or moved to a line or paragraph and starts with a break, with text after it,
/// is cut back to that break, which screen readers read as "blank".</para>
///
/// <para><b>Paragraphs in plain text (issue #1077).</b> A TextBox's Paragraph unit is the
/// whole text. When <c>plainText</c> is given (the plain-text and Markdown editor), Paragraph
/// is answered from the text itself, using <see cref="PlainTextParagraphs"/>: the runs between
/// hard line breaks. The rich editor's paragraphs are WPF's own and are right, apart from the
/// empty-line fix.</para>
///
/// <para>Everything else is WPF's. WPF's provider bridge marshals every call to the editor's
/// UI thread before it reaches here, as it does for its own provider, and unwraps the ranges a
/// client passes back, so the <see cref="Range"/> wrappers only ever meet each other.</para>
/// </summary>
internal sealed class EditorTextProvider : ITextProvider
{
    private readonly ITextProvider _inner;
    private readonly Func<string>? _plainText;

    public EditorTextProvider(ITextProvider inner, Func<string>? plainText)
    {
        _inner = inner;
        _plainText = plainText;
    }

    public ITextProvider Inner => _inner;

    public ITextRangeProvider DocumentRange => Wrap(_inner.DocumentRange)!;
    public SupportedTextSelection SupportedTextSelection => _inner.SupportedTextSelection;
    public ITextRangeProvider[] GetSelection() => _inner.GetSelection().Select(r => Wrap(r)!).ToArray();
    public ITextRangeProvider[] GetVisibleRanges() => _inner.GetVisibleRanges().Select(r => Wrap(r)!).ToArray();
    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => Wrap(_inner.RangeFromChild(childElement))!;
    public ITextRangeProvider RangeFromPoint(Point screenLocation) => Wrap(_inner.RangeFromPoint(screenLocation))!;

    private Range? Wrap(ITextRangeProvider? range) => range is null ? null : new Range(this, range);

    private static ITextRangeProvider Unwrap(ITextRangeProvider range) => range is Range r ? r.Inner : range;

    /// <summary>A range of the editor's text: WPF's range, with lines and paragraphs corrected.</summary>
    internal sealed class Range : ITextRangeProvider
    {
        private readonly EditorTextProvider _owner;

        public Range(EditorTextProvider owner, ITextRangeProvider inner)
        {
            _owner = owner;
            Inner = inner;
        }

        public ITextRangeProvider Inner { get; }

        private bool PlainParagraphs(TextUnit unit) => unit == TextUnit.Paragraph && _owner._plainText != null;

        public ITextRangeProvider Clone() => new Range(_owner, Inner.Clone());
        public bool Compare(ITextRangeProvider range) => Inner.Compare(Unwrap(range));
        public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
            => Inner.CompareEndpoints(endpoint, Unwrap(targetRange), targetEndpoint);

        public void ExpandToEnclosingUnit(TextUnit unit)
        {
            if (PlainParagraphs(unit))
            {
                var text = _owner._plainText!();
                var (start, end) = PlainTextParagraphs.Enclosing(text, Offset(TextPatternRangeEndpoint.Start));
                SetOffsets(text, start, end);
                return;
            }
            Inner.ExpandToEnclosingUnit(unit);
            if (unit is TextUnit.Line or TextUnit.Paragraph) TrimEmptyLine();
        }

        public int Move(TextUnit unit, int count)
        {
            if (PlainParagraphs(unit)) return MovePlainParagraph(count);
            bool degenerate = IsDegenerate();
            var before = Inner.Clone();
            int moved = Inner.Move(unit, count);
            if (!degenerate && unit is TextUnit.Line or TextUnit.Paragraph) TrimEmptyLine();
            // WPF's RichTextBox says it moved one line from its last line while staying put. A
            // screen reader reading to the end moves until a move reports 0, so it never stopped.
            return moved != 0 && Inner.Compare(before) ? 0 : moved;
        }

        public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
        {
            if (PlainParagraphs(unit)) return MovePlainParagraphEndpoint(endpoint, count);
            // A line built from the caret by moving End one line on, the other way of asking
            // for "the line here", gets the same empty-line fix as ExpandToEnclosingUnit.
            bool lineFromCaret = endpoint == TextPatternRangeEndpoint.End && count == 1
                && unit is TextUnit.Line or TextUnit.Paragraph && IsDegenerate();
            int moved = Inner.MoveEndpointByUnit(endpoint, unit, count);
            if (lineFromCaret) TrimEmptyLine();
            return moved;
        }

        public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
            => Inner.MoveEndpointByRange(endpoint, Unwrap(targetRange), targetEndpoint);

        public ITextRangeProvider? FindAttribute(int attribute, object value, bool backward) => _owner.Wrap(Inner.FindAttribute(attribute, value, backward));
        public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase) => _owner.Wrap(Inner.FindText(text, backward, ignoreCase));
        public object GetAttributeValue(int attribute) => Inner.GetAttributeValue(attribute);
        public double[] GetBoundingRectangles() => Inner.GetBoundingRectangles();
        public IRawElementProviderSimple GetEnclosingElement() => Inner.GetEnclosingElement();
        public string GetText(int maxLength) => Inner.GetText(maxLength);
        public void Select() => Inner.Select();
        public void AddToSelection() => Inner.AddToSelection();
        public void RemoveFromSelection() => Inner.RemoveFromSelection();
        public void ScrollIntoView(bool alignToTop) => Inner.ScrollIntoView(alignToTop);
        public IRawElementProviderSimple[] GetChildren() => Inner.GetChildren();

        private bool IsDegenerate()
            => Inner.CompareEndpoints(TextPatternRangeEndpoint.Start, Inner, TextPatternRangeEndpoint.End) == 0;

        /// <summary>
        /// A line that starts with a line break and has text after it is an empty line WPF has
        /// glued to the next one (#891): keep only the break.
        /// </summary>
        private void TrimEmptyLine()
        {
            var text = Inner.GetText(-1);
            if (text.Length == 0 || text[0] is not ('\r' or '\n')) return;
            if (text.TrimStart('\r', '\n').Length == 0) return;
            Inner.MoveEndpointByRange(TextPatternRangeEndpoint.End, Inner, TextPatternRangeEndpoint.Start);
            Inner.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
        }

        // ---- Plain-text paragraphs. Offsets are string indexes into the editor's text. ----

        /// <summary>Where an endpoint is, as a string index: the length of the text before it.</summary>
        private int Offset(TextPatternRangeEndpoint endpoint)
        {
            var before = _owner._inner.DocumentRange;
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, Inner, endpoint);
            return before.GetText(-1).Length;
        }

        /// <summary>
        /// Puts this range at string indexes. Start goes first: if it passes End, UI Automation
        /// brings End along, and End is then moved from wherever it ended up.
        /// </summary>
        private void SetOffsets(string text, int start, int end)
        {
            MoveEndpointTo(TextPatternRangeEndpoint.Start, text, start);
            MoveEndpointTo(TextPatternRangeEndpoint.End, text, end);
        }

        /// <summary>
        /// Moves one endpoint to a string index, from where it already is. WPF's Character unit
        /// is slow (a move across 18,000 characters took about a second), so it is only ever asked
        /// to cross the short distance to the target, never the text before it. Its characters are
        /// insertion positions, not string indexes: a <c>\r\n</c>, an emoji's surrogate pair and a
        /// letter with a combining accent are one each, which is what .NET's text elements count.
        /// Where WPF still disagrees (some joined emoji sequences), it is nudged one character at
        /// a time, checked against <see cref="Offset"/>.
        /// </summary>
        private void MoveEndpointTo(TextPatternRangeEndpoint endpoint, string text, int target)
        {
            int current = Offset(endpoint);
            if (current == target) return;
            int low = Math.Clamp(Math.Min(current, target), 0, text.Length);
            int high = Math.Clamp(Math.Max(current, target), 0, text.Length);
            int elements = new System.Globalization.StringInfo(text.Substring(low, high - low)).LengthInTextElements;
            Inner.MoveEndpointByUnit(endpoint, TextUnit.Character, target > current ? elements : -elements);
            for (int i = 0; i < 8; i++)
            {
                current = Offset(endpoint);
                if (current == target) return;
                if (Inner.MoveEndpointByUnit(endpoint, TextUnit.Character, current < target ? 1 : -1) == 0) return;
            }
        }

        private int MovePlainParagraph(int count)
        {
            var text = _owner._plainText!();
            bool degenerate = IsDegenerate();
            int position = PlainTextParagraphs.Start(text, Offset(TextPatternRangeEndpoint.Start));
            int moved = 0;
            while (moved < count && PlainTextParagraphs.NextStart(text, position) is { } next) { position = next; moved++; }
            while (moved > count && PlainTextParagraphs.PreviousStart(text, position) is { } previous) { position = previous; moved--; }
            if (degenerate) SetOffsets(text, position, position);
            else
            {
                var (start, end) = PlainTextParagraphs.Enclosing(text, position);
                SetOffsets(text, start, end);
            }
            return moved;
        }

        private int MovePlainParagraphEndpoint(TextPatternRangeEndpoint endpoint, int count)
        {
            var text = _owner._plainText!();
            int start = Offset(TextPatternRangeEndpoint.Start), end = Offset(TextPatternRangeEndpoint.End);
            int position = endpoint == TextPatternRangeEndpoint.Start ? start : end;
            int moved = 0;
            while (moved < count && position < text.Length)
            {
                position = PlainTextParagraphs.NextStart(text, position) ?? text.Length;
                moved++;
            }
            while (moved > count && position > 0)
            {
                int own = PlainTextParagraphs.Start(text, position);
                position = own < position ? own : PlainTextParagraphs.PreviousStart(text, own) ?? 0;
                moved--;
            }
            // An endpoint moved past the other one takes it along, as UI Automation requires.
            if (endpoint == TextPatternRangeEndpoint.Start) SetOffsets(text, position, Math.Max(position, end));
            else SetOffsets(text, Math.Min(start, position), position);
            return moved;
        }
    }
}
