using System;
using System.IO;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The View half of landing focus before rows leave (#667, #670). The ViewModel half is covered in
/// <see cref="MessageRemovalFocusTests"/>; this is read from source because it needs a real, active window.
/// </summary>
public class FocusLandingGuardTests
{
    [Fact]
    public void LandingFocus_NeverPullsItIntoAnInactiveMainWindow() // #670
    {
        // Unwatching from a message window or the Watched Conversations manager, or a slow move the user has
        // left, lands focus in the main window while another window is active. Focusing a row then would take
        // the user out of the window they are in, so the check has to come before any row is focused.
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml.cs"));
        var start = code.IndexOf("private bool FocusSelectedMessageRowNow()", StringComparison.Ordinal);
        Assert.True(start >= 0, "FocusSelectedMessageRowNow is gone.");
        var body = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];

        var guard = body.IndexOf("if (!IsActive) return true;", StringComparison.Ordinal);
        var focus = body.IndexOf(".Focus()", StringComparison.Ordinal);
        Assert.True(guard >= 0, "FocusSelectedMessageRowNow no longer checks that the main window is active.");
        Assert.True(focus < 0 || guard < focus, "The active-window check must come before a row is focused.");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
