using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Guards #849 across the three windows that show shortcuts. A shortcut is shown and spoken only
/// through <c>sc:CommandShortcut.Id</c>, which reads it from the registry, so a rebound key
/// can't leave a menu, tooltip or screen reader announcing the old one. Read from the XAML source,
/// so every menu item and button is covered, including those in submenus and context menus that a
/// loaded window never builds.
/// </summary>
public class ShortcutExposureTests
{
    private const string HelpersNs = "clr-namespace:QuickMail.Helpers";

    /// <summary>Which source files register the commands each window's registry holds.</summary>
    private static readonly Dictionary<string, string[]> Windows = new()
    {
        ["MainWindow.xaml"]    = ["Views/MainWindow.xaml.cs", "ViewModels/MainViewModel.cs"],
        ["MessageWindow.xaml"] = ["Views/MessageWindow.xaml.cs"],
        ["ComposeWindow.xaml"] = ["Views/ComposeWindow.xaml.cs"],
    };

    /// <summary>
    /// Shortcuts that are not registry commands, so they stay literal. Each still has to be given
    /// as <c>AutomationProperties.AcceleratorKey</c> too, or a screen reader is not told it.
    /// </summary>
    private static readonly Dictionary<string, string> FixedAcceleratorKeys = new()
    {
        // Opens the palette itself, so it can't be dispatched through the registry (CLAUDE.md).
        ["Ctrl+Shift+P"] = "command palette",
        // WPF's own editing commands on the compose Edit menu; WPF draws these keys itself.
        ["Ctrl+Z"] = "Undo", ["Ctrl+Y"] = "Redo", ["Ctrl+X"] = "Cut",
        ["Ctrl+C"] = "Copy", ["Ctrl+V"] = "Paste", ["Ctrl+A"] = "Select All",
        // The compose From and Subject boxes: their labels' access keys.
        ["Alt+M"] = "From label", ["Alt+U"] = "Subject label",
    };

    /// <summary>Tooltips allowed to spell out a key because no single command owns it.</summary>
    private static readonly string[] ToolTipsNamingSeveralOrInControlKeys =
    [
        "Paragraph style (Ctrl+Alt+0 to 6, Ctrl+Shift+9)", // eight commands, one box
        "Clear search (Escape)",                           // Escape inside the search box
    ];

    private static readonly Regex KeyInParentheses =
        new(@"\((Ctrl|Alt|Shift|Win|F\d+|Del|Delete|Esc|Escape|Enter)\b", RegexOptions.Compiled);

    public static TheoryData<string> WindowFiles() => new(Windows.Keys);

    [Theory]
    [MemberData(nameof(WindowFiles))]
    public void EveryShortcutIdIsACommandThatWindowRegisters(string xamlFile)
    {
        var registered = RegisteredIds(Windows[xamlFile]);
        var unknown = Elements(xamlFile)
            .Select(e => e.Attribute(XName.Get("CommandShortcut.Id", HelpersNs))?.Value)
            .Where(id => id is not null && !registered.Contains(id))
            .ToList();

        Assert.True(unknown.Count == 0,
            $"{xamlFile} names commands its window never registers: {string.Join(", ", unknown)}");
    }

    [Theory]
    [MemberData(nameof(WindowFiles))]
    public void NoShortcutIsWrittenOutByHandForARegisteredCommand(string xamlFile)
    {
        var problems = new List<string>();
        foreach (var e in Elements(xamlFile))
        {
            var gesture = e.Attribute("InputGestureText")?.Value;
            var accel = e.Attribute("AutomationProperties.AcceleratorKey")?.Value;
            var where = $"{e.Name.LocalName} '{e.Attribute("Header")?.Value ?? e.Attribute("AutomationProperties.Name")?.Value}'";

            if (gesture is not null && gesture != accel)
                problems.Add($"{where} shows {gesture} but a screen reader is told '{accel}'. Use sc:CommandShortcut.Id.");
            if (accel is not null && !FixedAcceleratorKeys.ContainsKey(accel))
                problems.Add($"{where} has a hand-written AcceleratorKey {accel}. Use sc:CommandShortcut.Id.");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(WindowFiles))]
    public void NoTooltipSpellsOutAKey(string xamlFile)
    {
        // A literal "(Ctrl+N)" goes stale on rebind and is spoken as help text on top of the
        // accelerator. sc:CommandShortcut.ToolTip builds it from the registry while it is open.
        var stale = Elements(xamlFile)
            .Select(e => e.Attribute("ToolTip")?.Value)
            .Where(t => t is not null && KeyInParentheses.IsMatch(t)
                        && !ToolTipsNamingSeveralOrInControlKeys.Contains(t))
            .ToList();

        Assert.True(stale.Count == 0, $"{xamlFile}: {string.Join(" | ", stale)}");
    }

    [Theory]
    [MemberData(nameof(WindowFiles))]
    public void AShortcutTooltipAlwaysHasItsCommand(string xamlFile)
    {
        var orphans = Elements(xamlFile)
            .Where(e => e.Attribute(XName.Get("CommandShortcut.ToolTip", HelpersNs)) is not null
                        && e.Attribute(XName.Get("CommandShortcut.Id", HelpersNs)) is null)
            .Select(e => e.Attribute(XName.Get("CommandShortcut.ToolTip", HelpersNs))!.Value)
            .ToList();

        Assert.True(orphans.Count == 0, $"{xamlFile}: tooltip without a command: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void TheGuardSeesTheIdsItChecks()
    {
        // A namespace typo would make every check above pass on nothing.
        Assert.Contains(Elements("MainWindow.xaml"),
            e => e.Attribute(XName.Get("CommandShortcut.Id", HelpersNs))?.Value == "mail.reply");
        Assert.Contains("compose.heading3", RegisteredIds(Windows["ComposeWindow.xaml"]));
        Assert.Contains("settings.open", RegisteredIds(Windows["MainWindow.xaml"]));
    }

    private static IEnumerable<XElement> Elements(string xamlFile) =>
        XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "Views", xamlFile)).Descendants();

    private static HashSet<string> RegisteredIds(IEnumerable<string> sources)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", source));
            foreach (Match m in Regex.Matches(text, @"id:\s*""([^""]+)"""))
                ids.Add(m.Groups[1].Value);
            // Compose registers its six heading commands in a loop: id: $"compose.heading{level}".
            if (Regex.IsMatch(text, @"id:\s*\$""compose\.heading\{level\}"""))
                for (var level = 1; level <= 6; level++) ids.Add($"compose.heading{level}");
        }
        return ids;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }
}
