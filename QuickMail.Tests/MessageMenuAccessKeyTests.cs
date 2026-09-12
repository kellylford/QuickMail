using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
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

    /// <summary>Every context menu in the main window, keyed or not: the sync range, account actions and
    /// attachment menus have no <c>x:Key</c>, and were once left out.</summary>
    public static TheoryData<string> ContextMenus()
    {
        var data = new TheoryData<string>();
        foreach (var menu in MainWindow().Descendants(Wpf + "ContextMenu"))
            data.Add(MenuId(menu));
        return data;
    }

    [Theory]
    [MemberData(nameof(ContextMenus))]
    public void NoTwoItemsInAContextMenuShareAnAccessKey(string menuId)
    {
        var menu = MainWindow().Descendants(Wpf + "ContextMenu").Single(m => MenuId(m) == menuId);

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

    [Fact]
    public void NoMenuItemShowsItsNameWithASpaceMissing()
    {
        // Moving Archive's access key once took the space after it along with the old underscore, so the
        // menus showed "ArchiveConversation". Each of those items has its own AutomationProperties.Name, so
        // it was still spoken correctly and could not be caught by ear.
        var pairs = MainWindow().Descendants(Wpf + "MenuItem")
            .Select(m => (Header: (string?)m.Attribute("Header"), Name: (string?)m.Attribute("AutomationProperties.Name")))
            .Where(p => p.Header is { } h && p.Name is { } n && !h.StartsWith('{') && !n.StartsWith('{'))
            .Select(p => (p.Header, Shown: Plain(p.Header!.Replace("_", "", StringComparison.Ordinal)), Spoken: Plain(p.Name!)))
            .ToList();
        Assert.NotEmpty(pairs);   // the parse found items that carry both

        var squashed = pairs
            .Where(p => Squash(p.Shown) == Squash(p.Spoken)
                        && !string.Equals(p.Shown, p.Spoken, StringComparison.OrdinalIgnoreCase))
            .Select(p => $"'{p.Header}' shows as '{p.Shown}' but is spoken as '{p.Spoken}'");
        Assert.Empty(squashed);
    }

    /// <summary>A menu's name for test output: its key, else its name, else its line in the file.</summary>
    private static string MenuId(XElement menu)
        => (string?)menu.Attribute(Xaml + "Key")
           ?? (string?)menu.Attribute(Xaml + "Name")
           ?? (string?)menu.Attribute("Name")
           ?? $"line {((IXmlLineInfo)menu).LineNumber}";

    private static string Plain(string text)
        => text.Replace("…", "", StringComparison.Ordinal).Replace("...", "", StringComparison.Ordinal).Trim();

    private static string Squash(string text)
        => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

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
        => XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml"), LoadOptions.SetLineInfo);

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
