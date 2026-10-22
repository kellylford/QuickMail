using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using QuickMail.Helpers;
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

    /// <summary>
    /// The key each control showed before #849, written out by hand then and now looked up through its
    /// id. Catches an id that names the wrong command, such as Reply and Forward swapped, which
    /// <see cref="EveryShortcutIdIsACommandThatWindowRegisters"/> cannot. Keyed by the accessible name,
    /// else the header, content or x:Name.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> KeysShownBefore = new()
    {
        ["MainWindow.xaml"] = new()
        {
            // Given a key by #849 that they did not show before.
            ["Delete"] = "Delete", ["Delete Folder"] = "Delete", ["View mode"] = "Ctrl+Shift+V",
            ["Close search results"] = "Escape", ["Change Search"] = "Ctrl+/", ["RulesStatusButton"] = "Ctrl+Shift+L",
            ["Save As"] = "F12",
            ["Print"] = "Ctrl+P",
            ["Move to Archive"] = "Ctrl+Shift+M",
            ["Create Rule from Message"] = "Ctrl+Shift+T",
            ["_New Message"] = "Ctrl+N",
            ["_Save"] = "Ctrl+S",
            ["Save _As…"] = "F12",
            ["_Print…"] = "Ctrl+P",
            ["S_ettings…"] = "Ctrl+,",
            ["_Reply"] = "Ctrl+R",
            ["Reply _All"] = "Ctrl+Shift+R",
            ["_Forward"] = "Ctrl+Shift+F",
            ["_Delete"] = "Delete",
            ["Move to Arc_hive"] = "Ctrl+Shift+M",
            ["Empty _Trash"] = "Ctrl+Shift+E",
            ["_Watch Conversation"] = "Ctrl+Shift+W",
            ["_Grab Addresses from Message"] = "Ctrl+Shift+G",
            ["_Refresh"] = "F5",
            ["_Plain Text View"] = "Ctrl+Shift+H",
            ["_Load Pictures"] = "Ctrl+Shift+U",
            ["_Search Folders…"] = "Ctrl+Shift+Y",
            ["_Advanced Search…"] = "Ctrl+/",
            ["Address _Book…"] = "Ctrl+Shift+B",
            ["Manage Rules"] = "Ctrl+Shift+L",
            ["User _Guide"] = "F1",
            ["New Message"] = "Ctrl+N",
            ["Reply"] = "Ctrl+R",
            ["Reply All"] = "Ctrl+Shift+R",
            ["Forward"] = "Ctrl+Shift+F",
            ["Delete message"] = "Delete",
            ["Refresh"] = "F5",
            ["Empty Trash"] = "Ctrl+Shift+E",
            ["View User Guide"] = "F1",
            ["Move Up"] = "Alt+Up",
            ["Move Down"] = "Alt+Down",
            ["Move to Start"] = "Alt+Home",
            ["Move to End"] = "Alt+End",
        },
        ["MessageWindow.xaml"] = new()
        {
            ["Previous message"] = "Alt+Left", ["Next message"] = "Alt+Right",
            ["Save"] = "Ctrl+S",
            ["Save As"] = "F12",
            ["Print"] = "Ctrl+P",
            ["Close Window"] = "Ctrl+W",
            ["Reply"] = "Ctrl+R",
            ["Reply All"] = "Ctrl+Shift+R",
            ["Forward"] = "Ctrl+Shift+F",
            ["Delete"] = "Delete",
            ["Mark as Read"] = "Ctrl+Q",
            ["Grab Addresses from Message"] = "Ctrl+Shift+G",
            ["Create Rule from Message"] = "Ctrl+Shift+T",
            ["Previous Message"] = "Alt+Left",
            ["Next Message"] = "Alt+Right",
            ["Manage Rules"] = "Ctrl+Shift+L",
        },
        ["ComposeWindow.xaml"] = new()
        {
            ["Cancel and close compose window"] = "Escape", ["Message body"] = "Alt+Y",
            ["_Send"] = "Alt+S",
            ["Save _Draft"] = "Ctrl+S",
            ["_Add Attachments…"] = "Ctrl+Shift+A",
            ["_Close"] = "Escape",
            ["_Plain Text"] = "Ctrl+Shift+1",
            ["_Markdown"] = "Ctrl+Shift+2",
            ["_HTML"] = "Ctrl+Shift+3",
            ["Open preview"] = "F8",
            ["_Bold"] = "Ctrl+B",
            ["_Italic"] = "Ctrl+I",
            ["_Underline"] = "Ctrl+U",
            ["_Strikethrough"] = "Ctrl+Shift+X",
            ["_Normal Text"] = "Ctrl+Alt+0",
            ["Heading _1"] = "Ctrl+Alt+1",
            ["Heading _2"] = "Ctrl+Alt+2",
            ["Heading _3"] = "Ctrl+Alt+3",
            ["Heading _4"] = "Ctrl+Alt+4",
            ["Heading _5"] = "Ctrl+Alt+5",
            ["Heading _6"] = "Ctrl+Alt+6",
            ["_Quote"] = "Ctrl+Shift+9",
            ["Bu_llet List"] = "Ctrl+Shift+L",
            ["_Numbered List"] = "Ctrl+Shift+N",
            ["_Clear Formatting"] = "Ctrl+Space",
            ["_Announce Formatting"] = "Ctrl+T",
            ["Sho_w Formatting…"] = "Ctrl+Shift+T",
            ["_Link…"] = "Ctrl+K",
            ["_Image…"] = "Ctrl+Shift+I",
            ["Image _Properties…"] = "Alt+Enter",
            ["Check _Spelling…"] = "F7",
            ["_Next Misspelling"] = "Ctrl+F7",
            ["Pre_vious Misspelling"] = "Ctrl+Shift+F7",
            ["_Address Book…"] = "Ctrl+Shift+B",
            ["Chec_k Addresses"] = "Ctrl+K",
            ["Bold"] = "Ctrl+B",
            ["Italic"] = "Ctrl+I",
            ["Underline"] = "Ctrl+U",
            ["Strikethrough"] = "Ctrl+Shift+X",
            ["Bullet list"] = "Ctrl+Shift+L",
            ["Numbered list"] = "Ctrl+Shift+N",
            ["Insert link"] = "Ctrl+K",
            ["Insert image"] = "Ctrl+Shift+I",
            ["Clear formatting"] = "Ctrl+Space",
            ["Send message"] = "Alt+S",
            ["Save as draft"] = "Ctrl+S",
            ["Add file attachments"] = "Ctrl+Shift+A",
        },
    };

    [Theory]
    [MemberData(nameof(WindowFiles))]
    public void EachControlNamesTheCommandWhoseKeyItShowed(string xamlFile)
    {
        var defaults = RegisteredDefaults(Windows[xamlFile]);
        var expected = KeysShownBefore[xamlFile];
        var problems = new List<string>();
        foreach (var e in Elements(xamlFile))
        {
            var id = e.Attribute(XName.Get("CommandShortcut.Id", HelpersNs))?.Value;
            if (id is null) continue;

            var label = Label(e);
            defaults.TryGetValue(id, out var actual);
            if (!expected.TryGetValue(label, out var key))
            {
                // A command with no default key shows nothing until the user assigns one; which
                // command it is, MenuItemsNamedLikeACommandNameThatCommand pins.
                if (actual is not null)
                    problems.Add($"{label} ({id}) is not in the table; add the key it should show.");
            }
            else if (actual != key)
                problems.Add($"{label} names {id}, whose default is {actual ?? "none"}, but it showed {key}.");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Menu items that are not commands by the same name: each would show a key that does
    /// something else. Keyed by the item's label as <see cref="Label"/> reads it.
    /// </summary>
    private static readonly HashSet<string> NotTheCommandOfTheSameName =
    [
        "_Save…", // the attachment lists' Save saves the attachment, not the message (mail.save)
    ];

    /// <summary>
    /// A menu item named like a registered command must name that command, or a key the user
    /// assigns to it in Settings is never shown or reported there (#854: Calendar at Bottom of
    /// Folder List, whose command has no default key, so #849 never gave its item an id).
    /// </summary>
    [Theory]
    [MemberData(nameof(WindowFiles))]
    public void MenuItemsNamedLikeACommandNameThatCommand(string xamlFile)
    {
        var titles = RegisteredTitles(Windows[xamlFile]);
        var problems = new List<string>();
        foreach (var e in Elements(xamlFile).Where(e => e.Name.LocalName == "MenuItem"))
        {
            var label = Label(e);
            if (NotTheCommandOfTheSameName.Contains(label)) continue;
            if (!titles.TryGetValue(Normalize(label), out var id)) continue;

            var named = e.Attribute(XName.Get("CommandShortcut.Id", HelpersNs))?.Value;
            if (named != id)
                problems.Add($"{label} runs {id} but names {named ?? "no command"}.");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // "Move to _Folder…" and the title "Move to Folder…" compare equal.
    private static string Normalize(string label) =>
        label.Replace("_", "").TrimEnd('…', '.', ' ').ToLowerInvariant();

    private static Dictionary<string, string> RegisteredTitles(IEnumerable<string> sources)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", source));
            foreach (Match m in Regex.Matches(text, @"new CommandDefinition\((.*?)\)\);", RegexOptions.Singleline))
            {
                var id = Regex.Match(m.Groups[1].Value, @"id:\s*""([^""]+)""");
                var title = Regex.Match(m.Groups[1].Value, @"title:\s*""([^""]+)""");
                if (id.Success && title.Success)
                    titles[Normalize(title.Groups[1].Value)] = id.Groups[1].Value;
            }
        }
        return titles;
    }

    private static string Label(XElement e) =>
        new[]
        {
            e.Attribute("AutomationProperties.Name"), e.Attribute("Header"), e.Attribute("Content"),
            e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")),
        }
        .Select(a => a?.Value)
        .FirstOrDefault(v => v is not null && !v.StartsWith('{')) ?? "?"; // skip {Binding …}

    /// <summary>Each registered id's default key as the app formats it, read from the registrations.</summary>
    private static Dictionary<string, string> RegisteredDefaults(IEnumerable<string> sources)
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", source));
            foreach (Match m in Regex.Matches(text, @"new CommandDefinition\((.*?)\)\);", RegexOptions.Singleline))
            {
                var args = m.Groups[1].Value;
                var id = Regex.Match(args, @"id:\s*""([^""]+)""");
                var key = Regex.Match(args, @"defaultKey:\s*Key\.(\w+)");
                if (!id.Success || !key.Success) continue;
                defaults[id.Groups[1].Value] = GestureHelper.Format(
                    Enum.Parse<Key>(key.Groups[1].Value), ParseModifiers(args));
            }
        }
        // Compose registers its headings in a loop from an array of keys: Ctrl+Alt+1 to 6.
        for (var level = 1; level <= 6; level++)
            defaults.TryAdd($"compose.heading{level}", $"Ctrl+Alt+{level}");
        return defaults;
    }

    private static ModifierKeys ParseModifiers(string args)
    {
        var m = Regex.Match(args, @"defaultModifiers:\s*([^,\)]+)");
        if (!m.Success) return ModifierKeys.None;
        var mods = ModifierKeys.None;
        foreach (Match part in Regex.Matches(m.Groups[1].Value, @"ModifierKeys\.(\w+)"))
            mods |= Enum.Parse<ModifierKeys>(part.Groups[1].Value);
        return mods;
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
