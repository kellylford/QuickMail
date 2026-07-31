using System;

namespace QuickMail.Tests;

/// <summary>
/// Gate for tests that drive <b>synthesized keyboard input</b> through the WPF input pipeline
/// against a shown window. They are opt-in — set <c>QUICKMAIL_RUN_INPUT_TESTS=1</c> to run them;
/// CI sets it, and the workflow fails if they did not execute there, so the opt-in cannot quietly
/// mean "runs nowhere".
/// <para>
/// Use it on the test, not via a derived attribute — a <c>FactAttribute</c> subclass trips
/// <c>xUnit3003</c> (losing source-file/line info that IDE test navigation needs) and is evaluated
/// at discovery rather than execution, so a cached discovery can disagree with the environment:
/// </para>
/// <code>
/// [StaFact(Skip = InputTests.SkipReason,
///          SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
/// </code>
/// <para>
/// <b>Why an explicit opt-in rather than detecting the environment.</b> The obvious candidate is
/// Windows' <c>SPI_GETSCREENREADER</c> flag, and it is the wrong tool: it is a single process-wide
/// boolean with no reference counting, so with two assistive tools running, the second one exiting
/// clears the flag while the first is still active. It cannot answer "is anything running right
/// now", and not every tool sets it in the first place. An opt-in makes no claim about the machine —
/// it says only "someone asserted this environment is quiet", which CI can honestly assert and a
/// developer's desktop cannot.
/// </para>
/// </summary>
internal static class InputTests
{
    /// <summary>Set to <c>1</c> to run synthesized-input tests.</summary>
    public const string EnvVar = "QUICKMAIL_RUN_INPUT_TESTS";

    public const string SkipReason =
        "Synthesized-input test — set QUICKMAIL_RUN_INPUT_TESTS=1 to run. Skipped by default because "
      + "it depends on focus and real elapsed time in a shown window; the wiring it covers is asserted "
      + "deterministically by TypeAheadWiringTests. See issue #380.";

    /// <summary>
    /// Read by xUnit's <c>SkipUnless</c> at execution time. Must stay public and static.
    /// </summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);
}
