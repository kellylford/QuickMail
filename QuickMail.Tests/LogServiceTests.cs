using System;
using System.IO;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for LogService.Enabled gating and DeleteLog().
/// Each test runs in its own temp directory and restores static state on teardown.
/// </summary>
public sealed class LogServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly bool   _savedEnabled;
    private readonly bool   _savedDebugMode;
    private readonly string _savedFormat;

    public LogServiceTests()
    {
        _tempDir      = Path.Combine(Path.GetTempPath(), $"qm-log-test-{Guid.NewGuid():N}");
        _savedEnabled   = LogService.Enabled;
        _savedDebugMode = LogService.DebugMode;
        _savedFormat    = LogService.Format;

        Directory.CreateDirectory(_tempDir);
        LogService.Configure(_tempDir);
        LogService.Enabled   = true;
        LogService.DebugMode = false;
        LogService.Format    = "actionFirst";
    }

    public void Dispose()
    {
        LogService.Enabled   = _savedEnabled;
        LogService.DebugMode = _savedDebugMode;
        LogService.Format    = _savedFormat;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string LogFile => Path.Combine(_tempDir, "quickmail.log");

    [Fact]
    public void Log_WhenEnabled_WritesFile()
    {
        LogService.Enabled = true;
        LogService.Log("hello");
        Assert.True(File.Exists(LogFile));
    }

    [Fact]
    public void Log_WhenDisabled_DoesNotWriteFile()
    {
        LogService.Enabled = false;
        LogService.Log("hello");
        Assert.False(File.Exists(LogFile));
    }

    [Fact]
    public void Log_WhenDisabledButDebugModeOn_StillWritesFile()
    {
        LogService.Enabled   = false;
        LogService.DebugMode = true;
        LogService.Log("hello");
        Assert.True(File.Exists(LogFile));
    }

    [Fact]
    public void Log_ContainsMessage()
    {
        LogService.Log("sync complete");
        Assert.Contains("sync complete", File.ReadAllText(LogFile));
    }

    [Fact]
    public void DeleteLog_RemovesExistingFile()
    {
        LogService.Log("seed");
        Assert.True(File.Exists(LogFile));

        LogService.DeleteLog();

        Assert.False(File.Exists(LogFile));
    }

    [Fact]
    public void DeleteLog_WhenFileAbsent_DoesNotThrow()
    {
        Assert.False(File.Exists(LogFile));
        var ex = Record.Exception(LogService.DeleteLog);
        Assert.Null(ex);
    }

    [Fact]
    public void Log_AfterDelete_RecreatesFile()
    {
        LogService.Log("first");
        LogService.DeleteLog();

        LogService.Log("second");

        Assert.True(File.Exists(LogFile));
        Assert.Contains("second", File.ReadAllText(LogFile));
    }
}
