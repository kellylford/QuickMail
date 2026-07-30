using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Deterministic guards for first-letter (type-ahead) navigation wiring — issue #371, and the
/// stable replacement for the input-driven assertions that used to carry this (issue #380).
///
/// <para>
/// Type-ahead on these lists breaks in exactly two silent ways, and both are assertable without
/// simulating a single keystroke:
/// </para>
/// <list type="number">
/// <item><b>The declaration disappears.</b> Several of these lists use an <c>ItemTemplate</c> whose
/// root is a panel, so WPF's <c>TextSearch</c> has no text to match unless the list declares
/// <c>TextSearch.TextPath</c> explicitly. Drop the attribute and typing a letter does nothing.</item>
/// <item><b>The target property is renamed.</b> <c>TextPath</c> is a string, so renaming the
/// underlying CLR property leaves the XAML compiling and binding silently resolving to nothing.
/// This is the failure a XAML-only check would miss, so each row names the item type and the test
/// reflects the property.</item>
/// </list>
///
/// <para>
/// Why these are plain <c>[Fact]</c>s: they read the checked-in XAML as text and use reflection.
/// No <c>Application</c>, no STA thread, no shown window, no synthesized input, no dependence on
/// elapsed time — so nothing on the host machine can perturb them. The end-to-end proof that a real
/// keystroke reaches <c>TextSearch</c> lives in <see cref="AddressBookTypeAheadTests"/> behind
/// <see cref="InputStaFactAttribute"/>.
/// </para>
/// </summary>
public class TypeAheadWiringTests
{
    /// <summary>
    /// One row per list in the app that offers type-ahead. <see cref="EveryTypeAheadListInTheApp_IsRegisteredHere"/>
    /// fails if a new one is added without being registered, so this table cannot silently go stale.
    /// </summary>
    private sealed record Site(string XamlFile, string ElementName, string TextPath, Type ItemType);

    private static readonly Site[] Sites =
    [
        new("AddressBookWindow.xaml",   "ContactList",      "TypeAheadText",            typeof(ContactModel)),
        new("AddressBookWindow.xaml",   "GroupsList",       "Name",                     typeof(GroupModel)),
        new("AddressBookWindow.xaml",   "GroupMembersList", "TypeAheadText",            typeof(ContactModel)),
        new("AccountManagerDialog.xaml","AccountListBox",   "AccountLabelWithDefault",  typeof(AccountModel)),
        new("MainWindow.xaml",          "AccountList",      "AccountLabel",             typeof(AccountModel)),
        new("CommandPaletteWindow.xaml","CommandList",      "Title",                    typeof(CommandDefinition)),
        new("FolderPickerWindow.xaml",  "FolderTreeView",   "Label",                    typeof(FolderTreeNode)),
    ];

    // ── The guards ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryTypeAheadList_DeclaresItsTextPath()
    {
        var problems = new List<string>();
        foreach (var site in Sites)
        {
            var tag = OpeningTag(site);
            if (tag.Length == 0)
            {
                problems.Add($"{site.XamlFile}: no element named '{site.ElementName}' found.");
                continue;
            }
            var expected = $"TextSearch.TextPath=\"{site.TextPath}\"";
            if (!tag.Contains(expected, StringComparison.Ordinal))
                problems.Add($"{site.XamlFile}/{site.ElementName}: expected {expected}. "
                           + "Without it, typing a letter on this list does nothing.");
        }

        Assert.True(problems.Count == 0, Report("Type-ahead declaration missing or changed", problems));
    }

    [Fact]
    public void EveryTextPath_ResolvesToAPublicPropertyOnTheItemType()
    {
        var problems = new List<string>();
        foreach (var site in Sites)
        {
            var prop = site.ItemType.GetProperty(
                site.TextPath, BindingFlags.Public | BindingFlags.Instance);

            if (prop is null)
                problems.Add($"{site.XamlFile}/{site.ElementName}: TextSearch.TextPath=\"{site.TextPath}\" "
                           + $"has no public instance property on {site.ItemType.Name}. "
                           + "A rename here breaks type-ahead silently — XAML still compiles.");
            else if (!prop.CanRead)
                problems.Add($"{site.XamlFile}/{site.ElementName}: {site.ItemType.Name}.{site.TextPath} is not readable.");
        }

        Assert.True(problems.Count == 0, Report("Type-ahead TextPath does not resolve", problems));
    }

    [Fact]
    public void NoTypeAheadList_DisablesTextSearch()
    {
        // IsTextSearchEnabled defaults to true, so most sites omit it. Declaring a TextPath and then
        // turning the feature off is contradictory and would break type-ahead just as thoroughly as
        // deleting the path.
        var problems = new List<string>();
        foreach (var site in Sites)
        {
            var tag = OpeningTag(site);
            if (tag.Contains("IsTextSearchEnabled=\"False\"", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{site.XamlFile}/{site.ElementName}: declares a TextPath but sets IsTextSearchEnabled=\"False\".");
        }

        Assert.True(problems.Count == 0, Report("Type-ahead disabled on a list that declares a TextPath", problems));
    }

    [Fact]
    public void EveryTypeAheadListInTheApp_IsRegisteredHere()
    {
        // Keeps the table honest: a new type-ahead list must be registered above, which forces the
        // author to name its item type and get the reflection guard for free.
        var root = RepoRoot();
        var registered = Sites
            .Select(s => (s.XamlFile, s.ElementName))
            .ToHashSet();

        var problems = new List<string>();
        foreach (var file in XamlFiles(root))
        {
            var text = ReadXaml(file);
            var name = Path.GetFileName(file);

            for (var at = text.IndexOf("TextSearch.TextPath", StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf("TextSearch.TextPath", at + 1, StringComparison.Ordinal))
            {
                var tag = EnclosingOpeningTag(text, at);
                var elementName = AttributeValue(tag, "x:Name");

                if (elementName is null)
                    problems.Add($"{name}: a TextSearch.TextPath is declared on an element with no x:Name, "
                               + "so it cannot be registered in TypeAheadWiringTests. Give the element an x:Name.");
                else if (!registered.Contains((name, elementName)))
                    problems.Add($"{name}/{elementName}: type-ahead list not registered in TypeAheadWiringTests.Sites.");
            }
        }

        Assert.True(problems.Count == 0, Report("Unregistered type-ahead list", problems));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Report(string headline, List<string> problems) =>
        $"{headline}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", problems);

    private static string OpeningTag(Site site)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", "Views", site.XamlFile);
        Assert.True(File.Exists(path), $"XAML file not found: {path}");
        var text = ReadXaml(path);

        var at = text.IndexOf($"x:Name=\"{site.ElementName}\"", StringComparison.Ordinal);
        return at < 0 ? string.Empty : EnclosingOpeningTag(text, at);
    }

    /// <summary>
    /// Reads a XAML file with XML comments removed. These files document their own type-ahead
    /// wiring in prose (AddressBookWindow.xaml has a comment naming TextSearch.TextPath), and a
    /// commented-out or merely described attribute must not read as a live declaration in either
    /// direction — it would both mask a real removal and raise a phantom unregistered site.
    /// </summary>
    private static string ReadXaml(string path)
    {
        var text = File.ReadAllText(path);
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("<!--", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, open - i);
            var close = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
            if (close < 0) break;              // unterminated comment: drop the remainder
            i = close + 3;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The full opening tag containing the character at <paramref name="index"/> — from its '&lt;'
    /// through the matching '&gt;', ignoring '&gt;' inside attribute values. Attributes routinely
    /// span many lines in this codebase, so a line-based match would miss them.
    /// </summary>
    private static string EnclosingOpeningTag(string text, int index)
    {
        var start = text.LastIndexOf('<', index);
        if (start < 0) return string.Empty;

        var inQuote = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') inQuote = !inQuote;
            else if (c == '>' && !inQuote) return text[start..(i + 1)];
        }
        return string.Empty;
    }

    private static string? AttributeValue(string tag, string attribute)
    {
        var needle = $"{attribute}=\"";
        var at = tag.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return null;
        var valueStart = at + needle.Length;
        var end = tag.IndexOf('"', valueStart);
        return end < 0 ? null : tag[valueStart..end];
    }

    // Views + Controls: the places a type-ahead list can live. Mirrors ThemeRegressionGuardTests.
    private static IEnumerable<string> XamlFiles(string root)
    {
        foreach (var sub in new[] { "Views", "Controls" })
        {
            var dir = Path.Combine(root, "QuickMail", sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.xaml"))
                yield return file;
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
