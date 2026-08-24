using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Helpers;

namespace QuickMail.Controls;

/// <summary>What a <see cref="DateTimeField"/> holds, which decides its format and step sizes.</summary>
public enum DateTimeFieldKind
{
    /// <summary>A calendar date. <see cref="DateTimeField.Value"/> is a DateTime; the time of day
    /// rides along untouched so a date field and a time field can share one property.</summary>
    Date,

    /// <summary>A time of day. Value is a DateTime whose date part carries midnight crossings.</summary>
    Time,

    /// <summary>A plain whole number, bounded by Minimum and Maximum.</summary>
    Number,
}

/// <summary>
/// An editable field that also steps with the arrow keys: Up and Down move it by a day or a
/// quarter hour, Shift and Page by larger units, and typing accepts everything from "8/3" to
/// "tomorrow" to "+7". Replaces the stock <c>DatePicker</c> and the free-text time boxes in the
/// appointment editor.
///
/// It is deliberately a plain <see cref="TextBox"/> subclass with no automation peer of its own.
/// Three shapes were built and listened to with three screen readers before this one was chosen —
/// an edit field, an edit field claiming the Spinner role, and a purpose-built spinner control
/// with Value and RangeValue providers. All three announced correctly, so the one that invents
/// nothing wins: replacing <see cref="TextBox.Text"/> raises the UIA value-change event that
/// screen readers already act on, and the field keeps every editing affordance a real edit field
/// has. Do not add a custom peer or a QuickMail announcement here — neither is needed, and a
/// programmatic announcement would be filtered by the user's announcement settings while the
/// native value change is not.
///
/// Because implicit styles key on the exact type, the app theme dictionaries carry a one-line
/// <c>BasedOn</c> style for this type; without it a TextBox subclass renders unthemed.
/// </summary>
public class DateTimeField : TextBox
{
    /// <summary>Set while this control is rewriting its own Text, so the change is not re-parsed.</summary>
    private bool _updatingText;

    public DateTimeField()
    {
        // Single-line: a date or a time never wraps, and AcceptsReturn would swallow the Enter
        // that saves the appointment.
        AcceptsReturn = false;
    }

    // ------------------------------------------------------------- dependency properties

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(DateTimeFieldKind), typeof(DateTimeField),
            new FrameworkPropertyMetadata(DateTimeFieldKind.Date, OnFormatAffectingChanged));

    public DateTimeFieldKind Kind
    {
        get => (DateTimeFieldKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>
    /// The field's value: a <see cref="DateTime"/> for Date and Time, an <see cref="int"/> for
    /// Number, or null for an empty field when <see cref="AllowEmpty"/> is set.
    ///
    /// Typed as object so one control can serve a non-nullable DateTime (Start, End), a nullable
    /// one (Repeat until) and an int (Repeat interval) without three parallel properties and three
    /// sets of binding plumbing.
    /// </summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(object), typeof(DateTimeField),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnFormatAffectingChanged));

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// What an empty field adopts the first time it is stepped. Only meaningful with
    /// <see cref="AllowEmpty"/>: the Repeat until field starts blank, and stepping it has to land
    /// somewhere sensible rather than on whatever today happens to be.
    /// </summary>
    public static readonly DependencyProperty SeedValueProperty =
        DependencyProperty.Register(nameof(SeedValue), typeof(object), typeof(DateTimeField),
            new PropertyMetadata(null));

    public object? SeedValue
    {
        get => GetValue(SeedValueProperty);
        set => SetValue(SeedValueProperty, value);
    }

    /// <summary>Whether clearing the text is allowed and means "no value".</summary>
    public static readonly DependencyProperty AllowEmptyProperty =
        DependencyProperty.Register(nameof(AllowEmpty), typeof(bool), typeof(DateTimeField),
            new PropertyMetadata(false));

    public bool AllowEmpty
    {
        get => (bool)GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    /// <summary>Number kind only: the lowest value stepping or typing may produce.</summary>
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(int), typeof(DateTimeField),
            new PropertyMetadata(1));

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Number kind only: the highest value stepping or typing may produce.</summary>
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(DateTimeField),
            new PropertyMetadata(999));

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private static void OnFormatAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DateTimeField)d).RefreshText();

    // ----------------------------------------------------------------------- rendering

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        RefreshText();
    }

    private void RefreshText()
    {
        var formatted = FormatValue();
        if (Text == formatted) return;

        _updatingText = true;
        try
        {
            Text = formatted;
            // Caret to the end rather than selecting the whole value. Both were built and listened
            // to; leaving the text unselected is the one that reads cleanly. This is the *stepped*
            // value — arriving at the field is the other case, and OnGotKeyboardFocus does select
            // there so the first keystroke replaces rather than appends.
            CaretIndex = Text.Length;
        }
        finally { _updatingText = false; }
    }

    private string FormatValue() => Value switch
    {
        null => string.Empty,
        // "D" is the culture's long date — "Tuesday, July 28, 2026". Spelled out rather than
        // 7/28/2026 because the field is read aloud far more often than it is glanced at, and a
        // slash-separated date is ambiguous between cultures in a way the long form is not.
        DateTime d when Kind == DateTimeFieldKind.Date => d.ToString("D", CultureInfo.CurrentCulture),
        DateTime d when Kind == DateTimeFieldKind.Time => d.ToString("t", CultureInfo.CurrentCulture),
        int n => n.ToString(CultureInfo.CurrentCulture),
        _ => string.Empty,
    };

    // ------------------------------------------------------------------ key handling

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (TryMapGesture(e.Key, Keyboard.Modifiers, out var step, out var direction))
        {
            Step(step, direction);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            // Commit before Enter reaches the default Save button, so a save triggered from this
            // field uses what the user just typed. A value that will not parse blocks the save
            // instead: the text reverts to the last good value, which is both visible and spoken,
            // and pressing Enter again then saves the appointment the field actually holds.
            if (!Commit()) e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Maps a keystroke to a stepping gesture. Deliberately not registered in
    /// <c>CommandRegistry</c>: these are in-field editing keys, like the arrow keys inside any
    /// text box, not application commands. Registering them would fill the command palette with
    /// ten entries that do nothing anywhere else in the app.
    /// </summary>
    private static bool TryMapGesture(Key key, ModifierKeys modifiers, out FieldStep step, out int direction)
    {
        step = FieldStep.Normal;
        direction = 0;

        var ctrl = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        if ((modifiers & ModifierKeys.Alt) != 0) return false;

        switch (key)
        {
            case Key.Up: direction = 1; break;
            case Key.Down: direction = -1; break;
            case Key.PageUp: direction = 1; break;
            case Key.PageDown: direction = -1; break;
            default: return false;
        }

        var paging = key is Key.PageUp or Key.PageDown;
        step = paging
            ? ctrl ? FieldStep.Huge : FieldStep.Large
            : ctrl ? FieldStep.Small : shift ? FieldStep.Medium : FieldStep.Normal;
        return true;
    }

    private void Step(FieldStep step, int direction)
    {
        if (Kind == DateTimeFieldKind.Number)
        {
            var amount = step switch
            {
                FieldStep.Medium => 5,
                FieldStep.Large or FieldStep.Huge => 10,
                _ => 1,
            };
            var current = Value as int? ?? Minimum;
            Value = Math.Clamp(current + amount * direction, Minimum, Maximum);
            return;
        }

        // An empty field steps from its seed rather than from nothing, and the seed itself is the
        // first value the user lands on — stepping a blank "Repeat until" up should offer the
        // seed, not the day after it.
        if (Value is not DateTime instant)
        {
            Value = SeedValue as DateTime? ?? DateTime.Today;
            return;
        }

        Value = Kind == DateTimeFieldKind.Date
            ? DateTimeFieldParser.StepDate(instant, step, direction)
            : DateTimeFieldParser.StepTime(instant, step, direction);
    }

    // -------------------------------------------------------------------- committing

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        // Typing is left uncommitted until Enter or focus loss. Parsing every keystroke would fight
        // the user halfway through "August" and push half-formed dates into the ViewModel, where
        // duration linking would drag the end along with each one.
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // Select the value on entry, so the first character typed replaces it.
        //
        // Without this the field is entered with the caret parked after "Thursday, July 16, 2026"
        // and nothing selected, so typing a date appends to the formatted one: "8/3" becomes
        // "Thursday, July 16, 20268/3", which parses as nothing, and Commit quietly puts the old
        // value back. The field reads as if it ignores typing until the user selects the text
        // themselves, and — worse — an appointment then saves on a date they did not enter
        // (issues #570 and #519). Nothing is announced either, because the reverted text equals
        // the text already there, so RefreshText finds no change and raises no UIA value change.
        //
        // Selecting the value is what a formatted value field does everywhere: the editor's own
        // Title box does it on open, and FocusField does it when a refused save sends focus back
        // to the offending field. Stepping is untouched — RefreshText still leaves the caret at
        // the end with no selection, which is the reading that was chosen deliberately.
        //
        // Only ever the field's OWN formatted value, though. Keyboard focus comes back here for
        // reasons that are not the user arriving at a fresh field: closing the command palette
        // that Ctrl+Shift+P opens over the editor, or the window being reactivated. Typing is
        // uncommitted until Enter or focus loss, and neither of those happens on that round trip
        // — so the field still holds a half-finished entry, and re-selecting it would let the very
        // next keystroke wipe it. Typing "August ", opening and closing the palette, then typing
        // "3" would leave "3", which reads as the 3rd of the month shown: the same silent
        // wrong-date save this override exists to prevent, just arrived at differently.
        if (Text != FormatValue()) return;
        SelectAll();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        Commit();
        base.OnLostFocus(e);
    }

    /// <summary>
    /// Parses the typed text into <see cref="Value"/>. Returns false when it could not be parsed,
    /// having first put the last good value back so the field never holds text that means nothing.
    /// </summary>
    private bool Commit()
    {
        if (_updatingText) return true;

        var text = Text?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            if (AllowEmpty) { Value = null; return true; }
            RefreshText();
            return false;
        }

        if (Kind == DateTimeFieldKind.Number)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number))
            {
                Value = Math.Clamp(number, Minimum, Maximum);
                RefreshText();   // shows the clamp, so an out-of-range entry is visibly corrected
                return true;
            }
            RefreshText();
            return false;
        }

        var current = Value as DateTime? ?? SeedValue as DateTime? ?? DateTime.Today;
        var parsed = Kind == DateTimeFieldKind.Date
            ? DateTimeFieldParser.TryParseDate(text, current, DateTime.Today, out var result)
            : DateTimeFieldParser.TryParseTime(text, current, out result);

        if (!parsed)
        {
            RefreshText();
            return false;
        }

        Value = result;
        RefreshText();   // normalises "8/3" to the field's full display form
        return true;
    }
}
