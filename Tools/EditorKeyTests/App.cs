using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace EditorKeyTests;

/// <summary>What the message editor holds: its text, and where the caret or selection is in it.</summary>
internal sealed record EditorState(string Text, int SelectionStart, int SelectionLength)
{
    public string Selected => Text.Substring(SelectionStart, SelectionLength);
}

/// <summary>
/// The running QuickMail: starts it on a profile, opens a new message, and reads the message
/// editor through UI Automation, which is how a screen reader reads it too.
/// </summary>
internal sealed class App : IDisposable
{
    private readonly Process _process;
    private AutomationElement? _compose;

    private App(Process process) => _process = process;

    public static App Start(string exe, string profileDir)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add("--profileDir");
        info.ArgumentList.Add(profileDir);
        info.ArgumentList.Add("/debug");
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var app = new App(process);
        var main = app.WaitForWindow(w => w.Current.Name.EndsWith("- QuickMail", StringComparison.Ordinal)
                                          && !w.Current.Name.Contains("Message", StringComparison.Ordinal), 60,
            "QuickMail's main window");
        BringToFront(new IntPtr(main.Current.NativeWindowHandle), "QuickMail's main window");
        Thread.Sleep(1500);
        return app;
    }

    /// <summary>Opens a new message (Ctrl+N in the main window) and puts focus in its body (Alt+Y).</summary>
    public void OpenNewMessage()
    {
        Keyboard.Send("^n");
        _compose = WaitForWindow(w => w.Current.Name.StartsWith("New Message", StringComparison.Ordinal), 30, "a New Message window");
        Thread.Sleep(1500);
        Keyboard.Send("%y");
        Thread.Sleep(800);
    }

    /// <summary>
    /// Brings a window to the front, once, at the start of a run. Windows refuses this to a
    /// process that is not itself in front unless a key has just been pressed, so Alt is tapped
    /// first. Never used between the keys of a case.
    /// </summary>
    private static void BringToFront(IntPtr hwnd, string what)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (GetForegroundWindow() == hwnd) return;
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            Thread.Sleep(400);
        }
        if (GetForegroundWindow() != hwnd)
            throw new InvalidOperationException($"Could not bring {what} to the front; '{ForegroundTitle()}' is in front.");
    }

    public string ComposeTitle => _compose?.Current.Name ?? string.Empty;

    /// <summary>The window in front, by title: a dialog's when one is open.</summary>
    public static string ForegroundTitle()
    {
        var title = new char[300];
        int length = GetWindowText(GetForegroundWindow(), title, title.Length);
        return new string(title, 0, Math.Max(0, length));
    }

    /// <summary>The element UI Automation reports as focused: its name, its class and its automation id.</summary>
    public static (string Name, string Class, string Id) Focused()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            return (element.Current.Name, element.Current.ClassName, element.Current.AutomationId);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return ($"(unavailable: {ex.Message})", string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// The automation id of the editor that is showing. The two editors have one name, so a
    /// focus check by name cannot tell the hidden one from the visible one; the id can.
    /// </summary>
    public string VisibleEditorId =>
        ComposeTitle.Contains("- HTML -", StringComparison.Ordinal) ? "HtmlMessageBody" : "PlainMessageBody";

    /// <summary>The message body's editor in the compose window. Throws if it is not the new editor.</summary>
    private AutomationElement Editor()
    {
        if (_compose == null) throw new InvalidOperationException("No compose window is open.");
        // There are two body editors in the window, one for HTML and one for the other two
        // formats, with the same name; only one is showing. The window's title says which.
        string id = VisibleEditorId;
        var element = _compose.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        if (element != null) return element;
        var found = _compose.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Message body"));
        var classes = string.Join(", ", found.Cast<AutomationElement>().Select(e => e.Current.ClassName + "#" + e.Current.AutomationId));
        throw new InvalidOperationException(
            $"The new editor ({id}) was not found (found: {(classes.Length == 0 ? "nothing" : classes)}). " +
            "These tests are for the new editor: is the setting on?");
    }

    /// <summary>The editor's text (paragraph ends as \n) and selection, as UI Automation gives them.</summary>
    public EditorState Read()
    {
        var pattern = (TextPattern)Editor().GetCurrentPattern(TextPattern.Pattern);
        var document = pattern.DocumentRange;
        string text = Normalize(document.GetText(-1));
        var selection = pattern.GetSelection();
        if (selection.Length == 0) return new EditorState(text, 0, 0);

        // Offsets: the text from the document's start up to each end of the selection.
        var before = document.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
        int start = Normalize(before.GetText(-1)).Length;
        int length = Normalize(selection[0].GetText(-1)).Length;
        start = Math.Min(start, text.Length);
        length = Math.Min(length, text.Length - start);
        return new EditorState(text, start, length);
    }

    /// <summary>
    /// The editor's text ends with a paragraph mark the user did not type; UI Automation may or
    /// may not include it. Paragraph ends come as \r, \n or both, and are given as \n; a line break
    /// inside a paragraph (\v) is left as it is, so the two can be told apart.
    /// </summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Puts the caret, or a selection, at an offset in the editor's text.</summary>
    public void Select(int start, int length)
    {
        var pattern = (TextPattern)Editor().GetCurrentPattern(TextPattern.Pattern);
        var range = pattern.DocumentRange.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        if (start > 0) range.Move(TextUnit.Character, start);
        if (length > 0) range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, length);
        range.Select();
        Thread.Sleep(150);
    }

    private AutomationElement WaitForWindow(Func<AutomationElement, bool> match, int seconds, string what)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited) throw new InvalidOperationException($"QuickMail exited (code {_process.ExitCode}) while waiting for {what}.");
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, condition))
            {
                try { if (match(window)) return window; }
                catch (ElementNotAvailableException) { }
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException($"Timed out after {seconds} s waiting for {what}.");
    }

    public bool HasExited => _process.HasExited;

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, [Out] char[] text, int max);
}
