using System.Reflection;

namespace QuickMail.Helpers;

/// <summary>
/// The running application version as a display string, sourced from the assembly version.
/// The 4th (revision) component is shown only when it is non-zero, so normal releases read
/// "0.7.9" while a hotfix reads "0.7.9.1". Using this everywhere keeps the About dialog, the
/// Help "running version" entry, the SMTP User-Agent, and the update check in agreement — and,
/// because the update check parses this string, a hotfix build compares at full precision and
/// does not mistake its own release for a newer one.
/// </summary>
public static class AppVersion
{
    public static string Display { get; } = Compute();

    private static string Compute()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null) return "0.0.0";
        return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
    }
}
