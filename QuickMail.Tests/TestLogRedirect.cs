using System.IO;
using System.Runtime.CompilerServices;
using QuickMail.Services;

namespace QuickMail.Tests;

/// <summary>
/// Redirects <see cref="LogService"/> away from the user's real
/// <c>%APPDATA%\QuickMail\quickmail.log</c> for the whole test run.
///
/// LogService defaults its output to the live app profile's log file and only moves when
/// <see cref="LogService.Configure(string)"/> is called (the app calls it for <c>--profileDir</c>).
/// Tests never called it, so any production code exercised under test appended fixture noise
/// (fake accounts, deliberate "boom"/"denied" errors) into the developer's actual QuickMail log —
/// which made the real log useless for diagnosing live issues. The module initializer below runs
/// once when the test assembly loads, before any test, and points logging at an isolated temp dir.
/// </summary>
internal static class TestLogRedirect
{
    /// <summary>The single shared temp log dir for the whole test run. Tests that reconfigure
    /// LogService to their own dir must restore it here on teardown (see LogServiceTests.Dispose),
    /// or later tests recreate the just-deleted per-test dir and leak it.</summary>
    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "QuickMailTestLogs");

    [ModuleInitializer]
    internal static void Redirect()
    {
        Directory.CreateDirectory(Dir);
        LogService.Configure(Dir);
    }
}
