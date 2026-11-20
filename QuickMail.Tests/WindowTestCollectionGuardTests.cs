using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Which collections <see cref="WindowTestCollectionGuardAttribute"/> lets show a window. The
/// detection itself runs on every test in the suite; these pin the decision it makes.
/// </summary>
public class WindowTestCollectionGuardTests
{
    [Fact]
    public void TheWpfTestsCollection_MayShowWindows() =>
        Assert.True(WindowTestCollectionGuardAttribute.RunsAloneOrInWpfTests(typeof(WpfTestsCollection)));

    [Fact]
    public void ACollectionThatRunsAlone_MayShowWindows() =>
        Assert.True(WindowTestCollectionGuardAttribute.RunsAloneOrInWpfTests(typeof(ConnectionDiagnosticsCollection)));

    [Fact]
    public void AClassWithNoCollection_RunsInParallel_AndMayNot() =>
        Assert.False(WindowTestCollectionGuardAttribute.RunsAloneOrInWpfTests(null));

    [Fact]
    public void ANamedCollectionThatStillRunsInParallel_MayNot() =>
        Assert.False(WindowTestCollectionGuardAttribute.RunsAloneOrInWpfTests(typeof(WindowGuardParallelCollection)));
}

/// <summary>
/// That the guard's detection sees what it must: a window activated on the test's thread, even when
/// the test never pumps the dispatcher (Show, assert, Close, as SoundTutorialTests does), and not a
/// window that was only constructed or shown without activation. These show windows, so they are in
/// WpfTests themselves.
/// </summary>
[Collection("WpfTests")]
public class WindowTestCollectionGuardDetectionTests
{
    [StaFact]
    public void AWindowShownAndClosedWithoutPumping_IsDetected()
    {
        using var watch = WindowTestCollectionGuardAttribute.ActivationWatch.Start();
        var window = new System.Windows.Window { Width = 100, Height = 100 };
        window.Show();
        window.UpdateLayout();
        window.Close();
        Assert.True(watch.Activated);
    }

    [StaFact]
    public void AWindowThatIsOnlyConstructed_IsNotDetected()
    {
        using var watch = WindowTestCollectionGuardAttribute.ActivationWatch.Start();
        var window = new System.Windows.Window();
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        window.Close();
        Assert.False(watch.Activated);
    }

    [StaFact]
    public void AWindowShownWithoutActivation_IsNotDetected()
    {
        using var watch = WindowTestCollectionGuardAttribute.ActivationWatch.Start();
        var window = new System.Windows.Window { Width = 100, Height = 100, ShowActivated = false };
        window.Show();
        window.Close();
        Assert.False(watch.Activated);
    }
}

/// <summary>A named collection that still runs in parallel with the others; no tests use it.</summary>
// Unused on purpose: it exists only as the definition type handed to the guard above, and putting a
// test class in it would add a collection to the run for no reason.
#pragma warning disable xUnit1073
[CollectionDefinition("WindowGuardParallel")]
public sealed class WindowGuardParallelCollection { }
#pragma warning restore xUnit1073
