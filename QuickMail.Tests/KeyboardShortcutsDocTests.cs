using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// docs/KEYBOARD-SHORTCUTS.md is the developer reference for the main window's command registry,
/// and it had drifted: about sixty registered commands (the calendar family, the sort and filter
/// commands, Close Message, Load Pictures and more) were missing from its table. These tests read
/// the registrations straight from the source, the same way <see cref="ShortcutExposureTests"/>
/// does, and fail when the table and the code disagree.
/// </summary>
public class KeyboardShortcutsDocTests
{
    private const string TableHeading = "## Registered shortcut table (MainWindow)";

    /// <summary>
    /// Main-window command ids deliberately left out of the table, each with the reason. Empty
    /// today: every registered command is documented. Add an entry only with a real reason.
    /// </summary>
    private static readonly Dictionary<string, string> Undocumented = new(StringComparer.Ordinal)
    {
    };

    private static readonly Regex Registration =
        new(@"new CommandDefinition\(", RegexOptions.Compiled);

    private static readonly Regex LiteralId =
        new(@"new CommandDefinition\(\s*id:\s*""([^""]+)""", RegexOptions.Compiled);

    // An id that is not a string literal: a variable built at run time, such as commandId.
    private static readonly Regex NonLiteralId =
        new(@"new CommandDefinition\(\s*id:\s*[^\s"",][^,]*,", RegexOptions.Compiled);

    // How those run-time ids are built: var commandId = $"theme.apply.{theme.Id}";
    private static readonly Regex RunTimeIdFamily =
        new(@"commandId\s*=\s*\$""([\w.]+)\{", RegexOptions.Compiled);

    [Fact]
    public void EveryMainWindowCommand_HasARowInTheShortcutTable()
    {
        var rowIds = TableRowMentions();
        var missing = Registrations().LiteralIds
            .Where(id => !rowIds.Contains(id) && !Undocumented.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These commands are registered by MainViewModel or MainWindow but have no row in the " +
            $"'{TableHeading}' table of docs/KEYBOARD-SHORTCUTS.md. Add a row (key, `id`, title) " +
            "for each, or list it in Undocumented with the reason: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryIdInTheShortcutTable_IsRegistered()
    {
        var registered = Registrations();
        var documentedFamilies = registered.Families.Select(f => f + "{id}").ToHashSet(StringComparer.Ordinal);

        var stale = TableIdColumn()
            .Where(id => !registered.LiteralIds.Contains(id) && !documentedFamilies.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These ids have a row in docs/KEYBOARD-SHORTCUTS.md, but nothing in MainViewModel or " +
            "MainWindow registers them. Fix the id or remove the row: " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryRunTimeIdFamily_HasARowInTheShortcutTable()
    {
        var registered = Registrations();

        // Each registration whose id is not a literal must come from a family this test can see,
        // or a new kind of run-time id could slip in with no row.
        Assert.Equal(registered.Families.Count, registered.NonLiteralCount);

        var idColumn = TableIdColumn();
        foreach (var family in registered.Families)
            Assert.True(idColumn.Contains(family + "{id}"),
                $"Commands with ids '{family}…' are registered at run time; docs/KEYBOARD-SHORTCUTS.md " +
                $"needs one row for the family, with `{family}{{id}}` as its id.");
    }

    [Fact]
    public void EveryRegistration_IsReadByTheseTests()
    {
        var registered = Registrations();

        // A registration written another way (a positional id, say) would be invisible to the
        // checks above, and a regex that stopped matching would make them pass on nothing.
        Assert.Equal(registered.Total, registered.LiteralCount + registered.NonLiteralCount);
        Assert.True(registered.LiteralIds.Count > 100,
            $"Only {registered.LiteralIds.Count} command ids were found; the source parsing is broken.");
    }

    [Fact]
    public void UndocumentedAllowlist_NamesOnlyRegisteredIds()
    {
        var registered = Registrations().LiteralIds;
        foreach (var (id, reason) in Undocumented)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"Undocumented entry '{id}' needs a reason.");
            Assert.True(registered.Contains(id),
                $"Undocumented lists '{id}', which is no longer registered; remove the entry.");
        }
    }

    private sealed record RegistrationScan(
        HashSet<string> LiteralIds, int LiteralCount, int NonLiteralCount, int Total, HashSet<string> Families);

    private static RegistrationScan Registrations()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        int literal = 0, nonLiteral = 0, total = 0;

        foreach (var source in Sources())
        {
            var text = File.ReadAllText(source);
            total += Registration.Count(text);
            foreach (Match m in LiteralId.Matches(text))
            {
                literal++;
                ids.Add(m.Groups[1].Value);
            }
            nonLiteral += NonLiteralId.Count(text);
            foreach (Match m in RunTimeIdFamily.Matches(text))
                families.Add(m.Groups[1].Value);
        }

        return new RegistrationScan(ids, literal, nonLiteral, total, families);
    }

    /// <summary>
    /// The files that register into the main window's registry: MainViewModel and its partials,
    /// and MainWindow's code-behind.
    /// </summary>
    private static IEnumerable<string> Sources()
    {
        var viewModels = Path.Combine(RepoRoot(), "QuickMail", "ViewModels");
        yield return Path.Combine(viewModels, "MainViewModel.cs");
        foreach (var partial in Directory.GetFiles(viewModels, "MainViewModel.*.cs").OrderBy(p => p, StringComparer.Ordinal))
            yield return partial;
        yield return Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml.cs");
    }

    private static string TableSection()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "KEYBOARD-SHORTCUTS.md"));
        var start = doc.IndexOf(TableHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"docs/KEYBOARD-SHORTCUTS.md has no '{TableHeading}' section.");
        var end = doc.IndexOf("\n## ", start + TableHeading.Length, StringComparison.Ordinal);
        return end < 0 ? doc[start..] : doc[start..end];
    }

    /// <summary>Every id written in backticks on a row of the table, in any column.</summary>
    private static HashSet<string> TableRowMentions()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in TableSection().Split('\n').Where(l => l.StartsWith('|')))
            foreach (Match m in Regex.Matches(line, @"`([a-z]\w*(?:\.[\w{}]+)+)`"))
                ids.Add(m.Groups[1].Value);
        return ids;
    }

    /// <summary>The ids in the table's Command ID column.</summary>
    private static HashSet<string> TableIdColumn() =>
        Regex.Matches(TableSection(), @"^\|[^|\n]*\|\s*`([^`]+)`\s*\|", RegexOptions.Multiline)
             .Select(m => m.Groups[1].Value)
             .ToHashSet(StringComparer.Ordinal);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }
}
