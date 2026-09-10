using System;
using System.IO;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Test runs a rule against the whole message list the main window had when the Rules Manager opened,
/// not the selection (MainWindow hands it <c>_vm.Messages</c>). The UI used to say "selected messages" in
/// four places. The two status lines are pinned by behaviour tests in UnifiedRulesViewModelTests; the
/// Test button's accessible name and the command-palette title are not reachable that way, so they are
/// read from source here, and nothing in the rules window may describe Test as using the selection.
/// </summary>
public class RulesWindowWordingTests
{
    [Theory]
    [InlineData("ViewModels/UnifiedRulesViewModel.cs")]
    [InlineData("Views/UnifiedRulesWindow.xaml")]
    [InlineData("Views/UnifiedRulesWindow.xaml.cs")]
    public void NothingDescribesTestAsUsingTheSelection(string file)
        => Assert.DoesNotContain("selected messages", Source(file), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void TheTestButton_IsNamedForTheMessageList()
        => Assert.Contains("AutomationProperties.Name=\"Test selected rule against the message list\"",
                           Source("Views/UnifiedRulesWindow.xaml"), StringComparison.Ordinal);

    [Fact]
    public void ThePaletteEntry_IsNamedForTheMessageList()
        => Assert.Contains("title: \"Test Rule Against the Message List\"",
                           Source("Views/UnifiedRulesWindow.xaml.cs"), StringComparison.Ordinal);

    private static string Source(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{path} not found.");
        return File.ReadAllText(path);
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
