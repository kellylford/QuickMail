namespace QuickMail.Helpers;

/// <summary>
/// Which message editor this run of QuickMail is using, for a bug report's Environment section.
/// The setting alone does not answer that: with the setting on, a compose window still falls back
/// to the previous editor when the new one cannot start, and a report about the editor that does
/// not say which one was on screen costs a round trip with the reporter.
/// </summary>
public static class MessageEditorStatus
{
    private static volatile bool s_fellBack;

    /// <summary>
    /// Called by a compose window each time it had to fall back to the previous editor. Stays set
    /// for the rest of the run: a report may be written long after the window that fell back closed.
    /// </summary>
    public static void NoteFellBack() => s_fellBack = true;

    /// <summary>
    /// The text for the report. No personal information: one of three fixed strings.
    /// </summary>
    public static string Describe(bool useNewEditor) => Describe(useNewEditor, s_fellBack);

    /// <summary>
    /// The decision itself, kept free of the process-wide flag so tests can cover all of it
    /// without touching state other tests share.
    /// </summary>
    internal static string Describe(bool useNewEditor, bool fellBack)
    {
        if (!useNewEditor) return "Previous (new editor turned off in Settings)";
        return fellBack ? "Previous (the new editor could not start)" : "New";
    }
}
