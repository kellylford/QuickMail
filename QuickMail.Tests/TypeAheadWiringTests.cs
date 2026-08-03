using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Deterministic guards for first-letter (type-ahead) wiring on the lists that use WPF's built-in
/// <c>TextSearch</c> — issue #371, and the stable replacement for the input-driven assertions that
/// used to carry it (issue #380).
///
/// <para>Two silent failure modes, both assertable without simulating a keystroke:</para>
/// <list type="number">
/// <item><b>The declaration disappears.</b> Several of these lists use an <c>ItemTemplate</c> rooted
/// in a panel, so <c>TextSearch</c> has no text to match unless the list declares
/// <c>TextSearch.TextPath</c>. Drop the attribute and typing a letter does nothing.</item>
/// <item><b>The target property is renamed.</b> <c>TextPath</c> is a string, so renaming the CLR
/// property leaves the XAML compiling and the lookup silently resolving to nothing. A text-only
/// check cannot catch this, so each row names the item type and the test reflects the property.</item>
/// </list>
///
/// <para>
/// Plain <c>[Fact]</c>s: XML parsing plus reflection. No <c>Application</c>, STA thread, shown
/// window, synthesized input, or dependence on elapsed time, so nothing on the host can perturb them.
/// </para>
///
/// <para><b>What these do NOT cover</b> — stated so the list is not mistaken for exhaustive:</para>
/// <list type="bullet">
/// <item>They enumerate lists that <i>already declare</i> a TextPath, so they can never flag a list
/// that <i>needs</i> one and lacks it.</item>
/// <item>Nothing ties a row's <c>ItemType</c> to the list's real <c>ItemsSource</c>. Retargeting a
/// list to a different item type that lacks the property is as silent as the rename this does catch.</item>
/// <item>QuickMail's own hand-rolled accumulator (<c>TypeAheadPrefixTracker</c> +
/// <c>TypeAheadMatcher</c>, serving the folder tree, message list, group trees, and the folder
/// picker's tree) is a separate mechanism that declares no TextPath; it is covered
/// deterministically by <see cref="TypeAheadLogicTests"/> (issue #415).</item>
/// <item>Keys stolen before <c>TextSearch</c> sees them (mnemonics, <c>PreviewKeyDown</c> handlers) —
/// the way type-ahead has actually broken in this repo. See the notes in
/// <see cref="AddressBookTypeAheadTests"/>.</item>
/// <item>These read source text, so they pass on XAML that does not compile.</item>
/// </list>
/// </summary>
public class TypeAheadWiringTests
{
    /// <summary>
    /// One row per list that uses WPF <c>TextSearch</c>. <c>Xaml</c> is relative to <c>QuickMail/</c>
    /// so <c>Views/</c> and <c>Controls/</c> can both be registered and same-named files cannot
    /// collide. <see cref="EveryTextSearchListInTheApp_IsRegisteredHere"/> fails if one is added
    /// without a row here.
    /// </summary>
    private sealed record Site(string Xaml, string ElementName, string TextPath, Type ItemType);

    private static readonly Site[] Sites =
    [
        new("Views/AddressBookWindow.xaml",    "ContactList",      "TypeAheadText",           typeof(ContactModel)),
        new("Views/AddressBookWindow.xaml",    "GroupsList",       "Name",                    typeof(GroupModel)),
        new("Views/AddressBookWindow.xaml",    "GroupMembersList", "TypeAheadText",           typeof(ContactModel)),
        new("Views/AccountManagerDialog.xaml", "AccountListBox",   "AccountLabelWithDefault", typeof(AccountModel)),
        new("Views/MainWindow.xaml",           "AccountList",      "AccountLabel",            typeof(AccountModel)),
        new("Views/CommandPaletteWindow.xaml", "CommandList",      "Title",                   typeof(CommandDefinition)),
        new("Views/FolderPickerWindow.xaml",   "FolderListBox",    "FolderPath",              typeof(FolderPickerWindow.FolderPickerItem)),
        new("Views/WatchedConversationsWindow.xaml", "WatchList",  "Label",                   typeof(WatchRow)),
    ];

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string TextPathAttr = "TextSearch.TextPath";
    private const string TextEnabledAttr = "IsTextSearchEnabled";

    // ── The guards ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryTypeAheadList_DeclaresItsTextPath()
    {
        var problems = new List<string>();
        foreach (var site in Sites)
        {
            var element = FindElement(site);
            if (element is null)
            {
                problems.Add($"{site.Xaml}: no element with x:Name=\"{site.ElementName}\" found.");
                continue;
            }

            var declared = (string?)element.Attribute(TextPathAttr);
            if (declared is null)
                problems.Add($"{site.Xaml}/{site.ElementName}: no {TextPathAttr}. "
                           + "Without it, typing a letter on this list does nothing.");
            else if (declared != site.TextPath)
                problems.Add($"{site.Xaml}/{site.ElementName}: {TextPathAttr} is \"{declared}\", "
                           + $"expected \"{site.TextPath}\".");
        }

        Assert.True(problems.Count == 0, Report("Type-ahead declaration missing or changed", problems));
    }

    [Fact]
    public void EveryTextPath_ResolvesToAPublicPropertyOnTheItemType()
    {
        var problems = new List<string>();
        foreach (var site in Sites)
        {
            // TextPath supports dotted paths (e.g. "Folder.Name"); every segment must resolve.
            var owner = site.ItemType;
            foreach (var segment in site.TextPath.Split('.'))
            {
                var prop = owner.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
                if (prop is null)
                {
                    problems.Add($"{site.Xaml}/{site.ElementName}: {TextPathAttr}=\"{site.TextPath}\" — "
                               + $"'{segment}' is not a public instance property on {owner.Name}. "
                               + "A rename here breaks type-ahead silently; the XAML still compiles.");
                    break;
                }
                if (!prop.CanRead)
                {
                    problems.Add($"{site.Xaml}/{site.ElementName}: {owner.Name}.{segment} is not readable.");
                    break;
                }
                owner = prop.PropertyType;
            }
        }

        Assert.True(problems.Count == 0, Report("Type-ahead TextPath does not resolve", problems));
    }

    [Fact]
    public void NoTypeAheadList_DisablesTextSearch()
    {
        var problems = new List<string>();
        foreach (var site in Sites)
        {
            var enabled = (string?)FindElement(site)?.Attribute(TextEnabledAttr);
            if (string.Equals(enabled, "False", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{site.Xaml}/{site.ElementName}: declares {TextPathAttr} but sets "
                           + $"{TextEnabledAttr}=\"False\", which turns type-ahead off anyway.");
        }

        Assert.True(problems.Count == 0, Report("Type-ahead disabled on a list that declares a TextPath", problems));
    }

    /// <summary>
    /// WPF's TextSearch is DISABLED by default on TreeView and TreeViewItem (unlike ListBox,
    /// ListView, and ComboBox), so a bare <c>TextSearch.TextPath</c> on a TreeView does
    /// nothing — and even with <c>IsTextSearchEnabled="True"</c> it would only match one
    /// level's items, never the visible tree. Exactly this shipped in v0.8.32: the folder
    /// picker's tree got a TextPath, the release note claimed type-ahead, and typing did
    /// nothing (issue #418). Trees must use the hand-rolled route
    /// (<c>TypeAheadPrefixTracker</c> + <c>TreeViewFocusHelper.TrySelectNextMatch</c>) instead.
    /// </summary>
    [Fact]
    public void NoTreeView_DeclaresATextSearchTextPath()
    {
        var problems = new List<string>();
        foreach (var (relative, doc) in XamlDocuments())
        {
            foreach (var element in doc.Descendants()
                                       .Where(e => e.Name.LocalName is "TreeView" or "TreeViewItem"
                                                && e.Attribute(TextPathAttr) != null))
            {
                var name = (string?)element.Attribute(X + "Name") ?? element.Name.LocalName;
                problems.Add($"{relative}/{name}: {TextPathAttr} on a {element.Name.LocalName} is inert — "
                           + "TreeView text search is disabled by default, and enabling it only matches "
                           + "one level's items. Wire PreviewTextInput to TypeAheadPrefixTracker + "
                           + "TreeViewFocusHelper.TrySelectNextMatch instead (see FolderPickerWindow).");
            }
        }

        Assert.True(problems.Count == 0, Report("TextSearch declared on a TreeView, where it does nothing", problems));
    }

    [Fact]
    public void EveryTextSearchListInTheApp_IsRegisteredHere()
    {
        var registered = Sites.Select(s => (s.Xaml, s.ElementName)).ToHashSet();
        var problems = new List<string>();

        foreach (var (relative, doc) in XamlDocuments())
        {
            // Elements carrying the attached property directly.
            foreach (var element in doc.Descendants().Where(e => e.Attribute(TextPathAttr) != null))
            {
                var name = (string?)element.Attribute(X + "Name");
                if (name is null)
                    problems.Add($"{relative}: <{element.Name.LocalName}> declares {TextPathAttr} but has no "
                               + "x:Name, so it cannot be registered. Give the element an x:Name.");
                else if (!registered.Contains((relative, name)))
                    problems.Add($"{relative}/{name}: type-ahead list not registered in TypeAheadWiringTests.Sites.");
            }

            // Style/Setter route: a <Setter Property="TextSearch.TextPath"> applies to every element
            // the style targets, so it cannot be attributed to one named list. The guards above are
            // element-scoped and would silently not cover it, so flag it rather than pass.
            foreach (var _ in doc.Descendants()
                                 .Where(e => e.Name.LocalName == "Setter"
                                          && (string?)e.Attribute("Property") == TextPathAttr))
            {
                problems.Add($"{relative}: {TextPathAttr} is set via a <Setter>, which these guards do not "
                           + "cover (they are element-scoped). Declare it on the list element instead, or "
                           + "extend TypeAheadWiringTests to resolve styles.");
            }
        }

        Assert.True(problems.Count == 0, Report("Unregistered or uncoverable type-ahead list", problems));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Report(string headline, List<string> problems) =>
        $"{headline}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", problems);

    private static XElement? FindElement(Site site)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", site.Xaml.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"XAML file not found: {path}");

        // XDocument skips comments when walking Descendants(), so a commented-out or merely
        // described attribute cannot read as a live declaration in either direction. It also handles
        // single-quoted attributes, whitespace around '=', and entity escapes — all of which a
        // hand-rolled text scan gets wrong, and wrongly reported as a confusing failure.
        return XDocument.Load(path)
            .Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(X + "Name") == site.ElementName);
    }

    /// <summary>
    /// Every XAML file under Views/, Controls/ and Styles/, recursively — Styles/ because that is
    /// where a Style-based TextSearch setter would live, and recursively so a new subdirectory cannot
    /// become a blind spot. Matches ThemeRegressionGuardTests' directory set.
    /// </summary>
    private static IEnumerable<(string Relative, XDocument Doc)> XamlDocuments()
    {
        var root = RepoRoot();
        var projectDir = Path.Combine(root, "QuickMail");

        foreach (var sub in new[] { "Views", "Controls", "Styles" })
        {
            var dir = Path.Combine(projectDir, sub);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(projectDir, file)
                                   .Replace(Path.DirectorySeparatorChar, '/');
                yield return (relative, XDocument.Load(file));
            }
        }
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
