using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Controls;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Typing into the appointment editor's date and time fields (issue #570, and the appointment that
/// consequently saved on the wrong day in #519).
///
/// The fields open holding a fully formatted value — "Thursday, July 16, 2026" — so what the caret
/// and selection do when the user arrives decides whether the first keystroke replaces that value
/// or is appended to it. Appending produced "Thursday, July 16, 20268/3", which parses as nothing,
/// so <c>Commit</c> silently restored the old value: the field looked like it ignored typing, and
/// an appointment saved from it carried a date the user never entered.
///
/// The failure is invisible to inspection — the field still shows a perfectly good date afterwards
/// — which is why it is pinned here at the window level, driving the real control the editor uses.
/// </summary>
// Loads a real Window, so it joins the collection that serializes window-loading tests (#590).
[Collection("WpfTests")]
public class DateTimeFieldEntryTests
{
    private static readonly DateTime Start = new(2026, 7, 16, 9, 3, 0);

    /// <summary>
    /// An open editor window that closes when the test ends, pass or fail, with the culture pinned
    /// for as long as it is open.
    ///
    /// <para>
    /// The control formats and parses with <see cref="CultureInfo.CurrentCulture"/>, so every
    /// literal below — "Thursday, July 16, 2026", and "8/3" meaning the third of August — is only
    /// true on an en-US machine. Measured under de-DE without this: the field reads
    /// "Donnerstag, 16. Juli 2026" and "8/3" commits the 8th of March. That is the ambient culture
    /// deciding the result, not the change under test, which is the same reason
    /// <see cref="DateTimeFieldParserTests"/> pins a culture on every case.
    /// </para>
    /// </summary>
    private sealed class Editor : IDisposable
    {
        private readonly CultureInfo _culture = Thread.CurrentThread.CurrentCulture;

        public EventEditorViewModel Vm { get; }
        public EventEditorWindow Window { get; }

        public Editor()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
            Vm = new EventEditorViewModel(Start);
            Window = new EventEditorWindow(Vm);
            Window.Show();
        }

        public T Field<T>(string name) where T : class
        {
            var field = Window.FindName(name) as T;
            Assert.NotNull(field);
            return field!;
        }

        public void Dispose()
        {
            Window.Close();
            Thread.CurrentThread.CurrentCulture = _culture;
        }
    }

    /// <summary>
    /// Types text the way a keyboard user does: the first character replaces whatever is selected,
    /// and each one after it lands at the caret. Assigning <c>SelectedText</c> leaves the inserted
    /// character selected, so the caret is collapsed past it between keystrokes — without that,
    /// every character would overwrite the one before and only the last would survive.
    ///
    /// <para>
    /// This is a fair model of what a keystroke does to the text, but it does not go through the
    /// key pipeline — no <c>PreviewKeyDown</c>, no <c>PreviewTextInput</c>, no window-level
    /// handling. A character stolen before the TextBox ever sees it would not fail these tests.
    /// Driving real keystrokes needs the opt-in input suite (see <c>InputTests</c> in CLAUDE.md).
    /// </para>
    /// </summary>
    private static void Type(TextBox box, string text)
    {
        foreach (var ch in text)
        {
            box.SelectedText = ch.ToString();
            box.Select(box.SelectionStart + box.SelectionLength, 0);
        }
    }

    /// <summary>Moves focus onward, which is what commits a field the user tabs out of.</summary>
    private static void TabAway(Control field) =>
        field.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));

    [StaFact]
    public void EnteringADateField_SelectsTheValue_SoTypingReplacesIt()
    {
        using var editor = new Editor();
        var field = editor.Field<DateTimeField>("StartDateField");

        field.Focus();

        Assert.Equal("Thursday, July 16, 2026", field.Text);
        Assert.Equal(field.Text.Length, field.SelectionLength);
    }

    [StaFact]
    public void EnteringATimeField_SelectsTheValue_SoTypingReplacesIt()
    {
        using var editor = new Editor();
        var field = editor.Field<DateTimeField>("StartTimeField");

        field.Focus();

        Assert.NotEqual(0, field.Text.Length);
        Assert.Equal(field.Text.Length, field.SelectionLength);
    }

    [StaFact]
    public void TypingADate_ThenLeavingTheField_CommitsWhatWasTyped()
    {
        using var editor = new Editor();
        var field = editor.Field<DateTimeField>("StartDateField");

        field.Focus();
        Type(field, "8/3");
        TabAway(field);

        // The date the user typed, not the one the field opened on.
        Assert.Equal(new DateTime(2026, 8, 3), editor.Vm.Start.Date);
        Assert.Equal("Monday, August 3, 2026", field.Text);
    }

    [StaFact]
    public void TypingATime_ThenLeavingTheField_CommitsWhatWasTyped()
    {
        using var editor = new Editor();
        var field = editor.Field<DateTimeField>("StartTimeField");

        field.Focus();
        Type(field, "2:45 PM");
        TabAway(field);

        Assert.Equal(new TimeSpan(14, 45, 0), editor.Vm.Start.TimeOfDay);
    }

    /// <summary>
    /// The whole point of #570: an appointment saved straight after typing a date carries that
    /// date. Before the fix the typed text was appended, failed to parse, and the event was built
    /// from the value the editor opened on — so it landed on today and was not where the user
    /// went looking for it in the agenda (#519).
    /// </summary>
    [StaFact]
    public void SavingAfterTypingADate_BuildsTheEventOnTheTypedDate()
    {
        using var editor = new Editor();
        var title = editor.Field<TextBox>("TitleBox");
        var field = editor.Field<DateTimeField>("StartDateField");

        title.Text = "Dentist";
        field.Focus();
        Type(field, "8/3");
        TabAway(field);

        Assert.True(editor.Vm.TryBuildEvent(out var evt, out var error), error);
        var start = new DateTime(evt.StartTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(new DateTime(2026, 8, 3), start.Date);
    }

    /// <summary>
    /// Focus coming BACK to a field that is part-way through an entry must not re-select it.
    ///
    /// Typing is uncommitted until Enter or focus loss, and a modal opened over the editor —
    /// Ctrl+Shift+P opens the command palette exactly this way — takes keyboard focus and gives it
    /// back without either happening. Selecting the half-typed text on the way back in would let
    /// the next keystroke wipe it: "August " then "3" becomes "3", read as the third of the month
    /// showing, and the appointment saves on a date nobody typed. That is the same silent failure
    /// as #570 itself, which is why selecting on entry is limited to the field's own formatted
    /// value.
    /// </summary>
    [StaFact]
    public void ReturningToAPartlyTypedField_LeavesWhatWasTypedAlone()
    {
        using var editor = new Editor();
        var field = editor.Field<DateTimeField>("StartDateField");

        field.Focus();
        Type(field, "Aug");

        // A modal that opens over the editor and closes again — the command palette's shape.
        var modal = new Window { Owner = editor.Window, Width = 100, Height = 100 };
        modal.Loaded += (_, _) => modal.Close();
        modal.ShowDialog();

        Assert.Equal("Aug", field.Text);
        Assert.Equal(0, field.SelectionLength);
        Assert.Equal(3, field.CaretIndex);

        // So the entry can be finished where it left off.
        Type(field, "ust 3");
        TabAway(field);
        Assert.Equal(new DateTime(2026, 8, 3), editor.Vm.Start.Date);
    }

    /// <summary>
    /// Stepping keeps the reading it was given deliberately: the new value is left unselected with
    /// the caret at the end, so a screen reader speaks the value change and not a selection.
    /// </summary>
    [StaFact]
    public void SteppingWithAnArrow_LeavesTheValueUnselected()
    {
        using var editor = new Editor();
        var field = editor.Field<DateTimeField>("StartDateField");

        field.Focus();
        field.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor.Window), 0, Key.Up)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });

        Assert.Equal("Friday, July 17, 2026", field.Text);
        Assert.Equal(0, field.SelectionLength);
        Assert.Equal(field.Text.Length, field.CaretIndex);
    }
}
