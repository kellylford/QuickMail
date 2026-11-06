using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuickMail.Services;

/// <summary>
/// Offers QuickMail in Explorer's Open with for .eml files (#939), without becoming their default:
/// a "QuickMail.eml" file type, listed among .eml's Open with choices. Whatever opens .eml files now
/// (Outlook, Mail, nothing) still does, until the user picks QuickMail and asks to always use it.
///
/// <para>Everything goes in the current user's part of the registry (HKCU\Software\Classes), as a
/// per-user install has no right to more. Written by the installed copy's install and update hooks,
/// so it points at the install's stable path; removed by its uninstall hook. A portable copy
/// registers nothing.</para>
/// </summary>
public static class EmailFileAssociation
{
    public const string ProgId = "QuickMail.eml";
    private const string Extension = ".eml";
    internal const string ClassesPath = @"Software\Classes";

    /// <summary>The command Explorer runs to open a file with QuickMail.</summary>
    public static string OpenCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>
    /// Registers <paramref name="exePath"/> as a way to open .eml files. <paramref name="classes"/> is
    /// replaceable for tests; the app passes HKCU\Software\Classes.
    /// </summary>
    public static void Register(string exePath, RegistryKey? classes = null)
    {
        using var owned = classes == null ? Registry.CurrentUser.CreateSubKey(ClassesPath) : null;
        var root = classes ?? owned!;

        using (var prog = root.CreateSubKey(ProgId))
        {
            prog.SetValue("", "Email message");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exePath}\",0");
            using (var command = prog.CreateSubKey(@"shell\open\command")) command.SetValue("", OpenCommand(exePath));
        }
        // An entry in OpenWithProgids adds a choice; it does not change .eml's default.
        using (var openWith = root.CreateSubKey($@"{Extension}\OpenWithProgids"))
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

        if (classes == null) NotifyShell();
    }

    /// <summary>Removes what <see cref="Register"/> wrote, and nothing else under .eml.</summary>
    public static void Unregister(RegistryKey? classes = null)
    {
        using var owned = classes == null ? Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true) : null;
        var root = classes ?? owned;
        if (root == null) return;

        root.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        using (var openWith = root.OpenSubKey($@"{Extension}\OpenWithProgids", writable: true))
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);

        if (classes == null) NotifyShell();
    }

    /// <summary>Whether <paramref name="exePath"/> is registered. For tests and diagnostics.</summary>
    public static bool IsRegistered(string exePath, RegistryKey? classes = null)
    {
        using var owned = classes == null ? Registry.CurrentUser.OpenSubKey(ClassesPath) : null;
        var root = classes ?? owned;
        using var command = root?.OpenSubKey($@"{ProgId}\shell\open\command");
        using var openWith = root?.OpenSubKey($@"{Extension}\OpenWithProgids");
        return command?.GetValue("") as string == OpenCommand(exePath)
               && openWith?.GetValueNames().Contains(ProgId, StringComparer.OrdinalIgnoreCase) == true;
    }

    // Explorer caches associations; this tells it to look again.
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShell()
    {
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Only a refresh hint: Explorer picks the change up on its own later.
        }
    }
}
