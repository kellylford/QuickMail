using System.IO;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class SingleInstanceServiceTests
{
    // Each test that touches real kernel objects uses a unique profile path so tests never
    // collide with each other or with a QuickMail instance running on the machine.
    private static string[] UniqueProfileArgs() =>
        ProfileArgs(Path.Combine(Path.GetTempPath(), "qm-si-test-" + Guid.NewGuid().ToString("N")));

    private static string[] ProfileArgs(string dir) => new[] { "--profileDir", dir };

    [Fact]
    public void ProfileKey_IsStableAcrossCaseAndTrailingSeparator()
    {
        var baseKey = SingleInstanceService.ProfileKey(ProfileArgs(@"C:\Data\QuickMailProfile"));

        Assert.Equal(baseKey, SingleInstanceService.ProfileKey(ProfileArgs(@"c:\data\quickmailprofile")));
        Assert.Equal(baseKey, SingleInstanceService.ProfileKey(ProfileArgs(@"C:\Data\QuickMailProfile\")));
        Assert.Equal(baseKey, SingleInstanceService.ProfileKey(ProfileArgs(@"C:\Data\Other\..\QuickMailProfile")));
    }

    [Fact]
    public void ProfileKey_DiffersForDifferentDirectories()
    {
        var a = SingleInstanceService.ProfileKey(ProfileArgs(@"C:\Data\ProfileA"));
        var b = SingleInstanceService.ProfileKey(ProfileArgs(@"C:\Data\ProfileB"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ProfileKey_NoArgs_MatchesExplicitDefaultProfileDir()
    {
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");

        Assert.Equal(
            SingleInstanceService.ProfileKey(Array.Empty<string>()),
            SingleInstanceService.ProfileKey(ProfileArgs(defaultDir)));
    }

    [Fact]
    public void ProfileKey_IsFixedLengthHex()
    {
        var key = SingleInstanceService.ProfileKey(ProfileArgs(@"C:\Data\QuickMailProfile"));
        Assert.Equal(32, key.Length);
        Assert.All(key, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void SecondAcquire_ForSameProfile_FailsUntilFirstIsDisposed()
    {
        var args = UniqueProfileArgs();

        var first = SingleInstanceService.TryAcquire(args);
        Assert.NotNull(first);
        try
        {
            Assert.Null(SingleInstanceService.TryAcquire(args));
        }
        finally
        {
            first!.Dispose();
        }

        var second = SingleInstanceService.TryAcquire(args);
        Assert.NotNull(second);
        second!.Dispose();
    }

    [Fact]
    public void Acquire_ForDifferentProfiles_BothSucceed()
    {
        var first = SingleInstanceService.TryAcquire(UniqueProfileArgs());
        var second = SingleInstanceService.TryAcquire(UniqueProfileArgs());
        try
        {
            Assert.NotNull(first);
            Assert.NotNull(second);
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
        }
    }

    [Fact]
    public void SecondLaunch_SignalsFirstInstanceToActivate()
    {
        var args = UniqueProfileArgs();

        using var first = SingleInstanceService.TryAcquire(args)!;
        Assert.NotNull(first);

        using var activated = new ManualResetEventSlim(false);
        first.ListenForActivation(activated.Set);

        Assert.Null(SingleInstanceService.TryAcquire(args));
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "the running instance was not signaled by the second launch");
    }
}
