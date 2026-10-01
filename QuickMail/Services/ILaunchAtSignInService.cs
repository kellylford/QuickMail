namespace QuickMail.Services;

/// <summary>What Windows will do with QuickMail at the next sign-in (issue #770).</summary>
public enum LaunchAtSignInState
{
    /// <summary>No startup entry for this profile.</summary>
    Off,

    /// <summary>Registered, and Windows will start it.</summary>
    On,

    /// <summary>Registered, but the user turned it off in Task Manager or Settings > Apps >
    /// Startup. Windows keeps the entry and skips it, so for the user this is off.</summary>
    DisabledInWindows,
}

/// <summary>
/// Starts QuickMail when the user signs in to Windows (issue #770), through the per-user Run key —
/// the mechanism Task Manager's Startup apps page and Settings > Apps > Startup list and control.
/// The registry is the source of truth; nothing about this is remembered in config.ini, so the
/// setting can never disagree with what Windows will actually do.
/// </summary>
public interface ILaunchAtSignInService
{
    /// <summary>False for portable copies and development builds: their path can move, which
    /// would leave Windows with a dead startup entry. Only the installed path is stable.</summary>
    bool IsSupported { get; }

    LaunchAtSignInState GetState();

    /// <summary>Registers this profile to start at sign-in. An explicit choice by the user, so it
    /// also clears a "disabled" mark Windows' startup settings left on the entry.</summary>
    void Enable();

    /// <summary>Removes this profile's startup entry, and Windows' on/off mark for it.</summary>
    void Disable();
}
