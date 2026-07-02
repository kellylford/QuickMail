using System;
using System.Diagnostics;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Gatekeeper for URIs that leave the app via ShellExecute (links activated in message
/// bodies, preview windows, etc.). Message content is untrusted: the HTML sanitizer does
/// not rewrite anchor hrefs, so without this allow-list a crafted message could launch
/// any registered protocol handler (file:, search-ms:, ms-msdt:, …) the moment the user
/// activates a link. Only schemes a mail client legitimately hands to the OS are allowed.
/// </summary>
public static class ExternalUriPolicy
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>True when the URI is absolute and uses an allow-listed scheme.</summary>
    public static bool IsAllowed(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;

        foreach (var scheme in AllowedSchemes)
            if (string.Equals(parsed.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Opens the URI with the default handler if its scheme is allow-listed;
    /// otherwise logs and drops it. Returns true when the URI was handed to the shell.
    /// </summary>
    public static bool TryOpenExternal(string? uri)
    {
        if (!IsAllowed(uri))
        {
            LogService.Log($"ExternalUriPolicy: blocked non-allow-listed URI: {Truncate(uri)}");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri!) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Log($"ExternalUriPolicy: open failed for {Truncate(uri)}", ex);
            return false;
        }
    }

    private static string Truncate(string? uri) =>
        uri is null ? "(null)" : uri.Length <= 200 ? uri : uri[..200] + "…";
}
