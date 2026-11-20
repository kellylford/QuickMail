using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Restarts QuickMail with the arguments it was started with (issue #507: a staged settings import
/// is applied by the next start). The new process is told this one's id with
/// <see cref="WaitForExitArg"/> and waits for it to end before claiming the per-profile
/// single-instance mutex; without that wait it would find this process still holding it, signal
/// it to come to the front, and exit.
/// </summary>
public static class AppRestart
{
    public const string WaitForExitArg = "--waitForExit";

    /// <summary>How long a new process waits for the old one before carrying on regardless.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <summary>Starts the replacement process. The caller then shuts this one down. False, logged, when it could not start.</summary>
    public static bool StartReplacement(string[] currentArgs)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        var args = BuildArguments(currentArgs, Environment.ProcessId);
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            return Process.Start(psi) is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            LogService.Log("Restart: could not start QuickMail again", ex);
            return false;
        }
    }

    /// <summary>
    /// The replacement's arguments: this process's own, less any earlier wait marker and the
    /// sign-in flag (a restart the user asked for is not a launch by Windows at sign-in, which
    /// would start it minimized), plus a wait for <paramref name="pid"/>.
    /// </summary>
    internal static List<string> BuildArguments(string[] currentArgs, int pid)
    {
        var args = StripWaitArgument(currentArgs)
            .Where(a => !a.Equals("--startup", StringComparison.OrdinalIgnoreCase))
            .ToList();
        args.Add(WaitForExitArg);
        args.Add(pid.ToString(CultureInfo.InvariantCulture));
        return args;
    }

    /// <summary>The arguments without <see cref="WaitForExitArg"/> and its value.</summary>
    public static string[] StripWaitArgument(string[] args)
    {
        var result = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(WaitForExitArg, StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            result.Add(args[i]);
        }
        return [.. result];
    }

    /// <summary>The arguments with --profileDir's value replaced by <paramref name="profileDir"/>.</summary>
    public static string[] WithProfileDir(string[] args, string profileDir)
    {
        var result = args.ToArray();
        for (var i = 0; i < result.Length - 1; i++)
            if (result[i].Equals("--profileDir", StringComparison.OrdinalIgnoreCase))
                result[i + 1] = profileDir;
        return result;
    }

    /// <summary>True when this process was started by <see cref="StartReplacement"/>.</summary>
    public static bool IsReplacement(string[] args) => ParseWaitPid(args) is not null;

    /// <summary>The process id after <see cref="WaitForExitArg"/>, or null.</summary>
    internal static int? ParseWaitPid(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(WaitForExitArg, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                return pid;
        return null;
    }

    /// <summary>Called first thing at startup: waits for the process being replaced, if any, to end.</summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        if (ParseWaitPid(args) is not { } pid || pid == Environment.ProcessId) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(MaxWait);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { /* already gone */ }
    }
}
