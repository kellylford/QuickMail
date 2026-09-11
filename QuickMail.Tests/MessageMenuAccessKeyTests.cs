using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// With two items in one menu on the same access key, pressing it moves between them instead of choosing
/// either. The message menus have had several: Reply and Create Rule from Message on R (fixed in #688);
/// Reply All with Move to Archive and with Grab Addresses on A, and Reply with the group menus' Archive
/// items on R (#692). The main window's XAML is read as XML, so only a menu's direct items are compared.
/// </summary>
public class MessageMenuAccessKeyTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static TheoryData<string> ContextMenuKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in MainWindow().Descendants(Wpf + "ContextMenu")
                     .Select(m => (string?)m.Attribute(Xaml + "Key")).OfType<string>())
            data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(ContextMenuKeys))]
    public void NoTwoItemsInAContextMenuShareAnAccessKey(string key)
    {
        var menu = MainWindow().Descendants(Wpf + "ContextMenu").Single(m => (string?)m.Attribute(Xaml + "Key") == key);

        Assert.Empty(Clashes(menu));
    }

    [Fact]
    public void NoTwoItemsInTheMenuBarsMessageMenuShareAnAccessKey()
    {
        var menu = MainWindow().Descendants(Wpf + "MenuItem").Single(m => (string?)m.Attribute("Header") == "_Message");

        Assert.Contains("Reply _All", DirectHeaders(menu));   // the parse found the real items
        Assert.Empty(Clashes(menu));
    }

    [Fact]
    public void EveryItemInTheMessageContextMenuHasAnAccessKey()
    {
        var menu = MainWindow().Descendants(Wpf + "ContextMenu")
            .Single(m => (string?)m.Attribute(Xaml + "Key") == "MessageContextMenu");

        Assert.All(DirectHeaders(menu), h => Assert.True(h.Contains('_'), $"'{h}' has no access key."));
    }

    /// <summary>The literal headers of a menu's own items: not nested submenus, and not bound headers.</summary>
    private static List<string> DirectHeaders(XElement menu)
        => menu.Elements(Wpf + "MenuItem")
            .Select(m => (string?)m.Attribute("Header"))
            .OfType<string>()
            .Where(h => !h.StartsWith('{'))
            .ToList();

    /// <summary>Each access key used by more than one of the menu's items, with the items that share it.</summary>
    private static List<string> Clashes(XElement menu)
        => DirectHeaders(menu)
            .Where(h => h.Contains('_'))
            .GroupBy(h => char.ToLowerInvariant(h[h.IndexOf('_') + 1]))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g)}")
            .ToList();

    private static XDocument MainWindow()
        => XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml"));

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
