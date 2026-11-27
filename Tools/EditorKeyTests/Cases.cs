namespace EditorKeyTests;

/// <summary>One line of a case: something to do, or something that must be true.</summary>
internal sealed record Step(string Verb, string Argument, int Line);

/// <summary>A named sequence of steps, run once in each of its modes.</summary>
internal sealed class Case
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public List<string> Modes { get; } = new() { "plain", "markdown", "html" };
    public List<Step> Steps { get; } = new();

    /// <summary>A question for Kelly about what the right behavior is; shown in the report.</summary>
    public string? Judgment { get; set; }

    /// <summary>QuickMail is restarted before this case: it tests the first moments of a window.</summary>
    public bool Fresh { get; set; }
}

/// <summary>
/// Reads the case files. The format is one step per line, <c>verb: argument</c>, under a
/// <c>== Name</c> heading. See <c>cases/README.md</c> for the verbs.
/// </summary>
internal static class CaseFile
{
    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal)
    {
        "modes", "judgment", "fresh",
        // Setting up
        "text", "markdown", "caret",
        // Doing
        "keys", "type", "wait", "window",
        // Checking
        "caret-is", "selection-is", "text-is", "mode-is", "front-is", "focus-is", "typing-works", "markdown-is",
    };

    public static List<Case> Load(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.keys", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { path };
        var cases = new List<Case>();
        foreach (var file in files)
        {
            Case? current = null;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("==", StringComparison.Ordinal))
                {
                    current = new Case { Name = line.TrimStart('=').Trim(), File = Path.GetFileName(file) };
                    cases.Add(current);
                    continue;
                }
                if (current == null) throw new FormatException($"{file}({i + 1}): a step before the first '== Name' line.");

                int colon = line.IndexOf(':');
                string verb = (colon < 0 ? line : line[..colon]).Trim();
                string argument = colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
                // Text whose leading or trailing spaces matter is written in double quotes.
                if (verb is "text" or "type" or "text-is" or "markdown" or "markdown-is"
                    && argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"')
                    argument = argument[1..^1];
                if (!Verbs.Contains(verb)) throw new FormatException($"{file}({i + 1}): unknown step '{verb}'.");

                switch (verb)
                {
                    case "modes":
                        current.Modes.Clear();
                        current.Modes.AddRange(argument.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                        foreach (var mode in current.Modes)
                            if (mode is not ("plain" or "markdown" or "html"))
                                throw new FormatException($"{file}({i + 1}): unknown mode '{mode}'.");
                        break;
                    case "judgment":
                        current.Judgment = argument;
                        break;
                    case "fresh":
                        current.Fresh = true;
                        break;
                    default:
                        current.Steps.Add(new Step(verb, argument, i + 1));
                        break;
                }
            }
        }
        return cases;
    }

    /// <summary>Text as written in a case file: \n for a line end, \t for a tab, \\ for a backslash.</summary>
    public static string Unescape(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                char next = text[++i];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', 'v' => '\v', _ => next });
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// A place in text, as a case file names it: <c>start</c>, <c>end</c>, <c>at 5</c>,
    /// <c>before "word"</c>, <c>after "word"</c>, or <c>select "words"</c>. Returns the offset
    /// and, for a selection, its length.
    /// </summary>
    public static (int Start, int Length) Place(string spec, string text)
    {
        spec = spec.Trim();
        if (spec == "start") return (0, 0);
        if (spec == "end") return (text.Length, 0);
        if (spec.StartsWith("at ", StringComparison.Ordinal))
            return (int.Parse(spec[3..], System.Globalization.CultureInfo.InvariantCulture), 0);

        int quote = spec.IndexOf('"');
        int lastQuote = spec.LastIndexOf('"');
        if (quote < 0 || lastQuote <= quote) throw new FormatException($"Cannot read the place '{spec}'.");
        string kind = spec[..quote].Trim();
        string target = Unescape(spec.Substring(quote + 1, lastQuote - quote - 1));
        int at = text.IndexOf(target, StringComparison.Ordinal);
        if (at < 0) throw new FormatException($"The text has no \"{target}\" to be {kind}. Text: {Show(text)}");
        return kind switch
        {
            "before" => (at, 0),
            "after" => (at + target.Length, 0),
            "select" => (at, target.Length),
            _ => throw new FormatException($"Cannot read the place '{spec}'."),
        };
    }

    /// <summary>Text on one line for a report: paragraph ends as ⏎, tabs as ⇥, line breaks inside a paragraph as ↓.</summary>
    public static string Show(string text) =>
        "\"" + text.Replace("\n", "⏎", StringComparison.Ordinal).Replace("\t", "⇥", StringComparison.Ordinal)
            .Replace("\v", "↓", StringComparison.Ordinal) + "\"";
}
