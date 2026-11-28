// #1117: a start that failed before the main window appeared left QuickMail running with no
// window. That hidden process held the profile's single-instance lock, so every later start
// signaled it and exited, and QuickMail could not be opened again until the PC was restarted.
//
// The handler for unhandled UI-thread errors keeps the process alive on purpose, so a user is
// not left with a vanished window. That is right only once there is a window.

using Xunit;

namespace QuickMail.Tests;

public class StartupFailureExitTests
{
    [Fact]
    public void AnErrorBeforeTheMainWindowIsShown_EndsQuickMail()
        => Assert.True(App.ShouldExitAfterUnhandledError(mainWindowShown: false));

    [Fact]
    public void AnErrorAfterTheMainWindowIsShown_KeepsQuickMailRunning()
        => Assert.False(App.ShouldExitAfterUnhandledError(mainWindowShown: true));
}
