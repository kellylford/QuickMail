using System.Collections.Generic;
using System.Windows.Threading;
using Xunit;
using Xunit.v3;

namespace QuickMail.Tests;

/// <summary>
/// Guards <see cref="ShutDownStaDispatcherAttribute"/>. Rows of a delay-enumerated StaTheory share one
/// STA thread, so its Dispatcher must survive until the last row; everything else, and any theory
/// whose row count cannot be trusted, must still be shut down after each row (issue #211).
/// </summary>
public class StaDispatcherShutdownTests
{
    // One test case for the whole theory, so all three rows run on one rented thread. Without the
    // deferral the first row's After() shut that thread's Dispatcher down and rows 2 and 3 failed.
    [StaTheory(DisableDiscoveryEnumeration = true)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryRowOfADelayEnumeratedStaTheoryGetsALiveDispatcher(int row)
    {
        // Precondition: otherwise this would pass without exercising the deferral at all.
        Assert.IsAssignableFrom<XunitDelayEnumeratedTheoryTestCase>(TestContext.Current.Test?.TestCase);

        Assert.False(Dispatcher.CurrentDispatcher.HasShutdownStarted,
            $"row {row} ran on a thread whose Dispatcher had already been shut down");
        _ = new System.Windows.Controls.TextBox();   // a real WPF object
    }

    // The default: serializable [InlineData] rows are pre-enumerated, each row its own test case on its
    // own thread, so the deferral must NOT apply (it would never reach its count and would leak).
    [StaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void APreEnumeratedStaTheoryRowIsItsOwnTestCase(int row)
    {
        Assert.IsNotAssignableFrom<XunitDelayEnumeratedTheoryTestCase>(TestContext.Current.Test?.TestCase);

        Assert.False(Dispatcher.CurrentDispatcher.HasShutdownStarted,
            $"row {row} ran on a thread whose Dispatcher had already been shut down");
        _ = new System.Windows.Controls.TextBox();
    }

    // A foreground STA thread still alive when the run ends makes xunit force-exit and fail a run in
    // which every test passed ("Foreground threads were left running"). Before() marks it background.
    [StaFact]
    public void AnStaTestRunsOnABackgroundThread()
    {
        Assert.Equal(System.Threading.ApartmentState.STA,
            System.Threading.Thread.CurrentThread.GetApartmentState());
        Assert.True(System.Threading.Thread.CurrentThread.IsBackground);
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
