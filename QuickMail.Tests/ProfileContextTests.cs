using System;
using System.IO;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class ProfileContextTests
{
    [Fact]
    public void Default_ReturnsPathUnderAppData()
    {
        var profile  = ProfileContext.Default();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickMail");
        Assert.Equal(expected, profile.ProfileDir);
    }

    [Fact]
    public void Constructor_CreatesDirectoryIfMissing()
    {
        var dir = TempPath();
        Assert.False(Directory.Exists(dir));
        try
        {
            _ = new ProfileContext(dir);
            Assert.True(Directory.Exists(dir));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Constructor_SucceedsIfDirectoryAlreadyExists()
    {
        var dir = TempPath();
        Directory.CreateDirectory(dir);
        try
        {
            var profile = new ProfileContext(dir);
            Assert.Equal(dir, profile.ProfileDir);
        }
        finally { TryDelete(dir); }
    }

    // ── ParseProfileDir ───────────────────────────────────────────────────────

    [Fact]
    public void ParseProfileDir_ReturnsValue_WhenFlagPresent()
    {
        var result = ProfileContext.ParseProfileDir(["--profileDir", @"C:\foo"]);
        Assert.Equal(@"C:\foo", result);
    }

    [Fact]
    public void ParseProfileDir_IsCaseInsensitive()
    {
        var result = ProfileContext.ParseProfileDir(["--PROFILEDIR", @"C:\foo"]);
        Assert.Equal(@"C:\foo", result);
    }

    [Fact]
    public void ParseProfileDir_ReturnsNull_WhenFlagAbsent()
    {
        var result = ProfileContext.ParseProfileDir(["/debug"]);
        Assert.Null(result);
    }

    [Fact]
    public void ParseProfileDir_ReturnsNull_WhenFlagIsLastArg()
    {
        // --profileDir with no following value must not crash or return garbage
        var result = ProfileContext.ParseProfileDir(["--profileDir"]);
        Assert.Null(result);
    }

    [Fact]
    public void ParseProfileDir_ReturnsNull_OnEmptyArgs()
    {
        var result = ProfileContext.ParseProfileDir([]);
        Assert.Null(result);
    }

    // ── TryCreate ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryCreate_CreatesDirectory_AndReturnsProfile()
    {
        var dir = TempPath();
        try
        {
            var profile = ProfileContext.TryCreate(dir, out var error);
            Assert.NotNull(profile);
            Assert.Null(error);
            Assert.Equal(Path.GetFullPath(dir), profile.ProfileDir);
            Assert.True(Directory.Exists(dir));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void TryCreate_SucceedsIfDirectoryAlreadyExists()
    {
        var dir = TempPath();
        Directory.CreateDirectory(dir);
        try
        {
            var profile = ProfileContext.TryCreate(dir, out var error);
            Assert.NotNull(profile);
            Assert.Null(error);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void TryCreate_ReturnsError_WhenPathExistsAsFile()
    {
        var file = TempPath() + ".tmp";
        File.WriteAllText(file, string.Empty);
        try
        {
            var profile = ProfileContext.TryCreate(file, out var error);
            Assert.Null(profile);
            Assert.NotNull(error);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void TryCreate_ReturnsError_WhenPathContainsInvalidChars()
    {
        var profile = ProfileContext.TryCreate("Z:\\path|with<invalid>chars", out var error);
        Assert.Null(profile);
        Assert.NotNull(error);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"QM-Profile-{Guid.NewGuid():N}");

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
