using System;
using System.IO;
using System.Runtime.InteropServices;

namespace QuickMail.Helpers;

/// <summary>
/// Creates and removes the QuickMail desktop shortcut. The shortcut targets the running
/// executable; under a Velopack install that path (%LocalAppData%\QuickMail\current\QuickMail.exe)
/// is stable across updates, so the link stays valid after the app updates itself.
/// </summary>
public static class DesktopShortcut
{
    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "QuickMail.lnk");

    public static bool Exists() => File.Exists(ShortcutPath);

    /// <summary>Returns false when no shortcut was created (unknown process path, or the
    /// WScript.Shell COM class is unavailable) so callers never report a false success.</summary>
    public static bool Create()
    {
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target)) return false;

        // WScript.Shell is the only shell-link writer reachable without a COM interop assembly.
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "QuickMail";
            link.Save();
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    public static void Delete()
    {
        try { File.Delete(ShortcutPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
