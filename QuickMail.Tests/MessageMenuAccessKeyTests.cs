using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// With two menu items on one access key, pressing it moves between them instead of choosing either. The
/// message menus have had two such clashes: Reply and Create Rule from Message on R (fixed in #688), and
/// Reply All and Move to Archive on A (#692), in both the message context menu and the menu bar's
/// Message menu.
/// </summary>
public class MessageMenuAccessKeyTests
{
    [Fact]
    public void NoTwoItemsInTheMessageContextMenuShareAnAccessKey()
    {
        var xaml = MainWindowXaml();
        var start = xaml.IndexOf("x:Key=\"MessageContextMenu\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "MessageContextMenu is gone.");
        var menu = xaml[start..xaml.IndexOf("</ContextMenu>", start, StringComparison.Ordinal)];

        var headers = Regex.Matches(menu, "Header=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.All(headers, h => Assert.True(h.Contains('_'), $"'{h}' has no access key."));
        Assert.Empty(Clashes(headers));
    }

    [Fact]
    public void NoTwoItemsInTheMenuBarsMessageMenuShareAnAccessKey()
    {
        // The menu bar's Message menu and its siblings are indented 12 spaces and their direct items 16, so
        // the items of the Message menu are the 16-space headers before the next 12-space one.
        var xaml = MainWindowXaml();
        var start = xaml.IndexOf("<MenuItem Header=\"_Message\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The Message menu is gone.");
        var next = Regex.Match(xaml[(start + 1)..], @"\n {12}<MenuItem Header=");
        var menu = next.Success ? xaml.Substring(start, next.Index + 1) : xaml[start..];

        var headers = Regex.Matches(menu, "\n {16}<MenuItem Header=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("Reply _All", headers);   // the parse found the real items
        Assert.Empty(Clashes(headers));
    }

    /// <summary>Each access key used by more than one of <paramref name="headers"/>, with the items that share it.</summary>
    private static List<string> Clashes(IEnumerable<string> headers)
        => headers
            .Where(h => h.Contains('_'))
            .GroupBy(h => char.ToLowerInvariant(h[h.IndexOf('_') + 1]))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g)}")
            .ToList();

    private static string MainWindowXaml()
        => File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml"));

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
