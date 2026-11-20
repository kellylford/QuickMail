using System.Runtime.InteropServices;

namespace QuickMail.Services;

/// <summary>
/// Whether Windows reports it isn't a time for background sounds. This does NOT include Do Not
/// Disturb: measured in a VM, Windows goes on reporting that it accepts notifications with Do Not
/// Disturb on, and Microsoft documents no API for that state. Mute All Sounds covers it.
/// </summary>
public static class WindowsQuietState
{
    // QUERY_USER_NOTIFICATION_STATE
    private const int Busy = 2;
    private const int RunningD3DFullScreen = 3;
    private const int PresentationMode = 4;
    private const int QuietTime = 6;
    private const int App = 7;

    public static bool IsQuiet() =>
        SHQueryUserNotificationState(out var state) == 0 && IsQuietState(state);

    /// <summary>
    /// A failed query is not quiet, and nor is the screen being locked or asleep (QUNS_NOT_PRESENT):
    /// nothing is being interrupted then, and someone listening for mail still wants to hear it.
    /// </summary>
    internal static bool IsQuietState(int state) =>
        state is Busy or RunningD3DFullScreen or PresentationMode or QuietTime or App;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
