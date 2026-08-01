using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The "a native ARM build is available" notice (issue #18): an x64 copy running under
/// emulation on an ARM64 device says so once per version and offers a Help menu entry.
///
/// The detection itself cannot be unit-tested on an x64 CI runner — it reads the real
/// machine's architecture — so these cover the parts that are testable anywhere: the
/// once-per-version bookkeeping that decides whether the notice repeats, and the fact that
/// detection is correctly false on non-ARM hardware.
/// </summary>
public class NativeArmNoticeTests
{
    private static ProfileContext MakeTempProfile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QM-ArmNotice-{Guid.NewGuid():N}");
        return new ProfileContext(tempDir);
    }

    [Fact]
    public void NativeArmNoticeVersion_RoundTripsThroughConfigIni()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.NativeArmNoticeVersion = "0.8.37";
        service.Save(config);

        Assert.Equal("0.8.37", new ConfigService(profile).Load().NativeArmNoticeVersion);
    }

    [Fact]
    public void NativeArmNoticeVersion_DefaultsToEmpty_SoTheNoticeIsHeardOnce()
    {
        // Empty is what makes the first eligible launch announce; a non-empty default would
        // silence the notice permanently for everyone.
        Assert.Equal(string.Empty, new ConfigService(MakeTempProfile()).Load().NativeArmNoticeVersion);
    }

    [Fact]
    public void NativeArmNoticeVersion_SurvivesAnUnrelatedSave()
    {
        // The notice is stamped by MainViewModel and every other config write must preserve
        // it — losing it would make the announcement repeat at every launch, which is the
        // failure this key exists to prevent.
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.NativeArmNoticeVersion = "0.8.37";
        service.Save(config);

        var second = new ConfigService(profile).Load();
        second.AppearanceThemeId = "ember";
        new ConfigService(profile).Save(second);

        Assert.Equal("0.8.37", new ConfigService(profile).Load().NativeArmNoticeVersion);
    }

    [Fact]
    public void IsEmulatedOnArm64_IsFalse_WhenProcessAndOsArchitecturesAgree()
    {
        // On any machine where the process and the OS are the same architecture — every CI
        // runner, and a native ARM64 build on ARM64 hardware — nothing must be surfaced.
        if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
            return; // genuinely emulated; the assertion below would not apply

        Assert.False(QuickMail.Helpers.ProcessArchitectureInfo.IsEmulatedOnArm64);
    }
}
