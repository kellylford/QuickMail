namespace QuickMail.Helpers;

/// <summary>
/// Which message editor this run of QuickMail has actually been using, for a bug report's
/// Environment section. The setting alone does not answer that. A message window chooses its
/// editor when it opens, so a setting changed since then describes the next window and not the
/// one the report is about; and with the setting on, a window still falls back to the previous
/// editor when the new one cannot start, or for HTML format alone when one message will not load.
/// So each message window records what it got, and the report says that.
/// <para>Written and read on the UI thread only.</para>
/// </summary>
public static class MessageEditorStatus
{
    /// <summary>What the message windows of this run have recorded.</summary>
    /// <param name="AnyMessageOpened">False until the first message window opens.</param>
    /// <param name="LastSettingOn">The setting as the most recent message window read it.</param>
    /// <param name="LastUsesNewEditor">Whether that window's body is the new editor.</param>
    /// <param name="HtmlFallbacks">
    /// How many times HTML format alone went back to the previous editor while the rest of the
    /// window stayed on the new one.
    /// </param>
    internal readonly record struct State(
        bool AnyMessageOpened, bool LastSettingOn, bool LastUsesNewEditor, int HtmlFallbacks);

    private static State s_state;

    /// <summary>A message window has chosen its editor.</summary>
    public static void NoteMessageOpened(bool settingOn, bool usesNewEditor)
        => s_state = s_state with { AnyMessageOpened = true, LastSettingOn = settingOn, LastUsesNewEditor = usesNewEditor };

    /// <summary>A window that had chosen the new editor had to give it up for the whole body.</summary>
    public static void NoteFellBack()
        => s_state = s_state with { LastUsesNewEditor = false };

    /// <summary>HTML format went back to the previous editor in a window still using the new one.</summary>
    public static void NoteHtmlFellBack()
        => s_state = s_state with { HtmlFallbacks = s_state.HtmlFallbacks + 1 };

    /// <summary>The text for the report, from what this run recorded.</summary>
    public static string Describe(bool settingNow) => Describe(settingNow, s_state);

    /// <summary>
    /// The decision itself, kept free of the process-wide state so tests can cover all of it.
    /// Fixed words and a count only: this text is published in a public issue.
    /// </summary>
    internal static string Describe(bool settingNow, State state)
    {
        const string TurnedOff = "Previous (the new editor is turned off in Settings)";

        if (!state.AnyMessageOpened)
            return (settingNow ? "New" : TurnedOff) + "; no message window opened this session";

        string text = state.LastUsesNewEditor ? "New"
                    : state.LastSettingOn ? "Previous (the new editor could not start)"
                    : TurnedOff;

        if (state.HtmlFallbacks > 0)
            text += $"; HTML format went back to the previous editor {state.HtmlFallbacks} " +
                    (state.HtmlFallbacks == 1 ? "time" : "times") + " this session";

        // The line describes the most recent message window. Say so when the setting has been
        // changed since it opened, or the line reads as contradicting what Settings shows.
        if (settingNow != state.LastSettingOn)
            text += settingNow ? "; turned on in Settings since that message opened"
                               : "; turned off in Settings since that message opened";
        return text;
    }
}
