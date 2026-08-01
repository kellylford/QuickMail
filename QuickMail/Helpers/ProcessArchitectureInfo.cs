using System.Runtime.InteropServices;

namespace QuickMail.Helpers;

/// <summary>
/// Answers "is this copy of QuickMail running under emulation on an ARM64 device?" so the
/// x64 build can point those users at the native ARM64 build (issue #18).
///
/// Nothing migrates a QuickMail install from one CPU architecture to another: Velopack
/// records the channel an installed copy came from and only ever checks that channel, so an
/// x64 install on an ARM64 machine is never offered the ARM64 build. Without the app saying
/// so, those users have no way to learn the native build exists.
/// </summary>
internal static class ProcessArchitectureInfo
{
    /// <summary>
    /// True when this is an x64 process on an ARM64 operating system — i.e. running under
    /// Windows' Prism emulation while a native build is available.
    ///
    /// The two properties genuinely differ here: since .NET 7 corrected it,
    /// <c>OSArchitecture</c> reports the real machine (Arm64) while
    /// <c>ProcessArchitecture</c> reports this process (X64). On a native ARM64 build both
    /// read Arm64, so this is false and nothing is surfaced.
    /// </summary>
    public static bool IsEmulatedOnArm64 =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        RuntimeInformation.OSArchitecture      == Architecture.Arm64;
}
