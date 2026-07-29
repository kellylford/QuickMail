using System;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Connection diagnostics are opt-in: off by default, switched on under Settings → Advanced when
/// someone is investigating a problem.
///
/// The guarantees worth testing are that "off" really means off — nothing written, nothing recorded,
/// no command offered — and that turning it on takes effect immediately rather than at next startup,
/// because the person enabling it is trying to capture a problem that is happening right now.
/// </summary>
[Collection("ConnectionDiagnostics")]
public class ConnectionDiagnosticsSettingTests : IDisposable
{
    public ConnectionDiagnosticsSettingTests() => ConnectionJournal.ResetForTests();

    public void Dispose()
    {
        ConnectionJournal.ResetForTests();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DefaultsToOff()
    {
        Assert.False(new ConfigModel().ConnectionDiagnostics);
    }

    [Fact]
    public void WhenDisabled_NothingIsRecorded()
    {
        ConnectionJournal.Enabled = false;

        ConnectionJournal.Record(ConnectionEventKind.Connect, "Kelly", "h", "connect-failed", "boom");
        ConnectionJournal.RecordError(
            ConnectionEventKind.Idle, "Kelly", "h", "watcher-error", new InvalidOperationException("x"));

        // The only entry present is the "diagnostics-disabled" marker written as recording stopped;
        // nothing attempted afterwards is captured.
        var phases = ConnectionJournal.Snapshot().Select(e => e.Phase).ToList();
        Assert.DoesNotContain("connect-failed", phases);
        Assert.DoesNotContain("watcher-error", phases);
        Assert.Equal(new[] { "diagnostics-disabled" }, phases);
    }

    [Fact]
    public void WhenDisabled_SubscribersAreNotNotified()
    {
        // The diagnostics window subscribes to this; with recording off it must stay silent.
        ConnectionJournal.Enabled = false;

        var fired = 0;
        void Handler(ConnectionEvent _) => fired++;

        ConnectionJournal.EventRecorded += Handler;
        try { ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "reuse-hot"); }
        finally { ConnectionJournal.EventRecorded -= Handler; }

        Assert.Equal(0, fired);
    }

    [Fact]
    public void EnablingRecordsAMarkerAndStartsCapturingImmediately()
    {
        ConnectionJournal.Enabled = false;
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "before");

        ConnectionJournal.Enabled = true;
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "after");

        var phases = ConnectionJournal.Snapshot().Select(e => e.Phase).ToList();
        Assert.DoesNotContain("before", phases);
        Assert.Contains("diagnostics-enabled", phases);
        Assert.Contains("after", phases);
    }

    [Fact]
    public void DisablingLeavesAMarkerSoASilentJournalIsExplained()
    {
        // A journal that just stops mid-investigation is ambiguous — did the app die, or did
        // someone turn recording off? Say which.
        ConnectionJournal.Enabled = true;
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "during");

        ConnectionJournal.Enabled = false;

        var phases = ConnectionJournal.Snapshot().Select(e => e.Phase).ToList();
        Assert.Contains("diagnostics-disabled", phases);
        Assert.False(ConnectionJournal.Enabled);
    }

    [Fact]
    public void TogglingToTheSameValueDoesNotRepeatTheMarker()
    {
        ConnectionJournal.Enabled = true;   // ResetForTests already enabled it; this is a no-op
        ConnectionJournal.Enabled = true;

        Assert.DoesNotContain(ConnectionJournal.Snapshot(), e => e.Phase == "diagnostics-enabled");
    }

    [Fact]
    public void RoundTripsThroughConfigIni()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "qm-conn-diag-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var service = new ConfigService(new ProfileContext(dir));

            var cfg = service.Load();
            Assert.False(cfg.ConnectionDiagnostics);   // default in a fresh profile

            cfg.ConnectionDiagnostics = true;
            service.Save(cfg);

            Assert.True(new ConfigService(new ProfileContext(dir)).Load().ConnectionDiagnostics);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeleteLog_ClearsTheInMemoryRing()
    {
        ConnectionJournal.Enabled = true;
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "something");
        Assert.NotEmpty(ConnectionJournal.Snapshot());

        ConnectionJournal.DeleteLog();

        Assert.Empty(ConnectionJournal.Snapshot());
    }
}
