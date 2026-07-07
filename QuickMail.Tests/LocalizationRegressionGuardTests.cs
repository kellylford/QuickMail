using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Regression guards: no new hardcoded, untranslated user-facing strings in view XAML
/// or in the two code-behind sinks that surface text to the user (MessageBox.Show,
/// AccessibilityHelper.Announce). Every UI string must come from Resources/Strings.resx
/// via {x:Static strings:Strings.*} (XAML) or Strings.*/StringsHelper.Count (C#) so the
/// es/de/fr satellite catalogs stay complete. See docs/LOCALIZATION.md.
/// </summary>
public class LocalizationRegressionGuardTests
{
    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        return null;
    }

    // ── XAML: user-facing text attributes ───────────────────────────────────────

    /// <summary>
    /// Matches a literal value assigned to a known user-facing text attribute — not a markup
    /// extension ({Binding}, {x:Static}, ...), except the literal-text escape prefix "{}" (still
    /// hardcoded and must be caught). Whether the captured value is actually translatable text
    /// (vs. a pure glyph/XML entity) is decided separately by <see cref="HasTranslatableText"/>.
    /// </summary>
    private static readonly Regex UiTextAttribute = new(
        @"\b(?:Content|Text|Header|ToolTip|Title|AutomationProperties\.Name|AutomationProperties\.HelpText)=""(?<value>\{\}[^""]*|(?!\{)[^""]*)""",
        RegexOptions.Compiled);

    // XML character/entity references (&lt; &#x1F4CE; ...) are how XAML source spells a single
    // punctuation glyph or emoji — the letters inside the reference name are not translatable text.
    private static readonly Regex XmlCharRef = new(@"&(?:#x[0-9A-Fa-f]+|#[0-9]+|[a-zA-Z]+);", RegexOptions.Compiled);

    private static bool HasTranslatableText(string value) =>
        XmlCharRef.Replace(value, "").Any(char.IsLetter);

    /// <summary>Deliberate exceptions, reviewed case by case. Keep this list short.</summary>
    private static readonly Dictionary<string, string[]> XamlAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Universal single-letter/abbreviation rich-text toolbar glyphs (Bold/Italic/Underline/
        // Strikethrough/Heading 1-3) — conventionally shown the same way in every locale (Word,
        // Google Docs, etc.); the screen-reader-facing AutomationProperties.Name is localized.
        ["ComposeWindow.xaml"] = ["Content=\"B\"", "Content=\"I\"", "Text=\"U\"", "Text=\"S\"",
            "Content=\"H1\"", "Content=\"H2\"", "Content=\"H3\""],
    };

    private static IEnumerable<string> ViewXamlFiles(string root)
    {
        var appXaml = Path.Combine(root, "QuickMail", "App.xaml");
        if (File.Exists(appXaml)) yield return appXaml;

        foreach (var sub in new[] { "Views", "Controls", "Styles" })
        {
            var dir = Path.Combine(root, "QuickMail", sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.xaml"))
                yield return file;
        }
    }

    private static bool IsXamlAllowed(string fileName, string offendingText) =>
        XamlAllowlist.TryGetValue(fileName, out var allowed)
        && allowed.Any(a => offendingText.Contains(a, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ViewXaml_HasNoHardcodedUserFacingStrings()
    {
        var root = FindRepoRoot();
        Assert.False(root is null, "Repo source tree not found from test base directory.");

        var violations = new List<string>();
        foreach (var file in ViewXamlFiles(root!))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in UiTextAttribute.Matches(lines[i]))
                {
                    var value = m.Groups["value"].Value;
                    if (!HasTranslatableText(value)) continue;
                    if (IsXamlAllowed(name, m.Value) || IsXamlAllowed(name, value)) continue;
                    violations.Add($"{name}:{i + 1}: {m.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Hardcoded user-facing text in view XAML — add a key to Resources/Strings.resx "
            + "(+ es/de/fr) and reference it via {x:Static strings:Strings.*} instead "
            + "(or extend the reviewed allowlist if this one is deliberate):\n"
            + string.Join("\n", violations));
    }

    // ── C#: MessageBox.Show / AccessibilityHelper.Announce literal text ─────────

    private static readonly string[] TextSinks = ["MessageBox.Show(", "AccessibilityHelper.Announce("];

    // StringsHelper.Count's first argument is a resx *key* (e.g. "MainWindow_Announce_MessagesSelected"),
    // not display text — it's the correct localized pattern, so literals passed there are not violations.
    private static readonly Regex PrecedingResourceKeyCall = new(@"StringsHelper\.Count\(\s*$", RegexOptions.Compiled);

    private static readonly Regex EscapeSequence = new(@"\\.", RegexOptions.Compiled);

    /// <summary>Deliberate exceptions, reviewed case by case. Keep this list short.</summary>
    private static readonly Dictionary<string, (int start, int end)[]> CodeAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // App.xaml.cs: three startup/crash paths that run before ApplyUiCulture (culture is
        // resolved from config.ini at App.xaml.cs:135, well after these fire) or must not
        // depend on the resource system that might itself be implicated in the failure.
        ["App.xaml.cs"] =
        [
            (33, 51),   // --help / -h / /? CLI usage text — shown before any config/culture is loaded.
            (294, 301), // ShowProfileError — invalid --profileDir argument, before config/culture load.
            (303, 321), // OnDispatcherUnhandledException — global crash handler; must stay self-contained.
        ],
    };

    private static bool IsCodeAllowed(string fileName, int line) =>
        CodeAllowlist.TryGetValue(fileName, out var ranges)
        && ranges.Any(r => line >= r.start && line <= r.end);

    private static IEnumerable<string> AppSourceFiles(string root)
    {
        var dir = Path.Combine(root, "QuickMail");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    /// <summary>
    /// Finds every quoted string literal that lies within the balanced-paren argument list of
    /// each occurrence of <paramref name="sink"/> in <paramref name="text"/>, and returns the
    /// ones that contain real letters (after stripping backslash escapes) — i.e. plausible
    /// hardcoded display text rather than a resource key, punctuation, or a pure escape sequence.
    /// Nested calls (e.g. string.Format(...), StringsHelper.Count(...)) are walked into, since a
    /// literal concatenated or nested one level down is just as hardcoded as a direct argument.
    /// Limitation: does not special-case string literals nested inside a $"...{ }..." interpolation
    /// hole — none of the current call sites do this, so it is not handled.
    /// </summary>
    private static IEnumerable<(int line, string literal)> FindLiteralSinkArgs(string text, string sink)
    {
        int searchFrom = 0;
        while (true)
        {
            int idx = text.IndexOf(sink, searchFrom, StringComparison.Ordinal);
            if (idx < 0) yield break;

            int i = idx + sink.Length;
            int depth = 1;
            var literalSpans = new List<(int start, int end, bool verbatim)>();

            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (c == '(') { depth++; i++; }
                else if (c == ')') { depth--; i++; }
                else if (c == '"')
                {
                    bool verbatim = i > 0 && text[i - 1] == '@';
                    int litStart = i;
                    i++;
                    if (verbatim)
                    {
                        while (i < text.Length)
                        {
                            if (text[i] == '"')
                            {
                                if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                                i++; break;
                            }
                            i++;
                        }
                    }
                    else
                    {
                        while (i < text.Length)
                        {
                            if (text[i] == '\\') { i += 2; continue; }
                            if (text[i] == '"') { i++; break; }
                            if (text[i] == '\n') break; // unterminated on this line — bail
                            i++;
                        }
                    }
                    literalSpans.Add((litStart, i, verbatim));
                }
                else i++;
            }

            foreach (var (start, end, verbatim) in literalSpans)
            {
                var raw = text.Substring(start + 1, Math.Max(0, end - start - 2));
                var stripped = verbatim ? raw.Replace("\"\"", "\"") : EscapeSequence.Replace(raw, "");
                if (!stripped.Any(char.IsLetter)) continue;

                var prefix = text.Substring(Math.Max(0, start - 60), Math.Min(60, start));
                if (PrecedingResourceKeyCall.IsMatch(prefix)) continue; // resource key, not display text

                int line = text.Take(start).Count(ch => ch == '\n') + 1;
                yield return (line, stripped);
            }

            searchFrom = Math.Max(i, idx + sink.Length);
        }
    }

    [Fact]
    public void CodeBehind_HasNoHardcodedMessageBoxOrAnnounceText()
    {
        var root = FindRepoRoot();
        Assert.False(root is null, "Repo source tree not found from test base directory.");

        var violations = new List<string>();
        foreach (var file in AppSourceFiles(root!))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            foreach (var sink in TextSinks)
            {
                foreach (var (line, literal) in FindLiteralSinkArgs(text, sink))
                {
                    if (IsCodeAllowed(name, line)) continue;
                    violations.Add($"{name}:{line}: {sink} literal \"{literal}\"");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Hardcoded literal text passed to MessageBox.Show/AccessibilityHelper.Announce — "
            + "add a key to Resources/Strings.resx (+ es/de/fr) and reference it via Strings.* "
            + "or StringsHelper.Count instead (or extend the reviewed allowlist if this one is "
            + "deliberate, e.g. a pre-culture-resolution startup path):\n"
            + string.Join("\n", violations));
    }
}
