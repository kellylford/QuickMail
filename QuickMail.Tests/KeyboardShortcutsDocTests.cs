using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// docs/KEYBOARD-SHORTCUTS.md is the developer reference for the main window's command registry,
/// and it had drifted: about sixty registered commands (the calendar family, the sort and filter
/// commands, Close Message, Load Pictures and more) were missing from its table. These tests read
/// the registrations straight from the source, as <see cref="ShortcutExposureTests"/> does, and fail
/// when the table and the code disagree: a command with no row of its own, a row for a command that
/// no longer exists, or a row whose key is not the command's default key.
/// </summary>
public class KeyboardShortcutsDocTests
{
    private const string TableHeading = "## Registered shortcut table (MainWindow)";

    /// <summary>
    /// Main-window command ids deliberately left without a row of their own, each with the reason.
    /// Empty today: every registered command has one. Add an entry only with a real reason.
    /// </summary>
    private static readonly Dictionary<string, string> Undocumented = new(StringComparer.Ordinal)
    {
    };

    /// <summary>How the table writes a key whose <see cref="GestureHelper.Format"/> name is the enum name.</summary>
    private static readonly Dictionary<string, string> DocSpelling = new(StringComparer.Ordinal)
    {
        ["Oem3"] = "`", // Key.OemTilde, which shares its value with Oem3 and so formats as Oem3
    };

    // Every call that adds to a registry: _registry.Register(, registry.Register(, _commandRegistry.Register(.
    private static readonly Regex RegisterCall = new(@"\.Register\(", RegexOptions.Compiled);

    // What a call must pass for these tests to read it: new CommandDefinition(, new Models.CommandDefinition(,
    // or a target-typed new(.
    private static readonly Regex DefinitionStart =
        new(@"\Gnew\s*(?:(?:\w+\.)*CommandDefinition\s*)?\(", RegexOptions.Compiled);

    // How run-time ids are built: var commandId = $"theme.apply.{theme.Id}";
    private static readonly Regex RunTimeIdFamily =
        new(@"commandId\s*=\s*\$""([\w.]+)\{", RegexOptions.Compiled);

    // A table row with an id: | key | `id` | title |
    private static readonly Regex IdRow =
        new(@"^\|\s*([^|\r\n]*?)\s*\|\s*`([^`]+)`\s*\|", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void EveryMainWindowCommand_HasARowInTheShortcutTable()
    {
        var idColumn = TableRows().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var missing = Scan().LiteralIds
            .Where(id => !idColumn.Contains(id) && !Undocumented.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These commands are registered by MainViewModel or MainWindow but have no row of their own " +
            $"(an id in the Command ID column) in the '{TableHeading}' table of docs/KEYBOARD-SHORTCUTS.md. " +
            "Add a row (key, `id`, title) for each, or list it in Undocumented with the reason: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void EveryIdInTheShortcutTable_IsRegistered()
    {
        var scan = Scan();
        var familyRows = scan.Families.Select(f => f + "{id}").ToHashSet(StringComparer.Ordinal);

        var stale = TableRows().Select(r => r.Id)
            .Where(id => !scan.LiteralIds.Contains(id) && !familyRows.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These ids have a row in docs/KEYBOARD-SHORTCUTS.md, but nothing in MainViewModel or " +
            "MainWindow registers them. Fix the id or remove the row: " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryRunTimeIdFamily_HasARowInTheShortcutTable()
    {
        var scan = Scan();

        // Each registration whose id is not a literal must come from a family this test can see,
        // or a new kind of run-time id could slip in with no row.
        Assert.Equal(scan.Families.Count, scan.Registrations.Count(r => r.Id is null));

        var idColumn = TableRows().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var family in scan.Families)
            Assert.True(idColumn.Contains(family + "{id}"),
                $"Commands with ids '{family}…' are registered at run time; docs/KEYBOARD-SHORTCUTS.md " +
                $"needs one row for the family, with `{family}{{id}}` as its id.");
    }

    [Fact]
    public void EveryRowsKey_IsTheCommandsDefaultKey()
    {
        var gestures = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in Scan().Registrations.Where(r => r.Id is not null))
            gestures[r.Id!] = r.Gesture; // MainWindow's replacement of a VM command comes last, as at run time

        var wrong = new List<string>();
        foreach (var row in TableRows())
        {
            if (!gestures.TryGetValue(row.Id, out var gesture)) continue; // a family row, or stale (tested above)
            if (gesture is null) continue;                               // default key computed at run time

            if (row.Key == "*(unassigned)*")
            {
                if (gesture.Length > 0)
                    wrong.Add($"{row.Id}: the table says unassigned, the code's default is {gesture}");
                continue;
            }
            if (row.Key.StartsWith('*')) continue; // a described key, such as a saved view's own hotkey

            var alternatives = row.Key.Split(" / ").Select(k => k.Trim());
            if (gesture.Length == 0)
                wrong.Add($"{row.Id}: the table says {row.Key}, the code has no default key");
            else if (!alternatives.Contains(gesture, StringComparer.Ordinal))
                wrong.Add($"{row.Id}: the table says {row.Key}, the code's default is {gesture}");
        }

        Assert.True(wrong.Count == 0,
            "These rows of docs/KEYBOARD-SHORTCUTS.md give a key that is not the command's default key " +
            "(write *(unassigned)* for a command with none):\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void EveryRegistration_IsReadByTheseTests()
    {
        var scan = Scan();

        // A registration written another way (a definition built elsewhere and passed in, say) would be
        // invisible to the checks above, and parsing that stopped matching would make them pass on nothing.
        Assert.True(scan.Unreadable.Count == 0,
            "These Register calls in MainViewModel or MainWindow could not be read by this test. Pass " +
            "new CommandDefinition(id: \"…\", …) inline, or teach the test the new shape: " +
            string.Join(", ", scan.Unreadable));
        Assert.True(scan.LiteralIds.Count > 100,
            $"Only {scan.LiteralIds.Count} command ids were found; the source parsing is broken.");
    }

    [Fact]
    public void UndocumentedAllowlist_NamesOnlyRegisteredIds()
    {
        var registered = Scan().LiteralIds;
        foreach (var (id, reason) in Undocumented)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"Undocumented entry '{id}' needs a reason.");
            Assert.True(registered.Contains(id),
                $"Undocumented lists '{id}', which is no longer registered; remove the entry.");
        }
    }

    /// <param name="Id">The literal id, or null when the id is computed at run time.</param>
    /// <param name="Gesture">The default key as the table writes it; empty for none; null when computed.</param>
    private sealed record Registration(string? Id, string? Gesture);

    private sealed record RegistrationScan(
        List<Registration> Registrations, List<string> Unreadable, HashSet<string> Families)
    {
        public HashSet<string> LiteralIds { get; } =
            Registrations.Where(r => r.Id is not null).Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);
    }

    private sealed record TableRow(string Key, string Id);

    private static RegistrationScan Scan()
    {
        var registrations = new List<Registration>();
        var unreadable = new List<string>();
        var families = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in Sources())
        {
            var text = File.ReadAllText(source);
            foreach (Match family in RunTimeIdFamily.Matches(text))
                families.Add(family.Groups[1].Value);

            foreach (Match call in RegisterCall.Matches(text))
            {
                var lineStart = text.LastIndexOf('\n', call.Index) + 1;
                if (text[lineStart..call.Index].Contains("//", StringComparison.Ordinal)) continue; // a comment

                var where = $"{Path.GetFileName(source)}:{text.AsSpan(0, call.Index).Count('\n') + 1}";
                var start = call.Index + call.Length;
                while (start < text.Length && char.IsWhiteSpace(text[start])) start++;

                var definition = DefinitionStart.Match(text, start);
                var args = definition.Success ? Arguments(text, definition.Index + definition.Length) : null;
                var registration = args is null ? null : Read(args);
                if (registration is null) unreadable.Add(where);
                else registrations.Add(registration);
            }
        }

        return new RegistrationScan(registrations, unreadable, families);
    }

    /// <summary>The argument list of a call whose opening parenthesis ends just before <paramref name="from"/>.</summary>
    private static string? Arguments(string text, int from)
    {
        var depth = 1;
        for (var i = from; i < text.Length; i++)
        {
            switch (text[i])
            {
                // Comments inside the call ("a shared mailbox's message") must not open a char literal.
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                    i = text.IndexOf('\n', i);
                    if (i < 0) return null;
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '*':
                    i = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (i < 0) return null;
                    i++;
                    break;
                case '"':
                case '\'':
                    var quote = text[i];
                    for (i++; i < text.Length && text[i] != quote; i++)
                        if (text[i] == '\\') i++;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0) return text[from..i];
                    break;
            }
        }
        return null;
    }

    private static Registration? Read(string args)
    {
        string? id;
        var literal = Regex.Match(args, @"^\s*(?:id:\s*)?""([^""]+)""");
        if (literal.Success)
            id = literal.Groups[1].Value;
        else if (Regex.IsMatch(args, @"^\s*id:\s*[^\s"",][^,]*,"))
            id = null;
        else
            return null;

        var keyArg = Regex.Match(args, @"defaultKey:\s*([^,\)]+)");
        if (!keyArg.Success) return new Registration(id, string.Empty);

        var key = Regex.Match(keyArg.Groups[1].Value, @"^Key\.(\w+)\s*$");
        if (!key.Success) return new Registration(id, null); // e.g. defaultKey: defaultKey, parsed at run time

        var gesture = GestureHelper.Format(Enum.Parse<Key>(key.Groups[1].Value), ParseModifiers(args));
        foreach (var (name, spelling) in DocSpelling)
            if (gesture == name || gesture.EndsWith("+" + name, StringComparison.Ordinal))
                gesture = gesture[..^name.Length] + spelling;
        return new Registration(id, gesture);
    }

    // As ShortcutExposureTests.ParseModifiers: defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift
    private static ModifierKeys ParseModifiers(string args)
    {
        var m = Regex.Match(args, @"defaultModifiers:\s*([^,\)]+)");
        if (!m.Success) return ModifierKeys.None;
        var mods = ModifierKeys.None;
        foreach (Match part in Regex.Matches(m.Groups[1].Value, @"ModifierKeys\.(\w+)"))
            mods |= Enum.Parse<ModifierKeys>(part.Groups[1].Value);
        return mods;
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

    /// <summary>The table's rows that name an id in the Command ID column.</summary>
    private static List<TableRow> TableRows()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "KEYBOARD-SHORTCUTS.md"));
        var start = doc.IndexOf(TableHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"docs/KEYBOARD-SHORTCUTS.md has no '{TableHeading}' section.");
        var end = doc.IndexOf("\n## ", start + TableHeading.Length, StringComparison.Ordinal);
        var section = end < 0 ? doc[start..] : doc[start..end];

        return IdRow.Matches(section)
                    .Select(m => new TableRow(m.Groups[1].Value.Trim(), m.Groups[2].Value))
                    .ToList();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }
}
