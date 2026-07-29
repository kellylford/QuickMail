using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QuickMail.Services;

/// <summary>
/// Watches for accounts being shown as disconnected and independently verifies whether they
/// actually are, recording a verdict in the <see cref="ConnectionJournal"/>.
///
/// The reported symptom is "the accounts show disconnected in the account list" — with the honest
/// caveat that nobody knows whether they are genuinely disconnected. Everything else in this build
/// records what the app <em>did</em>; this records what was <em>true</em>, which is the one thing
/// no amount of re-reading our own logs can establish.
///
/// The two possible verdicts point at different fixes:
/// <list type="bullet">
/// <item><c>label=DISCONNECTED actual=REACHABLE</c> — a state-tracking bug. The account is fine and
/// the UI is stale (issue #314: status has no single owner and is written from eight places).</item>
/// <item><c>label=DISCONNECTED actual=UNREACHABLE</c> — connections really are dropping, and the
/// host, socket-count, and error data in the same journal say why.</item>
/// </list>
///
/// Probing costs a connection, and a per-IP connection limit is an active suspect in this
/// investigation, so the probe is careful not to become part of the problem: one probe runs at a
/// time process-wide, an account is probed at most once per <see cref="MinInterval"/>, and every
/// probe fully disconnects. Each probe is journaled, so probe traffic is never mistaken for
/// application traffic when reading the log.
/// </summary>
public sealed class ConnectionTruthProbe : IDisposable
{
    /// <summary>Default minimum gap between probes of the same account.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Minimum gap between probes of the same account. Injectable so tests can actually observe
    /// loop behaviour: with the shipped 60-second value, a test would have to run for over a
    /// minute to notice a loop that should have stopped but didn't — which is precisely the defect
    /// that reached review twice.
    /// </summary>
    public TimeSpan MinInterval { get; }

    private readonly IConnectionProbe _probe;
    private readonly Func<Guid, string> _labelOf;

    // Only one probe in flight at a time, process-wide.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    // accountId -> the repeating verification loop currently running for it, and the token that
    // stops it. The token matters: without it, NoteConnected only removed dictionary entries while
    // the loop stayed asleep in its 60-second delay. A later NoteDisconnected repopulated those
    // entries, the orphan woke to a satisfied condition, and it kept probing alongside the new
    // loop — one extra permanent loop per status flap, which is exactly the "never be part of the
    // problem it measures" contract this class claims to keep.
    private readonly ConcurrentDictionary<Guid, LoopHandle> _loops = new();
    private readonly ConcurrentDictionary<Guid, bool> _disconnected = new();
    private readonly ConcurrentDictionary<Guid, ProbeResult> _lastResult = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastResultAt = new();

    // Monotonic stamp of the last completed probe, for interval arithmetic. Separate from
    // _lastResultAt (wall-clock, for display): a DST or NTP step backwards would otherwise defer
    // verification for up to an hour, or throw out of Task.Delay on a large enough correction.
    private readonly ConcurrentDictionary<Guid, long> _lastProbeTicks = new();

    private sealed class LoopHandle
    {
        public required Task Task { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        public void StopAndDispose()
        {
            // Cancel before disposing, per the IDisposable rules in CLAUDE.md.
            try { Cts.Cancel(); } catch { }
            try { Cts.Dispose(); } catch { }
        }
    }

    private bool _disposed;

    /// <param name="probe">Performs the actual reachability check.</param>
    /// <param name="labelOf">Resolves an account id to a display label for the journal.</param>
    /// <param name="minInterval">Minimum gap between probes of one account; defaults to 60 seconds.</param>
    public ConnectionTruthProbe(
        IConnectionProbe probe, Func<Guid, string> labelOf, TimeSpan? minInterval = null)
    {
        _probe      = probe;
        _labelOf    = labelOf;
        MinInterval = minInterval ?? DefaultMinInterval;
    }

    /// <summary>The most recent verdict for an account, or null if it has never been probed.</summary>
    public ProbeResult? LastResultFor(Guid accountId) =>
        _lastResult.TryGetValue(accountId, out var r) ? r : null;

    /// <summary>When the most recent verdict for an account was recorded.</summary>
    public DateTime? LastResultAtFor(Guid accountId) =>
        _lastResultAt.TryGetValue(accountId, out var t) ? t : null;

    /// <summary>
    /// Call whenever the UI marks an account as disconnected. Starts a verification loop that keeps
    /// checking every <see cref="MinInterval"/> until <see cref="NoteConnected"/> is called, so we
    /// learn not just whether the label was wrong but how long it stayed wrong.
    /// </summary>
    /// <param name="source">Where the disconnected status came from, recorded in the journal.</param>
    public void NoteDisconnected(Guid accountId, string source)
    {
        if (_disposed) return;

        // Diagnostics off means NOTHING runs. This is not merely about logging: the verification
        // loop below opens a real authenticated connection, outside the pool's cap, every minute
        // until the account recovers. Without this check, a single IDLE failure on a default install
        // starts that loop for the rest of the session — adding connections to a host at exactly the
        // moment it is already refusing them, which is the failure mode this whole feature exists to
        // investigate. The probe must never be part of the problem it measures.
        if (!ConnectionJournal.Enabled) return;

        // Already tracked — but "tracked" is not the same as "still being checked". A loop can have
        // exited while the entry remains: diagnostics were switched off, or it faulted. Restart it
        // rather than assuming, otherwise an account can sit marked-disconnected with nothing
        // verifying it for the rest of the session. Timing-independent by design — relying on the
        // loop to clean up after itself cannot work when it sleeps a minute between rounds.
        if (!_disconnected.TryAdd(accountId, true))
        {
            var stillRunning = _loops.TryGetValue(accountId, out var existing) && !existing.Task.IsCompleted;
            if (stillRunning)
            {
                ConnectionJournal.Record(
                    ConnectionEventKind.Probe, _labelOf(accountId), "-", "marked-disconnected-again",
                    $"source={source} (verification already running)");
                return;
            }

            ConnectionJournal.Record(
                ConnectionEventKind.Probe, _labelOf(accountId), "-", "verification-restarted",
                $"source={source} (previous check had stopped)");
            StartLoop(accountId);
            return;
        }

        ConnectionJournal.Record(
            ConnectionEventKind.Probe, _labelOf(accountId), "-", "marked-disconnected",
            $"source={source} — verifying whether the account is really unreachable");

        StartLoop(accountId);
    }

    /// <summary>
    /// Starts (or replaces) the verification loop for an account, stopping any previous one first
    /// so loops can never accumulate.
    /// </summary>
    private void StartLoop(Guid accountId)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var handle = new LoopHandle
        {
            Cts  = cts,
            Task = Task.Run(() => VerifyUntilConnectedAsync(accountId, cts.Token), cts.Token),
        };

        if (_loops.TryRemove(accountId, out var previous)) previous.StopAndDispose();
        _loops[accountId] = handle;
    }

    /// <summary>Stops and forgets an account's verification loop, if it has one.</summary>
    private void StopLoop(Guid accountId)
    {
        if (_loops.TryRemove(accountId, out var handle)) handle.StopAndDispose();
    }

    /// <summary>
    /// Call whenever the UI marks an account as connected. Stops the verification loop and records
    /// how long the account spent showing as disconnected.
    /// </summary>
    public void NoteConnected(Guid accountId, string source)
    {
        if (_disposed) return;
        // Deliberately NOT gated on ConnectionJournal.Enabled: if diagnostics were switched off
        // while an account was being verified, its loop still has to be told to stop.
        if (!_disconnected.TryRemove(accountId, out _)) return;   // wasn't disconnected

        StopLoop(accountId);
        ConnectionJournal.Record(
            ConnectionEventKind.Probe, _labelOf(accountId), "-", "marked-connected",
            $"source={source} — verification stopped");
    }

    /// <summary>True if the UI currently shows this account as disconnected.</summary>
    public bool IsMarkedDisconnected(Guid accountId) => _disconnected.ContainsKey(accountId);

    /// <summary>
    /// Stops verification for any account not in <paramref name="liveAccountIds"/>.
    ///
    /// An account deleted while it was showing disconnected never gets a NoteConnected call — it is
    /// simply gone from the list — so without this its loop keeps opening connections once a minute
    /// for the rest of the session, for a mailbox the user has removed.
    /// </summary>
    public void RetainOnly(IEnumerable<Guid> liveAccountIds)
    {
        if (_disposed) return;

        var live = new HashSet<Guid>(liveAccountIds);
        foreach (var id in _disconnected.Keys.ToArray())
        {
            if (live.Contains(id)) continue;
            if (!_disconnected.TryRemove(id, out _)) continue;

            StopLoop(id);
            ConnectionJournal.Record(
                ConnectionEventKind.Probe, _labelOf(id), "-", "verification-abandoned",
                "account is no longer in the account list");
        }
    }

    private async Task VerifyUntilConnectedAsync(Guid accountId, CancellationToken ct)
    {
        var round = 0;
        try
        {
            // Honour the documented per-account rate limit across loops, not just within one.
            // Status can flap — the folder-count sweep alone can re-run every few seconds — and each
            // flap starts a fresh loop. Probing immediately every time would mean one extra
            // authenticated connect per flap, against a host that is by hypothesis already refusing
            // them: the same mistake as probing while switched off, one level down. Wait out the
            // remainder rather than declining, so verification is deferred and never skipped.
            if (_lastProbeTicks.TryGetValue(accountId, out var lastTicks))
            {
                var since = TimeSpan.FromTicks(
                    (long)((Stopwatch.GetTimestamp() - lastTicks) * (10_000_000.0 / Stopwatch.Frequency)));
                if (since > TimeSpan.Zero && since < MinInterval)
                {
                    ConnectionJournal.Record(
                        ConnectionEventKind.Probe, _labelOf(accountId), "-", "verification-deferred",
                        $"last checked {since.TotalSeconds:F0}s ago; waiting out the " +
                        $"{MinInterval.TotalSeconds:F0}s minimum before checking again");

                    try { await Task.Delay(MinInterval - since, ct); }
                    catch (OperationCanceledException) { return; }
                }
            }

            while (!ct.IsCancellationRequested && _disconnected.ContainsKey(accountId))
            {
                if (!ConnectionJournal.Enabled)
                {
                    // Switched off mid-verification. Clear the tracking state as well as stopping:
                    // leaving the account in _disconnected made a later NoteDisconnected see a
                    // verification "already running" that had in fact exited, so re-enabling
                    // diagnostics never resumed checking that account for the rest of the session.
                    _disconnected.TryRemove(accountId, out _);
                    return;   // the handle is disposed by whoever starts the next loop
                }

                round++;
                await RunProbeAsync(accountId, $"auto round {round}", ct);

                try { await Task.Delay(MinInterval, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            // The verification loop failing silently would be its own invisible bug.
            ConnectionJournal.RecordError(
                ConnectionEventKind.Probe, _labelOf(accountId), "-", "verify-loop-failed", ex);
        }
    }

    /// <summary>
    /// Runs one probe now, subject to the global one-at-a-time gate. Used by the automatic loop and
    /// by the "Test this account" button in the diagnostics window.
    /// </summary>
    public async Task<ProbeResult> RunProbeAsync(Guid accountId, string trigger, CancellationToken ct = default)
    {
        var label = _labelOf(accountId);

        // Shutdown must look like cancellation to callers, never like a crash. Both the linked-token
        // source and the semaphore throw ObjectDisposedException once Dispose has run, and a probe
        // racing app exit is entirely normal — the diagnostics window can still be open. Translate.
        CancellationTokenSource linked;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        }
        catch (ObjectDisposedException)
        {
            throw new OperationCanceledException("The connection probe has been shut down.");
        }

        using (linked)
        {
            try { await _oneAtATime.WaitAsync(linked.Token); }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException("The connection probe has been shut down.");
            }

            return await RunProbeCoreAsync(accountId, label, trigger, linked.Token);
        }
    }

    private async Task<ProbeResult> RunProbeCoreAsync(
        Guid accountId, string label, string trigger, CancellationToken ct)
    {
        try
        {
            ConnectionJournal.Record(
                ConnectionEventKind.Probe, label, "-", "probe-begin", $"trigger={trigger}");

            var result = await _probe.ProbeAccountAsync(accountId, ct);

            _lastResult[accountId]     = result;
            _lastResultAt[accountId]   = DateTime.Now;              // display
            _lastProbeTicks[accountId] = Stopwatch.GetTimestamp();  // interval arithmetic

            // The one line the whole build exists to produce. Formatted so `grep VERDICT` over
            // connection.log answers the question without reading anything else.
            var labelState = _disconnected.ContainsKey(accountId) ? "DISCONNECTED" : "CONNECTED";
            var actual = result.Outcome switch
            {
                ProbeOutcome.Reachable   => "REACHABLE",
                ProbeOutcome.Unreachable => "UNREACHABLE",
                _                        => "NOT-TESTABLE",
            };

            // Only claim the displayed status is wrong when the probe positively established the
            // opposite. An inconclusive probe must never accuse the UI of being wrong — doing so is
            // what produced the first live false alarm.
            var mismatch = labelState == "DISCONNECTED" && result.Reachable
                ? "  ← DISPLAYED STATUS IS WRONG"
                : string.Empty;

            ConnectionJournal.Record(
                ConnectionEventKind.Probe, label, "-", "verdict",
                $"label={labelState} actual={actual} elapsed={result.ElapsedMs}ms " +
                $"trigger={trigger} detail=[{result.Detail}]{mismatch}");

            return result;
        }
        finally
        {
            // Dispose may have run while the probe was in flight; releasing a disposed semaphore
            // throws, and there is nothing left to release at that point anyway.
            try { _oneAtATime.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Per-account summary lines for the diagnostics report.</summary>
    public IReadOnlyList<string> SummaryLines(IEnumerable<(Guid Id, string Label, bool IsConnected)> accounts) =>
        accounts.Select(a =>
        {
            var verdict = LastResultFor(a.Id);
            var at      = LastResultAtFor(a.Id);
            var shown   = a.IsConnected ? "connected" : "DISCONNECTED";
            return verdict == null
                ? $"{a.Label} — shown as {shown}; never probed"
                : $"{a.Label} — shown as {shown}; last probe {at:HH:mm:ss} said {verdict}";
        }).ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel before disposing so in-flight probes see OperationCanceledException rather than
        // ObjectDisposedException (see the IDisposable rules in CLAUDE.md).
        try { _shutdown.Cancel(); } catch { }
        foreach (var id in _loops.Keys.ToArray()) StopLoop(id);
        _shutdown.Dispose();
        _oneAtATime.Dispose();
        GC.SuppressFinalize(this);
    }
}
