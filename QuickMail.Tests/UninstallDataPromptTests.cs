// The uninstall-time data prompt must not ask during an upgrade (#245): a Windows Installer major
// upgrade removes the old copy first, and its uninstall hook cannot tell that from an uninstall.
// The decision is made by the PowerShell script itself, so these tests run the real script, with
// powershell.exe as the hook does, against a scratch "install" and short waits. -DryRun logs the
// decision instead of showing the question, so nothing here ever puts a dialog on screen.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public sealed class UninstallDataPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"quickmail-prompt-test-{Guid.NewGuid():N}");
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
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Process Start(int removalWait = 10, int quiet = 3)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{_script}\" -Exe \"{_exe}\" -DataDir \"{_dataDir}\" " +
            $"-Log \"{_log}\" -RemovalWaitSeconds {removalWait} -QuietSeconds {quiet} -MaxWaitSeconds 60 -DryRun")
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

    private static async Task WaitForStart(string log, CancellationToken ct)
    {
        // The script logs as its first act; acting on the "install" before then would test nothing.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!(File.Exists(log) && File.ReadAllText(log).Contains("prompt script started")) && DateTime.UtcNow < until)
            await Task.Delay(100, ct);
    }

    [Fact]
    public async Task Uninstall_FilesStayGone_Asks()
    {
        var p = Start();
        await WaitForStart(_log, TestContext.Current.CancellationToken);
        File.Delete(_exe);

        var log = Finish(p);

        Assert.Contains("would ask", log);
        Assert.DoesNotContain("not asking", log);
    }

    [Fact]
    public async Task Upgrade_FilesComeBack_DoesNotAsk()
    {
        var p = Start(quiet: 8);
        await WaitForStart(_log, TestContext.Current.CancellationToken);
        File.Delete(_exe);
        await Task.Delay(1500, TestContext.Current.CancellationToken);   // the new copy installs
        File.WriteAllText(_exe, "new version");

        var log = Finish(p);

        Assert.Contains("installed again (an upgrade); not asking", log);
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
        await WaitForStart(_log, TestContext.Current.CancellationToken);
        File.Delete(_exe);

        Assert.Contains("no data folder; not asking", Finish(p));
    }

    [Fact]
    public async Task Script_DeletesItself()
    {
        var p = Start();
        await WaitForStart(_log, TestContext.Current.CancellationToken);
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
        Assert.DoesNotContain("-DryRun", args);
    }
}
