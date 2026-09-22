using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// <c>MainWindow.OnWindowContextMenuOpening</c> is a fallback: when a list rebuild has removed the
/// focused item, WPF has no element to route <c>ContextMenuOpening</c> from, so the event reaches
/// the Window and Shift+F10 would show the Win32 system menu instead of QuickMail's (#148). The
/// handler answers that by opening the message menu.
///
/// <para>The hazard is that <c>ContextMenuOpening</c> <b>bubbles</b>. A gesture raised inside a pane
/// that owns its own <c>ContextMenu</c> passes through this Window-level handler on its way up,
/// before WPF opens the menu it found — so marking the event handled here cancels that pane's menu
/// and substitutes the message one. Every such pane must therefore be excluded explicitly. The
/// folder tree was (#255: Shift+F10 on a folder offered Reply/Reply All), and the account list was
/// not, so Shift+F10 on an account gave the message menu — reported against 0.8.48, though the
/// defect long predates it.</para>
///
/// <para>This reads the XAML rather than driving a window because there is no way to raise a real
/// <c>ContextMenuEventArgs</c> from a test — the class has no public constructor. What it can do is
/// the part that actually regressed: notice a pane that gained a menu of its own and was not added
/// to the guard.</para>
/// </summary>
public class ContextMenuFallbackTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The message list is the fallback's own target, so it is the one menu-owning pane that must
    /// NOT be excluded — excluding it would turn the fallback off altogether.
    /// </summary>
    private const string FallbackTarget = "MessageList";

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }

    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", file));

    /// <summary>The body of the fallback handler, from its signature to the start of the next member.</summary>
    private static string FallbackBody()
    {
        var source = Source("MainWindow.xaml.cs");
        var start = source.IndexOf("private void OnWindowContextMenuOpening", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnWindowContextMenuOpening not found — was it renamed?");

        var end = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        if (end < 0) end = source.Length;
        return source[start..end];
    }

    /// <summary>
    /// Every named control in MainWindow.xaml that owns a ContextMenu of its own, either as an
    /// inline property element (&lt;ListBox.ContextMenu&gt;) or as an attribute pointing at a
    /// resource. A control that assigns its menu in a ContextMenuOpening handler marks the event
    /// handled and so never reaches the Window — those are found separately below.
    /// </summary>
    public static TheoryData<string> PanesOwningAMenu()
    {
        var data = new TheoryData<string>();
        foreach (var name in MenuOwningPaneNames().Where(n => n != FallbackTarget))
            data.Add(name);
        return data;
    }

    private static List<string> MenuOwningPaneNames()
    {
        var root = XDocument.Parse(Source("MainWindow.xaml")).Root!;
        var names = new List<string>();

        foreach (var element in root.Descendants())
        {
            var name = (string?)element.Attribute(Xaml + "Name");
            if (string.IsNullOrEmpty(name)) continue;

            // <Foo.ContextMenu> declared under this element, or ContextMenu="{StaticResource …}".
            var ownsInline = element.Elements()
                .Any(child => child.Name.LocalName.EndsWith(".ContextMenu", StringComparison.Ordinal));
            var ownsByAttribute = element.Attribute("ContextMenu") != null;
            // A pane that builds its menu in its own ContextMenuOpening handler sets e.Handled, and
            // the fallback's first line returns on that — it needs no entry in the guard.
            var handlesItself = element.Attribute("ContextMenuOpening") != null;

            if ((ownsInline || ownsByAttribute) && !handlesItself)
                names.Add(name);
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(PanesOwningAMenu))]
    public void EveryPaneThatOwnsAMenu_IsExcludedFromTheMessageMenuFallback(string paneName)
    {
        // Adding a ContextMenu to a pane in XAML is not enough: without an entry here the Window
        // handler cancels it and opens the message menu instead. The failure is silent in review —
        // the XAML is correct and the menu simply never appears.
        Assert.Contains($"IsDescendantOf({paneName},", FallbackBody(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSweepFoundTheAccountListAndTheAttachmentList()
    {
        // Guards the theory above against quietly narrowing: with no panes in the data it still
        // passes, which is how the account list went unnoticed for as long as it did.
        var names = MenuOwningPaneNames();

        Assert.Contains("AccountList", names);
        Assert.Contains("ReadingPaneAttachmentList", names);
        Assert.Contains(FallbackTarget, names);
        // The two toolbar buttons whose ContextMenu is their dropdown. The sweep is what found
        // them: Shift+F10 on either was answered with the message menu, the same defect as the
        // account list, reached from the toolbar instead of the account pane.
        Assert.Contains("SyncRangeButton", names);
        Assert.Contains("ViewModeButton", names);
    }

    [Fact]
    public void TheFolderTreeIsStillExcluded()
    {
        // The folder tree gets its menu from an ItemContainerStyle setter rather than a property on
        // the TreeView, so the XAML sweep above does not see it. It is the original #255 fix and has
        // to stay named in the guard regardless.
        Assert.Contains("IsDescendantOf(FolderList,", FallbackBody(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageListIsNotExcluded()
    {
        // The fallback exists to open the message menu; excluding its own target would disable it
        // and bring back the Win32 system menu on Shift+F10 (#148).
        Assert.DoesNotContain($"IsDescendantOf({FallbackTarget},", FallbackBody(), StringComparison.Ordinal);
    }
}
