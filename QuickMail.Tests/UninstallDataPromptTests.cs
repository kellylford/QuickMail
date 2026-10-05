// The uninstall-time data prompt must not ask during an upgrade (#245): a Windows Installer major
// upgrade removes the old copy first, and its uninstall hook cannot tell that from an uninstall.
// The decision is made by the PowerShell script itself, so these tests run the real script, with
// powershell.exe as the hook does, against a scratch "install", a scratch Uninstall key and a
// mutex of their own standing in for Windows Installer's, with short waits. -DryRun logs the
// decision instead of showing the question, so nothing here ever puts a dialog on screen — and
// nothing reads this machine's real Add/Remove Programs entries or installer state.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public sealed class UninstallDataPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"quickmail-prompt-test-{Guid.NewGuid():N}");
    private readonly string _uninstallKey = $@"Software\QuickMail.Tests\Uninstall-{Guid.NewGuid():N}";
    private readonly string _mutexName = $@"Local\QuickMail.Tests.Installer-{Guid.NewGuid():N}";
    private readonly string _exe;
    private readonly string _dataDir;
    private readonly string _log;
    private readonly string _script;

    public UninstallDataPromptTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "current"));
        _exe = Path.Combine(_root, "current", "QuickMail.exe");
        _dataDir = Path.Combine(_root, "Roaming", "QuickMail");
        _log = Path.Combine(_root, "uninstall.log");
        _script = Path.Combine(_root, "prompt.ps1");
        File.WriteAllText(_exe, "stand-in");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(_script, UninstallDataPrompt.Script);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_uninstallKey, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\QuickMail.Tests"))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
            {
                try { Registry.CurrentUser.DeleteSubKey(@"Software\QuickMail.Tests", throwOnMissingSubKey: false); }
                catch (InvalidOperationException) { } // a parallel test created a child meanwhile
            }
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Process Start(int removalWait = 10, int quiet = 3, int maxWait = 60)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{_script}\" -Exe \"{_exe}\" -DataDir \"{_dataDir}\" " +
            $"-Log \"{_log}\" -RemovalWaitSeconds {removalWait} -QuietSeconds {quiet} -MaxWaitSeconds {maxWait} " +
            $"-MutexName \"{_mutexName}\" -UninstallRoot \"HKCU:\\{_uninstallKey}\" -DryRun")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi)!;
    }

    private string Finish(Process p)
    {
        Assert.True(p.WaitForExit(90_000), "the prompt script did not finish");
        p.Dispose();
        return File.Exists(_log) ? File.ReadAllText(_log) : "";
    }

    private async Task WaitForStart()
    {
        // The script logs as its first act; acting on the "install" before then would test nothing.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(_log) && File.ReadAllText(_log).Contains("prompt script started")) return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Fail("the prompt script did not start within 30 seconds");
    }

    private static Task Delay(int ms) => Task.Delay(ms, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Uninstall_FilesStayGone_Asks()
    {
        var p = Start();
        await WaitForStart();
        File.Delete(_exe);

        var log = Finish(p);

        Assert.Contains("would ask", log);
        Assert.DoesNotContain("not asking", log);
    }

    [Fact]
    public async Task Upgrade_FilesComeBack_DoesNotAsk()
    {
        var p = Start(quiet: 8);
        await WaitForStart();
        File.Delete(_exe);
        await Delay(1500);   // the new copy installs
        File.WriteAllText(_exe, "new version");

        var log = Finish(p);

        Assert.Contains("installed again (an upgrade); not asking", log);
        Assert.DoesNotContain("would ask", log);
    }

    [Fact]
    public async Task Upgrade_InstallerBusyPastTheQuietWindow_StillDoesNotAsk()
    {
        // The case the mutex is for: the new copy arrives later than the quiet window, but the
        // installer is executing throughout, so "still gone" is never believed early.
        using var installer = new Mutex(initiallyOwned: false, _mutexName);
        var p = Start(quiet: 2);
        await WaitForStart();
        File.Delete(_exe);
        await Delay(5000);   // well past the 2 s quiet window
        File.WriteAllText(_exe, "new version");

        var log = Finish(p);

        Assert.Contains("installed again (an upgrade); not asking", log);
        Assert.DoesNotContain("would ask", log);
    }

    [Fact]
    public async Task InstallerBusyForever_GivesUpWaiting_AndAsks()
    {
        // Some unrelated installation holding Windows Installer must delay the question, never
        // suppress it.
        using var installer = new Mutex(initiallyOwned: false, _mutexName);
        var p = Start(quiet: 2, maxWait: 4);
        await WaitForStart();
        File.Delete(_exe);

        Assert.Contains("would ask", Finish(p));
    }

    [Theory]
    [InlineData("MSI:QuickMail")]
    [InlineData("QuickMail")]
    public async Task StillInAddRemovePrograms_DoesNotAsk(string row)
    {
        // Installed again somewhere else, or a second copy remains: the data is still in use.
        Registry.CurrentUser.CreateSubKey($@"{_uninstallKey}\{row}").Dispose();
        var p = Start();
        await WaitForStart();
        File.Delete(_exe);

        var log = Finish(p);

        Assert.Contains($"still installed ({row}); not asking", log);
        Assert.DoesNotContain("would ask", log);
    }

    [Fact]
    public void FilesNeverRemoved_DoesNotAsk()
    {
        // The uninstall failed or was cancelled: QuickMail is still there, so nothing to offer.
        var log = Finish(Start(removalWait: 2));

        Assert.Contains("still installed; not asking", log);
    }

    [Fact]
    public async Task NoDataFolder_DoesNotAsk()
    {
        Directory.Delete(_dataDir, recursive: true);
        var p = Start();
        await WaitForStart();
        File.Delete(_exe);

        Assert.Contains("no data folder; not asking", Finish(p));
    }

    [Fact]
    public async Task Script_DeletesItself()
    {
        var p = Start();
        await WaitForStart();
        File.Delete(_exe);
        Finish(p);

        Assert.False(File.Exists(_script));
    }

    [Fact]
    public void Arguments_QuotePaths_AndRunTheRealPrompt()
    {
        var args = UninstallDataPrompt.Arguments(@"C:\T\p.ps1", @"C:\Users\A B\AppData\Local\QuickMail\current\QuickMail.exe",
                                                 @"C:\Users\A B\AppData\Roaming\QuickMail", @"C:\T\u.log");

        Assert.Contains("-File \"C:\\T\\p.ps1\"", args);
        Assert.Contains("-Exe \"C:\\Users\\A B\\AppData\\Local\\QuickMail\\current\\QuickMail.exe\"", args);
        Assert.Contains("-DataDir \"C:\\Users\\A B\\AppData\\Roaming\\QuickMail\"", args);
        Assert.Contains($"-QuietSeconds {UninstallDataPrompt.DefaultQuietSeconds}", args);
        // The real prompt watches the real installer and the real Add/Remove Programs.
        Assert.DoesNotContain("-DryRun", args);
        Assert.DoesNotContain("-MutexName", args);
        Assert.DoesNotContain("-UninstallRoot", args);
    }
}
