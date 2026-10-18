using System.Collections.Generic;
using System.Windows.Threading;
using Xunit;
using Xunit.v3;

namespace QuickMail.Tests;

/// <summary>
/// Guards <see cref="ShutDownStaDispatcherAttribute"/>: a StaTheory's rows share one STA thread, so
/// its Dispatcher must survive until the last row, and must still be shut down whenever the number
/// of rows that will actually run cannot be trusted (issue #211).
/// </summary>
public class StaDispatcherShutdownTests
{
    // Every row checks the Dispatcher is still alive. Before the fix the first row's After() shut it
    // down, so rows 2 and 3 ran on a thread whose Dispatcher was dead.
    [StaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryRowOfAStaTheoryGetsALiveDispatcher(int row)
    {
        Assert.False(Dispatcher.CurrentDispatcher.HasShutdownStarted,
            $"row {row} ran on a thread whose Dispatcher had already been shut down");
        _ = new System.Windows.Controls.TextBox();   // a real WPF object, as the failing tests built
    }

    // The deferral relies on the whole theory being ONE delay-enumerated test case on one thread. If
    // theory pre-enumeration is ever turned on, each row becomes its own case and the deferral is
    // skipped (per-row shutdown again) — this test says so rather than leaving it to be rediscovered.
    [StaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void StaTheoriesAreDelayEnumerated(int row)
    {
        _ = row;
        Assert.IsAssignableFrom<XunitDelayEnumeratedTheoryTestCase>(TestContext.Current.Test?.TestCase);
    }

    [Fact]
    public void NoDataAttributesIsOneRow()
        => Assert.Equal(1, ShutDownStaDispatcherAttribute.ExpectedRows([]));

    [Fact]
    public void InlineDataRowsAreCounted()
        => Assert.Equal(3, ShutDownStaDispatcherAttribute.ExpectedRows(
            [new InlineDataAttribute(1), new InlineDataAttribute(2), new InlineDataAttribute(3)]));

    // Rows that may never reach After() would leave the total unreachable and leak the Dispatcher onto
    // a dying thread, so the count must be refused and per-row shutdown kept.
    public static TheoryData<string> RowsThatMayNotRun => new()
    {
        "Skip", "SkipUnless", "SkipWhen", "Explicit",
    };

    [Theory]
    [MemberData(nameof(RowsThatMayNotRun))]
    public void ARowThatMayNotRunMakesTheCountUnknown(string setting)
    {
        var row = new InlineDataAttribute(2);
        switch (setting)
        {
            case "Skip": row.Skip = "not today"; break;
            case "SkipUnless": row.SkipUnless = "SomeProperty"; break;
            case "SkipWhen": row.SkipWhen = "SomeProperty"; break;
            case "Explicit": row.Explicit = true; break;
        }

        Assert.Null(ShutDownStaDispatcherAttribute.ExpectedRows([new InlineDataAttribute(1), row]));
    }

    [Fact]
    public void ADataSourceThatMustBeRunMakesTheCountUnknown()
        => Assert.Null(ShutDownStaDispatcherAttribute.ExpectedRows(
            new List<IDataAttribute> { new InlineDataAttribute(1), new MemberDataAttribute(nameof(RowsThatMayNotRun)) }));
}
