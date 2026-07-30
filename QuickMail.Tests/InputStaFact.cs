using System;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// An STA test that drives <b>synthesized keyboard input</b> through the real WPF input pipeline
/// against a shown window, and is therefore sensitive to anything else on the machine that reacts
/// to windows appearing and selection changing.
/// <para>
/// These are opt-in: they run only when <c>QUICKMAIL_RUN_INPUT_TESTS=1</c> is set (CI sets it), and
/// report as <b>skipped with a reason</b> everywhere else. The gate is deliberately an explicit
/// opt-in rather than an attempt to detect the environment:
/// </para>
/// <list type="bullet">
/// <item>Windows' <c>SPI_GETSCREENREADER</c> flag is a single process-wide boolean with no
/// reference counting, so with two assistive tools running, the second one exiting clears the flag
/// while the first is still active. It cannot answer "is anything running right now", and not every
/// tool sets it in the first place.</item>
/// <item>An opt-in makes no claim about the machine. It says only "someone asserted this
/// environment is quiet", which is a claim CI can actually make and a developer's desktop cannot.</item>
/// </list>
/// <para>
/// The wiring these tests exercise is covered deterministically and unconditionally by
/// <see cref="TypeAheadWiringTests"/>, so skipping them locally loses no regression protection for
/// anything QuickMail itself owns.
/// </para>
/// </summary>
public sealed class InputStaFactAttribute : StaFactAttribute
{
    /// <summary>Set to <c>1</c> to run synthesized-input tests.</summary>
    public const string EnvVar = "QUICKMAIL_RUN_INPUT_TESTS";

    public InputStaFactAttribute()
    {
        if (!Enabled)
            Skip = $"Synthesized-input test — set {EnvVar}=1 to run. Skipped by default because it "
                 + "depends on real elapsed time and focus in a shown window; the wiring it covers is "
                 + "asserted deterministically by TypeAheadWiringTests.";
    }

    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);
}
