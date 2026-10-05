using System;

namespace QuickMail.Tests;

/// <summary>
/// Gate for tests that <b>launch child processes and wait on them in real time</b> — today the
/// uninstall data prompt's PowerShell script (<see cref="UninstallDataPromptTests"/>). Opt-in: set
/// <c>QUICKMAIL_RUN_PROCESS_TESTS=1</c> to run them. CI sets it, and the workflow fails if they did
/// not all execute there, so the opt-in cannot quietly mean "runs nowhere".
/// <para>
/// <b>Why opt-in.</b> On a developer machine with a screen reader's hooks loaded, full suite runs
/// that included these tests crashed the test host far more often (exit 0xC0000602, a native
/// fail-fast; seven of fourteen runs) than runs without them — but not never: a run with them
/// skipped also crashed (0xC0000005). So these tests are not the cause, which is still unknown;
/// they make it much more likely, and they were timing-sensitive under load. Do not "fix" the
/// crash by deleting them. A CI runner is the environment that can honestly assert "nothing else
/// is running", the same reasoning as <see cref="InputTests"/>. Use it the same way:
/// </para>
/// <code>
/// [Fact(Skip = ProcessTests.SkipReason,
///       SkipUnless = nameof(ProcessTests.Enabled), SkipType = typeof(ProcessTests))]
/// </code>
/// </summary>
internal static class ProcessTests
{
    /// <summary>Set to <c>1</c> to run child-process tests.</summary>
    public const string EnvVar = "QUICKMAIL_RUN_PROCESS_TESTS";

    public const string SkipReason =
        "Child-process test — set QUICKMAIL_RUN_PROCESS_TESTS=1 to run. Skipped by default because "
      + "it launches powershell.exe and waits on it in real time, which crashed the test host on a "
      + "developer machine with a screen reader running. CI runs it.";

    /// <summary>Read by xUnit's <c>SkipUnless</c> at execution time. Must stay public and static.</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);
}
