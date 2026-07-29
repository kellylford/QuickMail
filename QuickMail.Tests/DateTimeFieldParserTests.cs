using System;
using System.Globalization;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Stepping and free-text parsing for the appointment editor's date and time fields.
///
/// Every case pins <see cref="CultureInfo.InvariantCulture"/> explicitly. The parser reads the
/// culture's time separator and AM/PM designators, so a suite that let the ambient culture through
/// would pass on en-US and fail on de-DE for reasons that have nothing to do with the change under
/// test.
/// </summary>
public class DateTimeFieldParserTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A Thursday, 9:15 in the morning, with seconds set so truncation is exercised.</summary>
    private static readonly DateTime Current = new(2026, 7, 16, 9, 15, 30);

    private static readonly DateTime Today = new(2026, 7, 16);

    // ---------------------------------------------------------------- stepping

    [Theory]
    [InlineData(FieldStep.Small, 1, "2026-07-17")]
    [InlineData(FieldStep.Normal, 1, "2026-07-17")]
    [InlineData(FieldStep.Normal, -1, "2026-07-15")]
    [InlineData(FieldStep.Medium, 1, "2026-07-23")]
    [InlineData(FieldStep.Medium, -1, "2026-07-09")]
    [InlineData(FieldStep.Large, 1, "2026-08-16")]
    [InlineData(FieldStep.Large, -1, "2026-06-16")]
    [InlineData(FieldStep.Huge, 1, "2027-07-16")]
    [InlineData(FieldStep.Huge, -1, "2025-07-16")]
    public void StepDate_MovesByTheGestureAmount(FieldStep step, int direction, string expectedDate)
    {
        var result = DateTimeFieldParser.StepDate(Current, step, direction);

        Assert.Equal(DateTime.Parse(expectedDate, Inv).Date, result.Date);
    }

    [Fact]
    public void StepDate_PreservesTimeOfDayIncludingSeconds()
    {
        var result = DateTimeFieldParser.StepDate(Current, FieldStep.Normal, 1);

        Assert.Equal(Current.TimeOfDay, result.TimeOfDay);
    }

    [Fact]
    public void StepDate_MonthEndClampsRatherThanOverflowing()
    {
        var jan31 = new DateTime(2026, 1, 31, 9, 0, 0);

        var result = DateTimeFieldParser.StepDate(jan31, FieldStep.Large, 1);

        // 2026 is not a leap year, so a clamping AddMonths lands on the 28th. An overflowing
        // implementation would produce March 3 and quietly move the appointment a month too far.
        Assert.Equal(new DateTime(2026, 2, 28), result.Date);
    }

    [Theory]
    [InlineData(9, 0, 1, 9, 15)]     // on the grid: up a full step
    [InlineData(9, 0, -1, 8, 45)]    // on the grid: down a full step
    [InlineData(9, 7, 1, 9, 15)]     // off the grid: up snaps forward
    [InlineData(9, 7, -1, 9, 0)]     // off the grid: down snaps back
    [InlineData(9, 15, 1, 9, 30)]
    [InlineData(9, 44, 1, 9, 45)]
    [InlineData(9, 46, -1, 9, 45)]
    public void StepTime_NormalSnapsToTheQuarterHourGrid(
        int hour, int minute, int direction, int expectedHour, int expectedMinute)
    {
        var current = new DateTime(2026, 7, 16, hour, minute, 0);

        var result = DateTimeFieldParser.StepTime(current, FieldStep.Normal, direction);

        Assert.Equal(new DateTime(2026, 7, 16, expectedHour, expectedMinute, 0), result);
    }

    [Fact]
    public void StepTime_DiscardsSecondsWhenSnapping()
    {
        // 9:07:30 must snap from 9:07, not round up to 9:08 first and land somewhere else.
        var result = DateTimeFieldParser.StepTime(Current, FieldStep.Normal, 1);

        Assert.Equal(new DateTime(2026, 7, 16, 9, 30, 0), result);
    }

    [Fact]
    public void StepTime_UpwardAcrossMidnight_AdvancesTheDate()
    {
        var late = new DateTime(2026, 7, 16, 23, 50, 0);

        var result = DateTimeFieldParser.StepTime(late, FieldStep.Normal, 1);

        // The carry is the whole reason a time field holds a full instant. Wrapping to 00:00 on
        // the SAME day would silently move the appointment back twenty-four hours.
        Assert.Equal(new DateTime(2026, 7, 17, 0, 0, 0), result);
    }

    [Fact]
    public void StepTime_DownwardAcrossMidnight_RetreatsTheDate()
    {
        // Starting ON the grid, so the step is a full quarter hour rather than a snap. From an
        // off-grid 00:10 the answer would be 00:00 — the previous grid point, same day.
        var midnight = new DateTime(2026, 7, 16, 0, 0, 0);

        var result = DateTimeFieldParser.StepTime(midnight, FieldStep.Normal, -1);

        Assert.Equal(new DateTime(2026, 7, 15, 23, 45, 0), result);
    }

    [Fact]
    public void StepTime_SmallMovesOneMinuteWithoutSnapping()
    {
        var offGrid = new DateTime(2026, 7, 16, 9, 7, 0);

        var result = DateTimeFieldParser.StepTime(offGrid, FieldStep.Small, 1);

        Assert.Equal(new DateTime(2026, 7, 16, 9, 8, 0), result);
    }

    [Theory]
    [InlineData(FieldStep.Medium, 1, "2026-07-16 10:15")]
    [InlineData(FieldStep.Large, 1, "2026-07-16 10:15")]
    [InlineData(FieldStep.Large, -1, "2026-07-16 08:15")]
    [InlineData(FieldStep.Huge, 1, "2026-07-17 09:15")]
    public void StepTime_LargerGesturesMoveHoursAndDays(FieldStep step, int direction, string expected)
    {
        var result = DateTimeFieldParser.StepTime(Current, step, direction);

        Assert.Equal(DateTime.Parse(expected, Inv), result);
    }

    // ------------------------------------------------------------ time parsing

    [Theory]
    [InlineData("9", 9, 0)]
    [InlineData("14", 14, 0)]
    [InlineData("21", 21, 0)]
    [InlineData("930", 9, 30)]
    [InlineData("0930", 9, 30)]
    [InlineData("1430", 14, 30)]
    [InlineData("9:30", 9, 30)]
    [InlineData("09:05", 9, 5)]
    [InlineData("14:30", 14, 30)]
    [InlineData("9:30 AM", 9, 30)]
    [InlineData("9:30am", 9, 30)]
    [InlineData("9:30 PM", 21, 30)]
    [InlineData("9pm", 21, 0)]
    [InlineData("9p", 21, 0)]
    [InlineData("9a", 9, 0)]
    [InlineData("12:15 AM", 0, 15)]
    [InlineData("12:15 PM", 12, 15)]
    [InlineData("noon", 12, 0)]
    [InlineData("midnight", 0, 0)]
    public void TryParseTime_AcceptsTheFormatsPeopleType(string text, int hour, int minute)
    {
        var ok = DateTimeFieldParser.TryParseTime(text, Current, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 7, 16, hour, minute, 0), result);
    }

    [Theory]
    [InlineData("8/3")]
    [InlineData("8/3/2026")]
    [InlineData("2026-08-03")]
    [InlineData("August 3")]
    [InlineData("tomorrow")]
    public void TryParseTime_RejectsDateShapedText(string text)
    {
        // The shipped bug this replaces: DateTime.TryParse("8/3") succeeds and its TimeOfDay is
        // zero, so typing a date into the time box set the appointment to midnight with no error.
        var ok = DateTimeFieldParser.TryParseTime(text, Current, out var result, Inv);

        Assert.False(ok);
        Assert.Equal(Current, result);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("9:75")]
    [InlineData("13pm")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    public void TryParseTime_RejectsNonsense(string text)
    {
        Assert.False(DateTimeFieldParser.TryParseTime(text, Current, out _, Inv));
    }

    [Fact]
    public void TryParseTime_SignedOffsetRollsTheDate()
    {
        var late = new DateTime(2026, 7, 16, 23, 50, 0);

        var ok = DateTimeFieldParser.TryParseTime("+30", late, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 7, 17, 0, 20, 0), result);
    }

    [Theory]
    [InlineData("+30", "2026-07-16 09:45")]
    [InlineData("-15", "2026-07-16 09:00")]
    [InlineData("+2h", "2026-07-16 11:15")]
    [InlineData("-1h", "2026-07-16 08:15")]
    public void TryParseTime_OffsetsAreRelativeToTheCurrentValue(string text, string expected)
    {
        var ok = DateTimeFieldParser.TryParseTime(text, Current, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(DateTime.Parse(expected, Inv), result);
    }

    // ------------------------------------------------------------ date parsing

    [Theory]
    [InlineData("today", "2026-07-16")]
    [InlineData("tomorrow", "2026-07-17")]
    [InlineData("tom", "2026-07-17")]
    [InlineData("yesterday", "2026-07-15")]
    public void TryParseDate_AcceptsRelativeWords(string text, string expected)
    {
        var ok = DateTimeFieldParser.TryParseDate(text, Current, Today, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(DateTime.Parse(expected, Inv).Date, result.Date);
    }

    [Theory]
    [InlineData("friday", "2026-07-17")]     // Today is Thursday, so Friday is tomorrow.
    [InlineData("fri", "2026-07-17")]
    [InlineData("thursday", "2026-07-16")]   // A bare weekday can mean today.
    [InlineData("next thursday", "2026-07-23")]  // "next" never does.
    [InlineData("wed", "2026-07-22")]        // Wednesday has passed; the next one is a week out.
    public void TryParseDate_ResolvesWeekdays(string text, string expected)
    {
        var ok = DateTimeFieldParser.TryParseDate(text, Current, Today, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(DateTime.Parse(expected, Inv).Date, result.Date);
    }

    [Theory]
    [InlineData("+7", "2026-07-23")]
    [InlineData("-3", "2026-07-13")]
    [InlineData("+2w", "2026-07-30")]
    [InlineData("+1m", "2026-08-16")]
    [InlineData("+1y", "2027-07-16")]
    [InlineData("+10d", "2026-07-26")]
    public void TryParseDate_AcceptsSignedOffsets(string text, string expected)
    {
        var ok = DateTimeFieldParser.TryParseDate(text, Current, Today, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(DateTime.Parse(expected, Inv).Date, result.Date);
    }

    [Fact]
    public void TryParseDate_OffsetsAreRelativeToTheFieldNotToToday()
    {
        // The field has already been moved a month past today; "+7" must mean a week beyond that.
        var moved = new DateTime(2026, 8, 16, 9, 15, 0);

        var ok = DateTimeFieldParser.TryParseDate("+7", moved, Today, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 8, 23), result.Date);
    }

    [Theory]
    [InlineData("08/03/2026", "2026-08-03")]
    [InlineData("2026-08-03", "2026-08-03")]
    [InlineData("August 3, 2026", "2026-08-03")]
    public void TryParseDate_AcceptsAbsoluteDates(string text, string expected)
    {
        var ok = DateTimeFieldParser.TryParseDate(text, Current, Today, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(DateTime.Parse(expected, Inv).Date, result.Date);
    }

    [Fact]
    public void TryParseDate_PreservesTheTimeOfDay()
    {
        // A date field must never reset the appointment's time as a side effect of moving its day.
        var ok = DateTimeFieldParser.TryParseDate("2026-08-03", Current, Today, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(Current.TimeOfDay, result.TimeOfDay);
    }

    [Fact]
    public void TryParseDate_BareDayNumberMeansThatDayOfTheShownMonth()
    {
        var ok = DateTimeFieldParser.TryParseDate("3", Current, Today, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 7, 3), result.Date);
    }

    [Fact]
    public void TryParseDate_BareDayNumberBeyondTheMonthIsRejected()
    {
        var february = new DateTime(2026, 2, 10, 9, 0, 0);

        Assert.False(DateTimeFieldParser.TryParseDate("31", february, Today, out _, Inv));
    }

    [Theory]
    [InlineData("9:30")]
    [InlineData("9:30 AM")]
    [InlineData("noon")]
    [InlineData("midnight")]
    [InlineData("930p")]
    public void TryParseDate_RejectsTimeShapedText(string text)
    {
        // Mirror of the time field's guard: a time typed into the date box would otherwise resolve
        // to today and move the appointment.
        var ok = DateTimeFieldParser.TryParseDate(text, Current, Today, out var result, Inv);

        Assert.False(ok);
        Assert.Equal(Current, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("+")]
    [InlineData("13/45/2026")]
    public void TryParseDate_RejectsNonsenseAndLeavesTheValueAlone(string text)
    {
        var ok = DateTimeFieldParser.TryParseDate(text, Current, Today, out var result, Inv);

        Assert.False(ok);
        Assert.Equal(Current, result);
    }

    [Fact]
    public void TryParseDate_MonthNamedWordIsNotMistakenForTheMeridiemShorthand()
    {
        // "sep" ends with the bare "p" shorthand for PM; the time-shape guard must not eat it.
        var ok = DateTimeFieldParser.TryParseDate("Sep 3, 2026", Current, Today, out var result, Inv);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 9, 3), result.Date);
    }
}
