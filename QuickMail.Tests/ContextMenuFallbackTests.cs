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
/// <para>The hazard is that <c>ContextMenuOpening</c> <b>bubbles</b>. A gesture raised inside a
/// control that owns its own <c>ContextMenu</c> passes through this Window-level handler on its way
/// up, before WPF opens the menu it found — so marking the event handled here cancels that control's
/// menu and substitutes the message one. Every such control must therefore be excluded explicitly.
/// The folder tree was (#255: Shift+F10 on a folder offered Reply/Reply All); the account list was
/// not, so Shift+F10 on an account gave the message menu, and neither were the toolbar's two
/// dropdown buttons. Reported against 0.8.48, though the defect long predates it.</para>
///
/// <para>This reads the XAML rather than driving a window: <c>ContextMenuEventArgs</c> has no public
/// constructor, so the event cannot be raised directly, and the synthesized-input harness that could
/// press the key for real is opt-in and skipped by default. What a sweep can do is the part that
/// actually regressed — notice a control that has a menu of its own and was not added to the guard.
/// It is what found the two toolbar buttons.</para>
/// </summary>
public class ContextMenuFallbackTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The message list is the fallback's own target, so it is the one menu-owning control that must
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

    /// <summary>
    /// The guard statement itself, not the whole handler. Scoped this tightly on purpose: the handler
    /// also carries a debug-log line naming the attachment list in an identical call, so a sweep over
    /// the whole body passed for that pane whether or not it was actually in the guard. Reading only
    /// the <c>if</c> means every assertion below is about the code that decides.
    ///
    /// <para>The cost is that it pins the guard's shape — replacing the explicit calls with a loop
    /// over an array of controls is behaviourally identical and would fail this. Rewrite the test
    /// alongside that refactor; do not drop it.</para>
    /// </summary>
    private static string FallbackGuard()
    {
        var source = Source("MainWindow.xaml.cs");
        var handler = source.IndexOf("private void OnWindowContextMenuOpening", StringComparison.Ordinal);
        Assert.True(handler >= 0, "OnWindowContextMenuOpening not found. Was it renamed?");

        var start = source.IndexOf("if (e.OriginalSource is DependencyObject src", handler, StringComparison.Ordinal);
        Assert.True(start >= 0, "The fallback's bail-out guard was not found. Was it restructured?");

        var end = source.IndexOf("return;", start, StringComparison.Ordinal);
        Assert.True(end > start, "The bail-out guard has no return. Was it restructured?");
        return source[start..(end + "return;".Length)];
    }

    /// <summary>
    /// Every named control in MainWindow.xaml that has a context menu of its own: declared as an
    /// inline property element (&lt;ListBox.ContextMenu&gt;), as an attribute pointing at a resource,
    /// or assigned from its own <c>ContextMenuOpening</c> handler.
    ///
    /// <para>Such a handler used to be treated as exempting a control from the guard, on the grounds
    /// that it marks the event handled. It does not: the three group trees set <c>e.Handled</c> only
    /// when nothing is selected, so in every ordinary case the gesture reaches the Window unhandled.
    /// They were saved by the fallback's own <c>IsMessagesView</c> test being false in their views —
    /// an accident, and exactly the kind this test exists to stop depending on.</para>
    /// </summary>
    private static List<string> MenuOwningControlNames()
    {
        var root = XDocument.Parse(Source("MainWindow.xaml")).Root!;
        var names = new List<string>();

        foreach (var element in root.Descendants())
        {
            var name = (string?)element.Attribute(Xaml + "Name");
            if (string.IsNullOrEmpty(name)) continue;

            var ownsInline = element.Elements()
                .Any(child => child.Name.LocalName.EndsWith(".ContextMenu", StringComparison.Ordinal));
            var ownsByAttribute = element.Attribute("ContextMenu") != null;
            var assignsInHandler = element.Attribute("ContextMenuOpening") != null;

            if (ownsInline || ownsByAttribute || assignsInHandler)
                names.Add(name);
        }

        return names;
    }

    public static TheoryData<string> ControlsOwningAMenu()
    {
        var data = new TheoryData<string>();
        foreach (var name in MenuOwningControlNames().Where(n => n != FallbackTarget))
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ControlsOwningAMenu))]
    public void EveryControlThatOwnsAMenu_IsExcludedFromTheMessageMenuFallback(string controlName)
    {
        // Giving a control a ContextMenu is not enough: without an entry here the Window handler
        // cancels it and opens the message menu instead. The failure is invisible in review — the
        // XAML is correct and the menu simply never appears.
        Assert.Contains($"IsDescendantOf({controlName}, src)", FallbackGuard(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSweepFoundEveryControlItShould()
    {
        // Guards the theory above against quietly narrowing: with nothing in the data it still
        // passes, which is how the account list went unnoticed for as long as it did.
        var names = MenuOwningControlNames();

        Assert.Contains("AccountList", names);
        Assert.Contains("ReadingPaneAttachmentList", names);
        Assert.Contains(FallbackTarget, names);
        // The two toolbar buttons whose ContextMenu is their dropdown. The sweep is what found them:
        // Shift+F10 on either was answered with the message menu, the same defect as the account
        // list, reached from the toolbar instead of the account pane.
        Assert.Contains("SyncRangeButton", names);
        Assert.Contains("ViewModeButton", names);
        // The three group trees assign their menu from a handler rather than in XAML.
        Assert.Contains("ConversationTree", names);
        Assert.Contains("SenderGroupTree", names);
        Assert.Contains("ToGroupTree", names);
    }

    [Fact]
    public void TheFolderTreeIsStillExcluded()
    {
        // The folder tree gets its menu from an ItemContainerStyle setter rather than a property on
        // the TreeView, so the XAML sweep above does not see it. It is the original #255 fix and has
        // to stay named in the guard regardless.
        Assert.Contains("IsDescendantOf(FolderList, src)", FallbackGuard(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageListIsNotExcluded()
    {
        // The fallback exists to open the message menu; excluding its own target would disable it
        // and bring back the Win32 system menu on Shift+F10 (#148).
        Assert.DoesNotContain($"IsDescendantOf({FallbackTarget}, src)", FallbackGuard(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The account menu acts on the selected account, and a WPF ListBox does not select on
    /// right-click. Before the fix above the account menu was effectively unreachable by mouse (the
    /// message menu opened instead), so making it reachable is what makes this matter: right-clicking
    /// one account while another was selected would delete, open or move the selected one.
    /// </summary>
    [Fact]
    public void RightClickingAnAccountSelectsIt()
    {
        var root = XDocument.Parse(Source("MainWindow.xaml")).Root!;
        var accountList = root.Descendants()
            .Single(e => (string?)e.Attribute(Xaml + "Name") == "AccountList");

        Assert.Equal("AccountList_PreviewMouseRightButtonDown",
                     (string?)accountList.Attribute("PreviewMouseRightButtonDown"));
        Assert.Contains("AccountList.SelectedItem = account;", Source("MainWindow.xaml.cs"),
                        StringComparison.Ordinal);
    }
}
