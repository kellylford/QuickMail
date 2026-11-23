using System;

namespace QuickMail.Models;

/// <summary>Where a message or auxiliary surface opens when the user activates it.</summary>
public enum MessageOpenMode
{
    ReadingPane = 0,   // today's behaviour, default
    Tab         = 1,   // opens in the tab strip in the main window
    Window      = 2,   // opens in a new top-level window
}

/// <summary>
/// Configuration for tab and window behaviour.
/// Persisted under the [windowing] section of config.ini.
/// </summary>
public class WindowingPreferences
{
    /// <summary>Where Enter / click on a message opens it.</summary>
    public MessageOpenMode MessageOpenMode { get; set; } = MessageOpenMode.ReadingPane;

    // ConfirmCloseTabWithUnsaved was removed in #1010: tabs only ever show messages, which have
    // nothing to save, so nothing read it. ConfigService ignores the key in an older config.ini.

    /// <summary>Reserved for v2. v1 always resets tabs at restart.</summary>
    public bool TabsRememberAcrossRestart { get; set; } = false;
}
