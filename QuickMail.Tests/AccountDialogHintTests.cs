using System.IO;
using System.Linq;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Instructions and usage tips must go through AccessibilityHelper.Announce, where the user's
/// AnnounceHints preference applies — not into AutomationProperties.HelpText, which the screen
/// reader speaks regardless of what the user has asked QuickMail for. Same rule as
/// AutomationProperties.Name, which the Accessibility Checklist already spells out.
/// </summary>
public class AccountDialogHintTests
{
    private static string ReadView(string fileName)
    {
        // Walk up from the test binary to the repo root.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "QuickMail", "Views", fileName);
        Assert.True(File.Exists(path), $"could not locate {fileName} (looked at {path})");
        return File.ReadAllText(path);
    }

    [Theory]
    [InlineData("AddAccountDialog.xaml")]
    [InlineData("AccountManagerDialog.xaml")]
    public void TheAccountDialogsCarryNoHelpText(string fileName)
    {
        var xaml = ReadView(fileName);

        Assert.DoesNotContain("AutomationProperties.HelpText", xaml, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AddAccountDialog.xaml")]
    [InlineData("AccountManagerDialog.xaml")]
    public void TheAccountDialogsRouteFocusHintsThroughTheAnnouncementSystem(string fileName)
    {
        var xaml = ReadView(fileName);

        Assert.Contains("GotKeyboardFocus=\"OnFieldFocused\"", xaml, System.StringComparison.Ordinal);
    }

    // An AutomationProperties.Name is a short label. A sentence in one is an instruction wearing a
    // label's clothes, and it bypasses the announcement preferences the same way HelpText does.
    [Theory]
    [InlineData("AddAccountDialog.xaml")]
    [InlineData("AccountManagerDialog.xaml")]
    public void NoAutomationNameIsASentence(string fileName)
    {
        var xaml = ReadView(fileName);

        var offenders = System.Text.RegularExpressions.Regex
            .Matches(xaml, @"AutomationProperties\.Name=""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .Where(v => v.Contains(". ", System.StringComparison.Ordinal) || v.EndsWith('.'))
            .ToList();

        Assert.True(offenders.Count == 0,
            "AutomationProperties.Name must be a short label, not a sentence: " + string.Join(" | ", offenders));
    }
}
