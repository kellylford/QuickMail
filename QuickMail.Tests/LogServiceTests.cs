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
        // Restore logging to the shared test-redirect dir BEFORE deleting ours. Otherwise LogService
        // is left pointing at _tempDir; the next test to log recreates it (via Directory.CreateDirectory
        // in LogService.Log), leaking a qm-log-test-* folder every run — hundreds piled up in %TEMP%.
        LogService.Configure(TestLogRedirect.Dir);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string LogFile => Path.Combine(_tempDir, "quickmail.log");

    /// <summary>
    /// A marker unique to this test instance. LogService's target directory is static, so while
    /// these tests point it at their own temp dir, any other test running in parallel that happens
    /// to log (44 production files call LogService) writes into that same file. Asserting on bare
    /// file existence therefore fails intermittently — roughly 2 runs in 7 — for reasons that have
    /// nothing to do with the behaviour under test. Asserting on this marker instead tests the
    /// actual contract ("did *our* message get written?") and is immune to the interference. (#377)
    /// </summary>
    private readonly string _marker = $"marker-{Guid.NewGuid():N}";

    /// <summary>
    /// Reads the log without locking out LogService's own writer. File.ReadAllText opens with
    /// FileShare.Read, so a concurrent append from another test's production code failed — and
    /// LogService swallows write errors, so the line vanished and this returned false. That was one
    /// of the suite's intermittent failures.
    /// </summary>
    private bool LogContainsMarker()
    {
        if (!File.Exists(LogFile)) return false;

        using var stream = new FileStream(
            LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Contains(_marker, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_WhenEnabled_WritesFile()
    {
        LogService.Enabled = true;
        LogService.Log(_marker);
        Assert.True(LogContainsMarker());
    }

    [Fact]
    public void Log_WhenDisabled_DoesNotWriteFile()
    {
        LogService.Enabled = false;
        LogService.Log(_marker);
        Assert.False(LogContainsMarker());
    }

    [Fact]
    public void Log_WhenDisabledButDebugModeOn_StillWritesFile()
    {
        LogService.Enabled   = false;
        LogService.DebugMode = true;
        LogService.Log(_marker);
        Assert.True(LogContainsMarker());
    }

    [Fact]
    public void Log_ContainsMessage()
    {
        LogService.Log($"sync complete {_marker}");
        Assert.Contains($"sync complete {_marker}", File.ReadAllText(LogFile), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteLog_RemovesExistingFile()
    {
        LogService.Log(_marker);
        Assert.True(LogContainsMarker());

        LogService.DeleteLog();

        // A parallel test may recreate the file immediately; what must be gone is our content.
        Assert.False(LogContainsMarker());
    }

    [Fact]
    public void DeleteLog_WhenFileAbsent_DoesNotThrow()
    {
        // Delete first so the second call is genuinely the file-absent case. The old version
        // asserted the file did not exist up front, which a parallel test's write could falsify.
        LogService.DeleteLog();

        var ex = Record.Exception(LogService.DeleteLog);
        Assert.Null(ex);
    }

    [Fact]
    public void Log_AfterDelete_RecreatesFile()
    {
        LogService.Log($"first {_marker}");
        LogService.DeleteLog();

        LogService.Log($"second {_marker}");

        Assert.True(File.Exists(LogFile));
        Assert.Contains($"second {_marker}", File.ReadAllText(LogFile), StringComparison.Ordinal);
    }
}
