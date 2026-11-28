using System.Diagnostics;
using System.Text;

namespace EditorKeyTests;

/// <summary>
/// Runs the message editor's keyboard cases against a published QuickMail.
///
///   EditorKeyTests --exe path\QuickMail.exe --profile path\fixture-profile
///                  [--cases dir-or-file] [--report out.md] [--label "no screen reader"]
///                  [--filter text] [--modes plain,html] [--delay 60]
///
/// Exit code 0 when every case passed, 1 when any failed, 2 when the run could not start.
/// </summary>
internal static class Program
{
    private sealed record Result(Case Case, string Mode, List<string> Failures, double Seconds);

    private static string _exe = string.Empty;
    private static string _profileTemplate = string.Empty;
    private static App? _app;
    // Whether UI Automation's text for each editor ends with the mark that ends the document,
    // learned from that editor when its body is empty. Once known it is required: an editor
    // that reports "x" where it should report "x" and the mark is not passed over.
    private static readonly Dictionary<string, bool> _finalMark = new();
    private static readonly List<string> _warnings = new();

    // Off (--no-restart) only when hunting a fault: the app's log then covers the whole run.
    private static bool _restartAfterFailure = true;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string casesPath = Path.Combine(AppContext.BaseDirectory, "cases");
        string? reportPath = null, filter = null;
        string label = "no screen reader";
        string[]? onlyModes = null;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--exe": _exe = Path.GetFullPath(Next()); break;
                case "--profile": _profileTemplate = Path.GetFullPath(Next()); break;
                case "--cases": casesPath = Path.GetFullPath(Next()); break;
                case "--report": reportPath = Path.GetFullPath(Next()); break;
                case "--label": label = Next(); break;
                case "--filter": filter = Next(); break;
                case "--modes": onlyModes = Next().Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--hold": Keyboard.HoldMs = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--old-build": App.AllowEditorByName = true; break;
                case "--no-restart": _restartAfterFailure = false; break;
                case "--delay": Keyboard.KeyDelayMs = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"Unknown argument {args[i]}"); return 2;
            }
        }
        if (!File.Exists(_exe) || !Directory.Exists(_profileTemplate))
        {
            Console.Error.WriteLine("Usage: EditorKeyTests --exe <QuickMail.exe> --profile <fixture profile folder> [--cases <dir>] [--report <file.md>] [--label <text>] [--filter <text>] [--modes plain,markdown,html] [--delay <ms>]");
            return 2;
        }

        List<Case> cases;
        try
        {
            cases = CaseFile.Load(casesPath);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (filter != null)
            cases = cases.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                     || c.File.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"{cases.Count} cases from {casesPath}; label: {label}");

        var results = new List<Result>();
        int restarts = 0;
        try
        {
            StartApp();
            foreach (var testCase in cases)
            {
                foreach (var mode in testCase.Modes)
                {
                    if (onlyModes != null && !onlyModes.Contains(mode)) continue;
                    var watch = Stopwatch.StartNew();
                    var failures = new List<string>();
                    try
                    {
                        if (testCase.Fresh) StartApp();
                        RunCase(testCase, mode, failures);
                    }
#pragma warning disable CA1031 // a case that throws is a failed case, whatever it threw; the run goes on
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        failures.Add($"stopped: {ex.GetType().Name}: {ex.Message}");
                    }
                    results.Add(new Result(testCase, mode, failures, watch.Elapsed.TotalSeconds));
                    Console.WriteLine($"{(failures.Count == 0 ? "PASS" : "FAIL")}  [{mode}] {testCase.Name}");
                    foreach (var failure in failures) Console.WriteLine($"        {failure}");

                    // After a failure the window may be in any state (a dialog open, focus
                    // gone). The next case starts from a fresh QuickMail, so one fault cannot
                    // pass for many, or hide them.
                    bool last = ReferenceEquals(testCase, cases[^1]) && mode == testCase.Modes.Last(m => onlyModes == null || onlyModes.Contains(m));
                    if (failures.Count > 0 && !last && _restartAfterFailure)
                    {
                        restarts++;
                        StartApp();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine($"The run could not go on: {ex.Message}");
            WriteReport(reportPath, label, results, restarts, aborted: ex.Message);
            return 2;
        }
        finally
        {
            _app?.Dispose();
        }

        WriteReport(reportPath, label, results, restarts, aborted: null);
        int failed = results.Count(r => r.Failures.Count > 0);
        Console.WriteLine($"{results.Count - failed} passed, {failed} failed, of {results.Count}.");
        return failed == 0 ? 0 : 1;
    }

    private static void StartApp()
    {
        _app?.Dispose();
        // A fresh copy of the profile each time: nothing a case did carries over.
        string profile = Path.Combine(Path.GetTempPath(), "qm-keytests-profile");
        if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        CopyDirectory(_profileTemplate, profile);
        _app = App.Start(_exe, profile);
        _app.OpenNewMessage();

        // What was learned about each editor's final mark is kept across restarts: it is a fact
        // about the control, and a case that starts in a new window may read the HTML editor
        // for the first time with text already in it.
        Read(); // learns the plain editor's final mark from its empty body
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    // ── Reading the editor ───────────────────────────────────────────────────

    private static EditorState Read()
    {
        var state = _app!.Read();
        string editor = _app.VisibleEditorId;
        if (!_finalMark.TryGetValue(editor, out bool mark))
        {
            // Only an empty body can say: its text is nothing, or the mark alone.
            if (state.Text.Length == 0) _finalMark[editor] = false;
            else if (state.Text == "\n") _finalMark[editor] = mark = true;
            else if (_finalMark.Count > 0)
            {
                // Read for the first time with text in it (a case that switches format with
                // text in the body, run before any case that starts in this editor). Both
                // editors are the same control, so what the other one taught is used; the
                // requirement below still applies from here on.
                _finalMark[editor] = mark = _finalMark.Values.First();
            }
            else throw new InvalidOperationException(
                $"The {editor} editor was first read with text in it ({CaseFile.Show(state.Text)}); it has to be read empty once, to learn how its text ends.");
        }
        if (!mark) return state;
        if (!state.Text.EndsWith('\n'))
            throw new InvalidOperationException($"The {editor} editor's text has lost the mark that ends it: {CaseFile.Show(state.Text)}.");
        string text = state.Text[..^1];
        int start = Math.Min(state.SelectionStart, text.Length);
        return new EditorState(text, start, Math.Min(state.SelectionLength, text.Length - start));
    }

    private static string CurrentMode()
    {
        string title = _app!.ComposeTitle;
        return title.Contains("- HTML -", StringComparison.Ordinal) ? "html"
            : title.Contains("- Markdown -", StringComparison.Ordinal) ? "markdown"
            : title.Contains("- Plain Text -", StringComparison.Ordinal) ? "plain"
            : $"unknown ({title})";
    }

    // ── Setting up ───────────────────────────────────────────────────────────

    /// <summary>
    /// Empties the body with Ctrl+A and Delete, and checks that it is empty. Those are keys under
    /// test too, so a failure here is a failure of the case, never something to retry past.
    /// </summary>
    private static void ClearBody()
    {
        if (Read().Text.Length == 0) return;
        Keyboard.Send("^a{DELETE}");
        Thread.Sleep(150);
        var after = Read();
        if (after.Text.Length != 0)
            throw new InvalidOperationException($"Ctrl+A, Delete did not empty the body; it holds {CaseFile.Show(after.Text)}.");
    }

    private static void SwitchTo(string mode)
    {
        if (CurrentMode() == mode) return;
        // An empty body switches without the "formatting will be lost" question.
        ClearBody();
        Keyboard.Send(mode switch { "plain" => "^+1", "markdown" => "^+2", _ => "^+3" });
        for (int i = 0; i < 20 && CurrentMode() != mode; i++) Thread.Sleep(250);
        if (CurrentMode() != mode) throw new InvalidOperationException($"Could not switch to {mode}; the window says {CurrentMode()}.");
        Thread.Sleep(600);
    }

    /// <summary>
    /// Puts text in the body by typing it, a key at a time, and checks it arrived exactly.
    /// Typing is the one thing every case depends on anyway; pasting would lean on Ctrl+V and
    /// the clipboard, which are under test and can be held by other programs.
    /// </summary>
    private static void TypeBody(string text)
    {
        if (text.Length == 0) return;
        Keyboard.Type(text);
        Thread.Sleep(200);
        string now = Read().Text;
        if (now != text)
            throw new InvalidOperationException($"Typing the case's text gave {CaseFile.Show(now)}, not {CaseFile.Show(text)}.");
    }

    // ── Running a case ───────────────────────────────────────────────────────

    private static void RunCase(Case testCase, string mode, List<string> failures)
    {
        if (_app!.HasExited) throw new InvalidOperationException("QuickMail is not running.");
        SwitchTo(mode);
        ClearBody();

        foreach (var step in testCase.Steps)
        {
            string where = $"line {step.Line}, {step.Verb}";
            switch (step.Verb)
            {
                case "text":
                    ClearBody();
                    TypeBody(CaseFile.Unescape(step.Argument));
                    break;

                case "markdown":
                    // A formatted body: written as Markdown, carried into HTML mode.
                    if (mode != "html") throw new FormatException($"{where}: 'markdown' sets up an HTML body; the case must be 'modes: html'.");
                    SwitchTo("markdown");
                    TypeBody(CaseFile.Unescape(step.Argument));
                    Keyboard.Send("^+3");
                    for (int i = 0; i < 20 && CurrentMode() != "html"; i++) Thread.Sleep(250);
                    Thread.Sleep(1200);
                    break;

                case "caret":
                {
                    var (start, length) = CaseFile.Place(step.Argument, Read().Text);
                    _app.Select(start, length);
                    var now = Read();
                    if (now.SelectionStart != start || now.SelectionLength != length)
                        throw new InvalidOperationException($"{where}: could not put the caret at {step.Argument} (it is at {now.SelectionStart}, length {now.SelectionLength}).");
                    break;
                }

                case "keys":
                    Keyboard.Send(step.Argument);
                    Thread.Sleep(120);
                    break;

                case "type":
                    Keyboard.Type(CaseFile.Unescape(step.Argument));
                    Thread.Sleep(120);
                    break;

                case "window":
                    // "main" or "message": switch windows, as with Alt+Tab or the taskbar.
                    _app.SwitchToWindow(message: step.Argument == "message");
                    break;

                case "wait":
                    Thread.Sleep((int)(double.Parse(step.Argument, System.Globalization.CultureInfo.InvariantCulture) * 1000));
                    break;

                case "caret-is":
                {
                    var state = Read();
                    var (start, _) = CaseFile.Place(step.Argument, state.Text);
                    if (state.SelectionLength != 0 || state.SelectionStart != start)
                        failures.Add($"{where}: caret should be {step.Argument} (offset {start}); it is {Describe(state)}.");
                    break;
                }

                case "selection-is":
                {
                    var state = Read();
                    string expected = CaseFile.Unescape(step.Argument.Trim().Trim('"'));
                    if (state.Selected != expected)
                        failures.Add($"{where}: selection should be {CaseFile.Show(expected)}; it is {Describe(state)}.");
                    break;
                }

                case "text-is":
                {
                    var state = Read();
                    string expected = CaseFile.Unescape(step.Argument);
                    if (state.Text != expected)
                        failures.Add($"{where}: text should be {CaseFile.Show(expected)}; it is {CaseFile.Show(state.Text)}.");
                    break;
                }

                case "mode-is":
                    if (CurrentMode() != step.Argument)
                        failures.Add($"{where}: the format should be {step.Argument}; the window says {CurrentMode()}.");
                    break;

                case "front-is":
                {
                    string title = App.ForegroundTitle();
                    if (!title.Contains(step.Argument, StringComparison.OrdinalIgnoreCase))
                        failures.Add($"{where}: the window in front should be '{step.Argument}'; it is '{title}'.");
                    break;
                }

                case "focus-is":
                {
                    var (name, className, id) = App.Focused();
                    if (step.Argument == "Message body")
                    {
                        // The two editors share this name: it must be the one that is showing.
                        if (!OnTheEditor(name, id))
                            failures.Add($"{where}: focus should be on the message body ({_app.VisibleEditorId}); it is on '{name}' ({className}{(id.Length > 0 ? " #" + id : string.Empty)}).");
                    }
                    else if (!name.Contains(step.Argument, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{where}: focus should be on '{step.Argument}'; it is on '{name}' ({className}).");
                    }
                    break;
                }

                case "typing-works":
                {
                    // Two things, both required. First, before any key is pressed: focus is
                    // reported on the editor that is showing. QuickMail mends focus on the first
                    // key, so typing alone can succeed from a state in which a screen reader
                    // user was left on a nameless pane. Second: a typed letter arrives. A focus
                    // report alone has been wrong before.
                    var (name, className, id) = App.Focused();
                    if (!OnTheEditor(name, id))
                        failures.Add($"{where}: before typing, focus should be on the message body ({_app.VisibleEditorId}); it is on '{name}' ({className}{(id.Length > 0 ? " #" + id : string.Empty)}); in front: '{App.ForegroundTitle()}'.");

                    var before = Read();
                    Keyboard.Type("q");
                    Thread.Sleep(200);
                    var after = Read();
                    string expected = before.Text[..before.SelectionStart] + "q" + before.Text[(before.SelectionStart + before.SelectionLength)..];
                    if (after.Text != expected)
                        failures.Add($"{where}: a typed letter did not arrive. Text was {CaseFile.Show(before.Text)}, is {CaseFile.Show(after.Text)}. " +
                                     $"Focus is reported on '{name}' ({className}); in front: '{App.ForegroundTitle()}'.");
                    else
                        Keyboard.Send("{BACKSPACE}");
                    break;
                }

                case "markdown-is":
                {
                    if (CurrentMode() != "html") throw new FormatException($"{where}: 'markdown-is' reads an HTML body back as Markdown; the message is in {CurrentMode()}.");
                    Keyboard.Send("^+2");
                    for (int i = 0; i < 20 && CurrentMode() != "markdown"; i++) Thread.Sleep(250);
                    Thread.Sleep(600);
                    string expected = CaseFile.Unescape(step.Argument);
                    string actual = Read().Text;
                    if (actual != expected)
                        failures.Add($"{where}: as Markdown it should be {CaseFile.Show(expected)}; it is {CaseFile.Show(actual)}.");
                    Keyboard.Send("^+3");
                    for (int i = 0; i < 20 && CurrentMode() != "html"; i++) Thread.Sleep(250);
                    Thread.Sleep(1000);
                    break;
                }

                default:
                    throw new FormatException($"{where}: not a step.");
            }
        }
    }

    /// <summary>
    /// True when the focused element is the editor that is showing: by its id, or, for a build
    /// from before the editors had ids (--old-build), by the name the two editors share.
    /// </summary>
    private static bool OnTheEditor(string name, string id) =>
        App.AllowEditorByName ? name == "Message body" : id == _app!.VisibleEditorId;

    private static string Describe(EditorState state)
    {
        string before = state.Text[..state.SelectionStart];
        string after = state.Text[(state.SelectionStart + state.SelectionLength)..];
        return state.SelectionLength == 0
            ? $"at offset {state.SelectionStart}: {CaseFile.Show(before + "|" + after)}"
            : $"a selection of {CaseFile.Show(state.Selected)} at offset {state.SelectionStart} in {CaseFile.Show(state.Text)}";
    }

    // ── The report ───────────────────────────────────────────────────────────

    private static void WriteReport(string? path, string label, List<Result> results, int restarts, string? aborted)
    {
        if (path == null) return;
        var sb = new StringBuilder();
        int failed = results.Count(r => r.Failures.Count > 0);
        sb.AppendLine("# Message editor keyboard tests");
        sb.AppendLine();
        sb.AppendLine($"- Run: {DateTime.Now:yyyy-MM-dd HH:mm}, {label}");
        sb.AppendLine($"- QuickMail: `{_exe}` ({FileVersionInfo.GetVersionInfo(_exe).ProductVersion})");
        sb.AppendLine($"- Result: **{results.Count - failed} passed, {failed} failed, of {results.Count}**" +
                      (restarts > 0 ? $" (QuickMail restarted {restarts} times, once after each failure)" : string.Empty));
        if (aborted != null) sb.AppendLine($"- **The run stopped early:** {aborted}");
        sb.AppendLine();

        if (failed > 0)
        {
            sb.AppendLine("## Failures");
            sb.AppendLine();
            foreach (var r in results.Where(r => r.Failures.Count > 0))
            {
                sb.AppendLine($"- **[{r.Mode}] {r.Case.Name}** ({r.Case.File})");
                foreach (var failure in r.Failures) sb.AppendLine($"  - {failure}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Every case");
        sb.AppendLine();
        sb.AppendLine("| Case | Plain Text | Markdown | HTML |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var group in results.GroupBy(r => r.Case))
        {
            string Cell(string mode)
            {
                var r = group.FirstOrDefault(x => x.Mode == mode);
                return r == null ? "not run" : r.Failures.Count == 0 ? "pass" : "FAIL";
            }
            sb.AppendLine($"| {group.Key.Name} | {Cell("plain")} | {Cell("markdown")} | {Cell("html")} |");
        }

        if (_warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Notes from the run");
            sb.AppendLine();
            foreach (var group in _warnings.GroupBy(w => w)) sb.AppendLine($"- {group.Key} ({group.Count()} times)");
        }

        var judgments = results.Select(r => r.Case).Distinct().Where(c => c.Judgment != null).ToList();
        if (judgments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Expected results that are judgment calls");
            sb.AppendLine();
            foreach (var c in judgments) sb.AppendLine($"- **{c.Name}**: {c.Judgment}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"Report: {path}");
    }
}
