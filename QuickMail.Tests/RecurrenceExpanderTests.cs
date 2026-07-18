using System;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

public class RecurrenceExpanderTests
{
    private static readonly DateTime Mon = new(2026, 7, 13, 9, 0, 0); // a Monday at 09:00

    [Fact]
    public void Daily_EveryDay_FillsWindow()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily };
        var occ = RecurrenceExpander.Expand(Mon, rule, Mon, Mon.AddDays(7));
        Assert.Equal(7, occ.Count);
        Assert.Equal(Mon, occ[0]);
        Assert.All(occ, o => Assert.Equal(new TimeSpan(9, 0, 0), o.TimeOfDay)); // time preserved
    }

    [Fact]
    public void Daily_Interval2_SkipsDays()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Interval = 2 };
        var occ = RecurrenceExpander.Expand(Mon, rule, Mon, Mon.AddDays(7));
        Assert.Equal(new[] { Mon, Mon.AddDays(2), Mon.AddDays(4), Mon.AddDays(6) }, occ);
    }

    [Fact]
    public void Weekly_Default_UsesStartWeekday()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly };
        var occ = RecurrenceExpander.Expand(Mon, rule, Mon, Mon.AddDays(28));
        Assert.Equal(4, occ.Count);
        Assert.All(occ, o => Assert.Equal(DayOfWeek.Monday, o.DayOfWeek));
    }

    [Fact]
    public void Weekly_ByDay_MultipleDaysPerWeek_InOrder()
    {
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly,
            ByDay = { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
        };
        var occ = RecurrenceExpander.Expand(Mon, rule, Mon, Mon.AddDays(7));
        Assert.Equal(3, occ.Count);
        Assert.Equal(DayOfWeek.Monday, occ[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Wednesday, occ[1].DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, occ[2].DayOfWeek);
        // ordering strictly increasing
        Assert.True(occ[0] < occ[1] && occ[1] < occ[2]);
    }

    [Fact]
    public void Count_LimitsTotalOccurrences()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Count = 3 };
        var occ = RecurrenceExpander.Expand(Mon, rule, Mon, Mon.AddYears(1));
        Assert.Equal(3, occ.Count);
    }

    [Fact]
    public void Until_StopsAtDate()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Until = Mon.AddDays(2) };
        var occ = RecurrenceExpander.Expand(Mon, rule, Mon, Mon.AddYears(1));
        Assert.Equal(3, occ.Count); // Mon, +1, +2 inclusive
    }

    [Fact]
    public void Window_ExcludesOccurrencesOutsideRange()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily };
        // Window covers only days 3-5 from start.
        var occ = RecurrenceExpander.Expand(Mon, rule, Mon.AddDays(3), Mon.AddDays(6));
        Assert.Equal(new[] { Mon.AddDays(3), Mon.AddDays(4), Mon.AddDays(5) }, occ);
    }

    [Fact]
    public void Monthly_SameDayOfMonth()
    {
        var start = new DateTime(2026, 1, 15, 10, 0, 0);
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly };
        var occ = RecurrenceExpander.Expand(start, rule, start, start.AddMonths(3));
        Assert.Equal(3, occ.Count);
        Assert.All(occ, o => Assert.Equal(15, o.Day));
    }

    [Fact]
    public void RRule_RoundTrips_WeeklyByDayWithCount()
    {
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 2,
            ByDay = { DayOfWeek.Tuesday, DayOfWeek.Thursday },
            Count = 12,
        };
        var text = rule.ToRRule();
        Assert.Contains("FREQ=WEEKLY", text);
        Assert.Contains("INTERVAL=2", text);
        Assert.Contains("BYDAY=TU,TH", text);
        Assert.Contains("COUNT=12", text);

        var parsed = RecurrenceRule.Parse(text)!;
        Assert.Equal(RecurrenceFrequency.Weekly, parsed.Frequency);
        Assert.Equal(2, parsed.Interval);
        Assert.Equal(new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }, parsed.ByDay.OrderBy(d => (int)d));
        Assert.Equal(12, parsed.Count);
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(RecurrenceRule.Parse(null));
        Assert.Null(RecurrenceRule.Parse(""));
        Assert.Null(RecurrenceRule.Parse("nonsense-without-freq"));
    }

    [Fact]
    public void Parse_HandlesRRulePrefixAndUntil()
    {
        var parsed = RecurrenceRule.Parse("RRULE:FREQ=DAILY;UNTIL=20261231T000000Z")!;
        Assert.Equal(RecurrenceFrequency.Daily, parsed.Frequency);
        Assert.NotNull(parsed.Until);
        Assert.Equal(2026, parsed.Until!.Value.Year);
    }
}
