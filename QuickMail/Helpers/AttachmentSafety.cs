using System;
using System.IO;
using System.Linq;

namespace QuickMail.Helpers;

/// <summary>
/// Shared safety checks for attachment filenames. Attachment names are server-supplied
/// (and therefore attacker-controlled), so every path that writes an attachment to disk
/// must go through <see cref="SanitizeFileName"/>, and every path that launches one must
/// consult <see cref="IsDangerousExtension"/> first.
/// </summary>
public static class AttachmentSafety
{
    /// <summary>
    /// Extensions Windows will execute (directly or via a registered handler) rather than
    /// open in a viewer. Kept in one place so the open-attachment confirmations in
    /// MainViewModel and ComposeViewModel can never drift apart again.
    /// </summary>
    private static readonly string[] DangerousExtensions =
    [
        ".exe", ".bat", ".cmd", ".com", ".pif", ".scr", ".msi", ".msp", ".msix",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ws",
        ".hta", ".jar", ".msc", ".cpl", ".reg", ".scf", ".lnk", ".url",
        ".chm", ".application", ".appref-ms", ".iso", ".img", ".vhd", ".vhdx",
    ];

    public static bool IsDangerousExtension(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return DangerousExtensions.Contains(ext);
    }

    /// <summary>
    /// Reduces a server-supplied attachment name to a bare, writable filename:
    /// strips directory components (defeats "../../Startup/evil.exe" and absolute paths),
    /// replaces characters invalid in Windows filenames, and trims trailing dots/spaces
    /// (which Windows silently drops, enabling extension spoofing). Never returns empty.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "attachment";

        // Path.GetFileName does not treat '/' as a separator inside invalid-char sequences
        // on all inputs, so normalize both separator styles before stripping directories.
        var name = fileName.Replace('/', '\\');
        name = Path.GetFileName(name);

        var invalid = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) >= 0)
        {
            var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            name = new string(chars);
        }

        name = name.TrimEnd('.', ' ');
        return name.Length == 0 ? "attachment" : name;
    }
}
