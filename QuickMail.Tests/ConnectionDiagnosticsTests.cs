using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Covers the connection-diagnostics machinery added for the repeated "accounts show disconnected"
/// reports. The point of these is narrow but important: the diagnostics are only worth shipping if
/// the verdict line is actually produced and actually says the right thing, because that one line
/// is what decides which fix we make next.
///
/// <see cref="ConnectionJournal"/> and <see cref="HostConnectionCensus"/> are static (they are called
/// from deep inside MailKit-facing code where threading an instance through would be invasive), so
/// these tests are marked non-parallel against each other and reset state per test.
/// </summary>
[Collection("ConnectionDiagnostics")]
public class ConnectionJournalTests : IDisposable
{
    public ConnectionJournalTests() => ConnectionJournal.ResetForTests();
    public void Dispose() => ConnectionJournal.ResetForTests();

    [Fact]
    public void Record_KeepsEventsInOrder()
    {
        ConnectionJournal.Record(ConnectionEventKind.Connect, "acct", "host", "first");
        ConnectionJournal.Record(ConnectionEventKind.Pool, "acct", "host", "second");

        var events = ConnectionJournal.Snapshot();

        Assert.Equal(2, events.Count);
        Assert.Equal("first", events[0].Phase);
        Assert.Equal("second", events[1].Phase);
    }

    [Fact]
    public void Record_RingIsBounded()
    {
        for (var i = 0; i < ConnectionJournal.Capacity + 50; i++)
            ConnectionJournal.Record(ConnectionEventKind.Pool, "acct", "host", $"e{i}");

        var events = ConnectionJournal.Snapshot();

        Assert.Equal(ConnectionJournal.Capacity, events.Count);
        // Oldest trimmed, newest kept.
        Assert.Equal($"e{ConnectionJournal.Capacity + 49}", events[^1].Phase);
        Assert.DoesNotContain(events, e => e.Phase == "e0");
    }

    [Fact]
    public void Record_BlankAccountAndHostBecomeDashes()
    {
        ConnectionJournal.Record(ConnectionEventKind.Status, "", "  ", "phase");

        var evt = Assert.Single(ConnectionJournal.Snapshot());
        Assert.Equal("-", evt.Account);
        Assert.Equal("-", evt.Host);
    }

    [Fact]
    public void ToString_OmitsPlaceholderFieldsButKeepsRealOnes()
    {
        ConnectionJournal.Record(ConnectionEventKind.Idle, "-", "-", "watchers-reconciled", "starting=1");
        ConnectionJournal.Record(ConnectionEventKind.Connect, "Kelly", "mail.example.com", "connect-ok");

        var lines = ConnectionJournal.Snapshot().Select(e => e.ToString()).ToList();

        Assert.DoesNotContain("account=-", lines[0]);
        Assert.Contains("starting=1", lines[0]);
        Assert.Contains("account=Kelly", lines[1]);
        Assert.Contains("host=mail.example.com", lines[1]);
    }

    [Fact]
    public void RecordError_IncludesTheWholeInnerChain()
    {
        var ex = new InvalidOperationException("outer", new TimeoutException("inner"));

        ConnectionJournal.RecordError(ConnectionEventKind.Connect, "Kelly", "h", "connect-failed", ex, "elapsed=12ms");

        var evt = Assert.Single(ConnectionJournal.Snapshot());
        Assert.Contains("elapsed=12ms", evt.Detail);
        Assert.Contains("outer", evt.Detail);
        Assert.Contains("inner", evt.Detail);   // the inner cause is the part that usually matters
    }

    [Fact]
    public void EventRecorded_FiresForSubscribers()
    {
        ConnectionEvent? seen = null;
        void Handler(ConnectionEvent e) => seen = e;

        ConnectionJournal.EventRecorded += Handler;
        try { ConnectionJournal.Record(ConnectionEventKind.Probe, "a", "h", "verdict"); }
        finally { ConnectionJournal.EventRecorded -= Handler; }

        Assert.NotNull(seen);
        Assert.Equal("verdict", seen!.Phase);
    }

    [Fact]
    public void EventRecorded_ThrowingSubscriberDoesNotBreakLogging()
    {
        void Bad(ConnectionEvent _) => throw new InvalidOperationException("boom");

        ConnectionJournal.EventRecorded += Bad;
        try { ConnectionJournal.Record(ConnectionEventKind.Pool, "a", "h", "still-recorded"); }
        finally { ConnectionJournal.EventRecorded -= Bad; }

        Assert.Single(ConnectionJournal.Snapshot());
    }

    [Fact]
    public void BuildReport_SurfacesVerdictsAndSaysSoWhenThereAreNone()
    {
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "reuse-hot");

        var report = ConnectionJournal.BuildReport();

        Assert.Contains("Probe verdicts", report);
        Assert.Contains("(none recorded", report);
    }

    [Fact]
    public void BuildReport_ListsVerdictsAheadOfTheRawJournal()
    {
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "reuse-hot");
        ConnectionJournal.Record(
            ConnectionEventKind.Probe, "Kelly", "-", "verdict", "label=DISCONNECTED actual=REACHABLE");

        var report = ConnectionJournal.BuildReport(new[] { "Kelly — shown as DISCONNECTED" });

        Assert.Contains("Kelly — shown as DISCONNECTED", report);
        Assert.True(
            report.IndexOf("label=DISCONNECTED actual=REACHABLE", StringComparison.Ordinal)
            < report.IndexOf("Full event journal", StringComparison.Ordinal),
            "verdicts must appear before the raw journal so the answer is not buried");
    }
}

[Collection("ConnectionDiagnostics")]
public class HostConnectionCensusTests : IDisposable
{
    public HostConnectionCensusTests() => HostConnectionCensus.ResetForTests();
    public void Dispose() => HostConnectionCensus.ResetForTests();

    [Fact]
    public void LiveCount_TracksOpenAndClose()
    {
        var account = Guid.NewGuid();

        HostConnectionCensus.Opened("mail.example.com", account);
        HostConnectionCensus.Opened("mail.example.com", account);
        Assert.Equal(2, HostConnectionCensus.LiveCount("mail.example.com"));

        HostConnectionCensus.Closed("mail.example.com", account);
        Assert.Equal(1, HostConnectionCensus.LiveCount("mail.example.com"));
    }

    [Fact]
    public void LiveCount_SumsAcrossAccountsOnTheSameHost()
    {
        HostConnectionCensus.Opened("shared.example.com", Guid.NewGuid());
        HostConnectionCensus.Opened("shared.example.com", Guid.NewGuid());

        Assert.Equal(2, HostConnectionCensus.LiveCount("shared.example.com"));
    }

    [Fact]
    public void Closed_NeverGoesNegative()
    {
        var account = Guid.NewGuid();
        HostConnectionCensus.Closed("mail.example.com", account);
        HostConnectionCensus.Closed("mail.example.com", account);

        Assert.Equal(0, HostConnectionCensus.LiveCount("mail.example.com"));
    }

    [Fact]
    public void Released_IsIdempotent()
    {
        // Clients are disposed from several paths and a double-release would silently corrupt the
        // count for the rest of the session — which would then be read as evidence in the journal.
        var client  = new object();
        var account = Guid.NewGuid();

        HostConnectionCensus.Opened("mail.example.com", account, client);
        Assert.Equal(1, HostConnectionCensus.LiveCount("mail.example.com"));

        HostConnectionCensus.Released(client);
        HostConnectionCensus.Released(client);
        HostConnectionCensus.Released(client);

        Assert.Equal(0, HostConnectionCensus.LiveCount("mail.example.com"));
    }

    [Fact]
    public void Released_IgnoresUnregisteredAndNullClients()
    {
        HostConnectionCensus.Released(null);
        HostConnectionCensus.Released(new object());   // e.g. a connect that failed before the socket opened

        Assert.Equal(0, HostConnectionCensus.LiveCount("mail.example.com"));
    }

    [Fact]
    public void Describe_ReportsCountAndIsAlwaysSafeForUnresolvableHosts()
    {
        HostConnectionCensus.Opened("nonexistent.invalid", Guid.NewGuid());

        var described = HostConnectionCensus.Describe("nonexistent.invalid");

        Assert.Contains("hostSockets=1", described);
        // ".invalid" is reserved and never resolves; the census must degrade rather than throw.
        Assert.Contains("ip=", described);
    }

    [Fact]
    public void SnapshotLines_ListsEveryTrackedHost()
    {
        HostConnectionCensus.Opened("a.invalid", Guid.NewGuid());
        HostConnectionCensus.Opened("b.invalid", Guid.NewGuid());

        var lines = HostConnectionCensus.SnapshotLines();

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("a.invalid", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("b.invalid", StringComparison.Ordinal));
    }
}

/// <summary>
/// A stand-in for the IMAP backend, so the probe's scheduling and verdict logic can be tested
/// without a network or real credentials.
/// </summary>
internal sealed class StubConnectionProbe : IConnectionProbe
{
    private readonly Func<Guid, ProbeResult> _result;
    private int _calls;

    public StubConnectionProbe(Func<Guid, ProbeResult> result) => _result = result;

    public int Calls => Volatile.Read(ref _calls);

    public Task<ProbeResult> ProbeAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_result(accountId));
    }
}

[Collection("ConnectionDiagnostics")]
public class ConnectionTruthProbeTests : IDisposable
{
    private readonly Guid _account = Guid.NewGuid();

    public ConnectionTruthProbeTests() => ConnectionJournal.ResetForTests();
    public void Dispose() => ConnectionJournal.ResetForTests();

    private ConnectionTruthProbe Create(bool reachable, string detail = "ok") =>
        new(new StubConnectionProbe(_ => new ProbeResult(
            reachable ? ProbeOutcome.Reachable : ProbeOutcome.Unreachable, 5, detail)), _ => "Kelly");

    [Fact]
    public async Task Verdict_FlagsAMismatchWhenTheLabelIsWrong()
    {
        // The whole build exists to produce this line.
        using var probe = Create(reachable: true);
        probe.NoteDisconnected(_account, "reachability-event");

        await probe.RunProbeAsync(_account, "test");

        var verdict = Assert.Single(ConnectionJournal.Snapshot(), e => e.Phase == "verdict");
        Assert.Contains("label=DISCONNECTED", verdict.Detail);
        Assert.Contains("actual=REACHABLE", verdict.Detail);
        Assert.Contains("DISPLAYED STATUS IS WRONG", verdict.Detail);
    }

    [Fact]
    public async Task Verdict_DoesNotFlagAMismatchWhenTheAccountIsGenuinelyDown()
    {
        using var probe = Create(reachable: false, detail: "SocketException: refused");
        probe.NoteDisconnected(_account, "reachability-event");

        await probe.RunProbeAsync(_account, "test");

        var verdict = Assert.Single(ConnectionJournal.Snapshot(), e => e.Phase == "verdict");
        Assert.Contains("label=DISCONNECTED", verdict.Detail);
        Assert.Contains("actual=UNREACHABLE", verdict.Detail);
        Assert.DoesNotContain("DISPLAYED STATUS IS WRONG", verdict.Detail);
        Assert.Contains("refused", verdict.Detail);
    }

    [Fact]
    public async Task Verdict_ReportsTheLabelAsConnectedWhenItIs()
    {
        using var probe = Create(reachable: true);

        await probe.RunProbeAsync(_account, "manual test");

        var verdict = Assert.Single(ConnectionJournal.Snapshot(), e => e.Phase == "verdict");
        Assert.Contains("label=CONNECTED", verdict.Detail);
    }

    [Fact]
    public async Task RunProbeAsync_RecordsTheTriggerSoProbeTrafficIsIdentifiable()
    {
        using var probe = Create(reachable: true);

        await probe.RunProbeAsync(_account, "manual test");

        Assert.Contains(
            ConnectionJournal.Snapshot(),
            e => e.Phase == "probe-begin" && e.Detail.Contains("trigger=manual test", StringComparison.Ordinal));
    }

    [Fact]
    public void NoteDisconnected_IsTrackedAndClearedByNoteConnected()
    {
        using var probe = Create(reachable: true);

        probe.NoteDisconnected(_account, "folder-load-failed");
        Assert.True(probe.IsMarkedDisconnected(_account));

        probe.NoteConnected(_account, "initial-connect");
        Assert.False(probe.IsMarkedDisconnected(_account));
    }

    [Fact]
    public void NoteDisconnected_DoesNotStackVerificationLoops()
    {
        using var probe = Create(reachable: true);

        probe.NoteDisconnected(_account, "first");
        probe.NoteDisconnected(_account, "second");
        probe.NoteDisconnected(_account, "third");

        var repeats = ConnectionJournal.Snapshot()
            .Count(e => e.Phase == "marked-disconnected-again");
        Assert.Equal(2, repeats);
        Assert.Single(ConnectionJournal.Snapshot(), e => e.Phase == "marked-disconnected");
    }

    [Fact]
    public void NoteConnected_OnAnAccountThatWasNeverDisconnectedIsANoOp()
    {
        using var probe = Create(reachable: true);

        probe.NoteConnected(_account, "initial-connect");

        Assert.DoesNotContain(ConnectionJournal.Snapshot(), e => e.Phase == "marked-connected");
    }

    [Fact]
    public async Task LastResult_IsRetainedForTheDiagnosticsWindow()
    {
        using var probe = Create(reachable: false, detail: "timed out");

        Assert.Null(probe.LastResultFor(_account));
        await probe.RunProbeAsync(_account, "test");

        var last = probe.LastResultFor(_account);
        Assert.NotNull(last);
        Assert.False(last!.Reachable);
        Assert.Contains("timed out", last.Detail);
        Assert.NotNull(probe.LastResultAtFor(_account));
    }

    [Fact]
    public async Task RunProbeAsync_SerialisesProbes()
    {
        // A per-IP connection limit is an active suspect, so the probe must never fan out and
        // become part of the problem it is measuring.
        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();

        var backend = new StubConnectionProbe(_ =>
        {
            lock (gate)
            {
                concurrent++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }
            Thread.Sleep(20);
            lock (gate) { concurrent--; }
            return new ProbeResult(ProbeOutcome.Reachable, 20, "ok");
        });

        using var probe = new ConnectionTruthProbe(backend, _ => "Kelly");

        await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => probe.RunProbeAsync(Guid.NewGuid(), $"parallel {i}")));

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(6, backend.Calls);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightProbesRatherThanFaultingThem()
    {
        var probe = Create(reachable: true);
        probe.NoteDisconnected(_account, "test");
        probe.Dispose();

        // A probe requested after disposal must surface cancellation, not ObjectDisposedException
        // (see the IDisposable rules in CLAUDE.md — cancel before dispose).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.RunProbeAsync(_account, "after dispose"));
    }

    [Fact]
    public async Task Verdict_NeverCallsTheStatusWrongWhenTheProbeCouldNotTest()
    {
        // Regression guard for the first live false alarm: an IMAP-only probe was asked about a
        // Microsoft Graph account, said "not registered with the IMAP service", and the window
        // reported a healthy account as being in the wrong state. Inconclusive must stay inconclusive.
        using var probe = new ConnectionTruthProbe(
            new StubConnectionProbe(_ => new ProbeResult(
                ProbeOutcome.NotSupported, 0, "not an IMAP account")),
            _ => "ICanBrew");

        probe.NoteDisconnected(_account, "reachability-event");
        await probe.RunProbeAsync(_account, "test");

        var verdict = Assert.Single(ConnectionJournal.Snapshot(), e => e.Phase == "verdict");
        Assert.Contains("actual=NOT-TESTABLE", verdict.Detail);
        Assert.DoesNotContain("DISPLAYED STATUS IS WRONG", verdict.Detail);
        Assert.DoesNotContain("UNREACHABLE", verdict.Detail);
    }

    [Fact]
    public void ProbeResult_NotSupported_IsNeitherReachableNorUnreachable()
    {
        var result = new ProbeResult(ProbeOutcome.NotSupported, 0, "no backend");

        Assert.False(result.Reachable);
        Assert.False(result.Unreachable);   // the bit that made NotSupported read as a failure
        Assert.True(result.Inconclusive);
    }

    [Fact]
    public void SummaryLines_StateBothWhatIsShownAndWhatWasFound()
    {
        using var probe = Create(reachable: true);

        var lines = probe.SummaryLines(new[] { (_account, "Kelly", false) });

        var line = Assert.Single(lines);
        Assert.Contains("Kelly", line);
        Assert.Contains("DISCONNECTED", line);
        Assert.Contains("never probed", line);
    }
}

/// <summary>
/// The static journal and census make these classes order-dependent against each other; xUnit runs
/// a single collection serially.
/// </summary>
[CollectionDefinition("ConnectionDiagnostics", DisableParallelization = true)]
public class ConnectionDiagnosticsCollection { }
