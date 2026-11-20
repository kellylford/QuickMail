using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace QuickMail.Tests;

/// <summary>
/// Names the foreground threads still alive when the test process exits, with their stacks.
///
/// <para>
/// xunit waits about ten seconds at the end of a run for foreground threads, then forces the process
/// to exit and fails a run in which every test passed: <c>[FATAL ERROR] TestPipelineException</c>,
/// "Foreground threads were left running, forcing process exit" in the trx. That says nothing about
/// WHICH thread, it strikes only some CI runs, and it has never reproduced in the test VM — so the
/// only place to catch the culprit is the run that fails. Marking Xunit.StaFact's threads background
/// (<see cref="ShutDownStaDispatcherAttribute"/>) was not enough on its own.
/// </para>
///
/// <para>
/// Off unless <c>QUICKMAIL_FOREGROUND_THREAD_REPORT</c> names a file; CI sets it and uploads the
/// file with the trx. The forced exit raises <see cref="AppDomain.ProcessExit"/>, where this takes a
/// snapshot of the process with ClrMD and writes every live foreground thread's managed stack.
/// </para>
/// </summary>
internal static class ForegroundThreadReport
{
    public const string EnvVar = "QUICKMAIL_FOREGROUND_THREAD_REPORT";

    // A module initializer in a test assembly is the one hook that runs before any test and in every
    // runner, which is what registering a process-exit handler needs.
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Test assembly, not a library; the handler must be in place before any test runs.")]
    [ModuleInitializer]
    internal static void Register()
    {
        var path = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(path)) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write(path);
    }

    private static void Write(string path)
    {
        try
        {
            var current = Environment.CurrentManagedThreadId;
            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using var runtime = target.ClrVersions[0].CreateRuntime();

            var foreground = runtime.Threads
                .Where(t => t.IsAlive && (t.State & ClrThreadState.TS_Background) == 0)
                .ToList();
            // The thread running this handler is foreground on every exit; anything more is a straggler.
            if (foreground.All(t => t.ManagedThreadId == current)) return;

            var report = new StringBuilder();
            report.AppendLine($"Foreground threads alive at process exit ({DateTime.UtcNow:O}):");
            foreach (var thread in foreground)
            {
                report.AppendLine();
                report.AppendLine($"Managed thread {thread.ManagedThreadId}, OS thread 0x{thread.OSThreadId:x}, " +
                                  $"state {thread.State}{(thread.ManagedThreadId == current ? " (running this report)" : "")}");
                foreach (var frame in thread.EnumerateStackTrace(includeContext: false, maxFrames: 60))
                    report.AppendLine("    " + (frame.Method?.Signature ?? frame.FrameName ?? frame.Kind.ToString()));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, report.ToString());
        }
        catch (Exception ex)
        {
            // A diagnostic must never be the reason a run fails; say why it produced nothing instead.
            try { File.WriteAllText(path, $"Foreground thread report failed: {ex}"); } catch { }
        }
    }
}
