using System;
using System.IO;
using Microsoft.Win32;

namespace QuickMail.Services;

/// <summary>
/// The per-user Run key implementation of <see cref="ILaunchAtSignInService"/> (issue #770).
///
/// Why the Run key and nothing else: it is the standard per-user startup mechanism, it needs no
/// administrator rights, Windows staggers its entries after sign-in rather than holding sign-in up
/// for them, and it is exactly what Task Manager's Startup apps page and Settings > Apps > Startup
/// show and switch. A scheduled task or a Startup-folder shortcut would work too, but would hide
/// QuickMail from at least one of those places.
///
/// Windows' own on/off switch lives beside it: turning an app off in Task Manager or Settings
/// leaves the Run value alone and writes a binary value of the same name under
/// Explorer\StartupApproved\Run whose first byte is odd (0x03, 0x07) when disabled and even
/// (0x02, 0x06) when enabled. A missing value means enabled.
///
/// One value per profile, so turning this on in a --profileDir test profile never replaces the
/// default profile's entry. The default profile's value is plain "QuickMail".
/// </summary>
public sealed class LaunchAtSignInService : ILaunchAtSignInService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The argument the Run entry passes, so the app knows Windows started it.</summary>
    public const string StartupArg = "--startup";

    internal const string BaseValueName = "QuickMail";

    private readonly string? _exePath;
    private readonly string _runKeyPath;
    private readonly string _approvedKeyPath;

    /// <param name="exePath">The executable to register; null when unknown.</param>
    /// <param name="customProfileDir">The --profileDir in effect, or null for the default profile.</param>
    /// <param name="isSupported">Whether this copy may register itself (installed copies only).</param>
    public LaunchAtSignInService(string? exePath, string? customProfileDir, bool isSupported)
        : this(exePath, customProfileDir, isSupported, RunKeyPath, ApprovedKeyPath) { }

    /// <summary>Tests point the two key paths at a scratch key under HKCU.</summary>
    internal LaunchAtSignInService(string? exePath, string? customProfileDir, bool isSupported,
                                   string runKeyPath, string approvedKeyPath)
    {
        _exePath = exePath;
        _runKeyPath = runKeyPath;
        _approvedKeyPath = approvedKeyPath;
        IsSupported = isSupported && !string.IsNullOrEmpty(exePath);
        ValueName = BuildValueName(customProfileDir);
        Command = string.IsNullOrEmpty(exePath) ? "" : BuildCommand(exePath, customProfileDir);
    }

    public bool IsSupported { get; }

    /// <summary>The Run value name for this profile.</summary>
    internal string ValueName { get; }

    /// <summary>The command line the Run value holds for this profile.</summary>
    internal string Command { get; }

    public LaunchAtSignInState GetState()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        if (run?.GetValue(ValueName) is not string command || !PointsAt(command, _exePath))
            return LaunchAtSignInState.Off;

        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath);
        return IsDisabledByWindows(approved?.GetValue(ValueName) as byte[])
            ? LaunchAtSignInState.DisabledInWindows
            : LaunchAtSignInState.On;
    }

    public void Enable()
    {
        if (!IsSupported) throw new InvalidOperationException("This copy of QuickMail cannot start at sign-in.");

        using (var run = Registry.CurrentUser.CreateSubKey(_runKeyPath))
            run.SetValue(ValueName, Command, RegistryValueKind.String);

        // Clear Windows' own mark rather than writing an "enabled" byte pattern of our own: a
        // missing value is what Windows itself treats as enabled, and leaves nothing of ours there.
        DeleteValue(_approvedKeyPath, ValueName);
    }

    public void Disable()
    {
        DeleteValue(_runKeyPath, ValueName);
        DeleteValue(_approvedKeyPath, ValueName);
    }

    /// <summary>
    /// Uninstall cleanup: removes every QuickMail startup entry — any profile — that starts
    /// <paramref name="exePath"/>, so an uninstalled QuickMail leaves nothing in Task Manager.
    /// Entries of the same name pointing at some other copy are not ours to remove.
    /// </summary>
    public static void RemoveAllFor(string exePath) => RemoveAllFor(exePath, RunKeyPath, ApprovedKeyPath);

    internal static void RemoveAllFor(string exePath, string runKeyPath, string approvedKeyPath)
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        if (run is null) return;

        foreach (var name in run.GetValueNames())
        {
            if (!IsOurValueName(name)) continue;
            if (run.GetValue(name) is not string command || !PointsAt(command, exePath)) continue;

            run.DeleteValue(name, throwOnMissingValue: false);
            DeleteValue(approvedKeyPath, name);
        }
    }

    // ── Pure helpers (unit-tested directly) ─────────────────────────────────────

    internal static string BuildValueName(string? customProfileDir)
    {
        if (customProfileDir is null) return BaseValueName;

        // Short but stable per directory; the same identity the single-instance guard uses, so
        // letter case and a trailing separator do not mint a second entry.
        var key = SingleInstanceService.ProfileKey(new[] { "--profileDir", customProfileDir });
        return $"{BaseValueName} ({key[..8]})";
    }

    internal static bool IsOurValueName(string name) =>
        name.Equals(BaseValueName, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(BaseValueName + " (", StringComparison.OrdinalIgnoreCase);

    internal static string BuildCommand(string exePath, string? customProfileDir)
    {
        var command = $"\"{exePath}\" {StartupArg}";
        if (customProfileDir is null) return command;

        // A trailing backslash would escape the closing quote when Windows splits the command
        // line ("C:\Data\" reads as C:\Data"). A drive root keeps its meaning as "D:\.".
        var dir = customProfileDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (dir.EndsWith(':')) dir += @"\.";
        return $"{command} --profileDir \"{dir}\"";
    }

    /// <summary>The executable a Run command starts: the quoted first token, or else everything
    /// up to the first space.</summary>
    internal static string ExecutableOf(string command)
    {
        var c = command.TrimStart();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end < 0 ? c[1..] : c[1..end];
        }
        var space = c.IndexOf(' ');
        return space < 0 ? c : c[..space];
    }

    /// <summary>Whether a Run command starts <paramref name="exePath"/>. An entry left by another
    /// copy (a portable exe, an older install location) is not this copy's entry.</summary>
    internal static bool PointsAt(string command, string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(ExecutableOf(command)), Path.GetFullPath(exePath),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsDisabledByWindows(byte[]? approval) =>
        approval is { Length: > 0 } && (approval[0] & 1) != 0;

    private static void DeleteValue(string keyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
