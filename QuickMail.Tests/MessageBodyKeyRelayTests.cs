using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// A key pressed inside a message body goes to the WebView2, never to the window's own key handling, so
/// the reading pane and the message window each inject a script that relays a set of gestures back to
/// WPF. The two sets have drifted apart twice: #483, and #676, where the reading pane had no Ctrl+Shift+P
/// and the command palette could not be opened while reading.
/// </summary>
public class MessageBodyKeyRelayTests
{
    // Gestures both message bodies must relay. Each surface may relay more of its own (the reading pane
    // also jumps to the folder tree).
    private static readonly string[] Shared =
        ["escape", "f6", "shift-f6", "shift-tab", "focus-attachments", "ctrl-w", "ctrl-shift-w", "ctrl-shift-p"];

    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void EveryMessageBody_RelaysTheSharedGestures(string file)
    {
        var posted = Posted(Source(file));

        foreach (var gesture in Shared)
            Assert.True(posted.Contains(gesture), $"{file} does not relay '{gesture}' out of the message body.");
    }

    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void EveryRelayedGesture_IsHandled(string file)
    {
        // A relay with no handler is a key that is swallowed: the script prevents the default and nothing acts.
        var code = Source(file);

        foreach (var gesture in Posted(code))
            Assert.True(code.Contains($"msg == \"{gesture}\"", StringComparison.Ordinal)
                        || code.Contains($"case \"{gesture}\":", StringComparison.Ordinal),
                        $"{file} relays '{gesture}' but nothing handles it.");
    }

    [Fact]
    public void ClosingThePaletteFromTheReadingPane_ReturnsToTheMessage()
    {
        // WPF reports nothing focused inside the message body (#672), so the palette's own "restore what had
        // focus" fell back to the message list, and closing it read as the message having closed itself.
        var code = Source("MainWindow.xaml.cs");
        var start = code.IndexOf("private void OpenCommandPalette()", StringComparison.Ordinal);
        Assert.True(start >= 0, "OpenCommandPalette is gone.");
        var body = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];

        // Where focus was is read before the palette opens, since afterwards it is on the palette's owner.
        var captured = body.IndexOf("var fromMessageBody = IsMessageBodyFocused;", StringComparison.Ordinal);
        var shown = body.IndexOf("palette.ShowDialog()", StringComparison.Ordinal);
        Assert.True(captured >= 0 && shown > captured, "fromMessageBody must be read before the palette is shown.");
        Assert.Contains("if (!(fromMessageBody && _vm.IsMessageOpen))", body, StringComparison.Ordinal);
        Assert.Contains("FocusMessageBodyHost();", body[shown..], StringComparison.Ordinal);
    }

    [Fact]
    public void AfterAPaletteCommand_FocusReturnsToTheMessageOnlyOnceItHasRun()
    {
        // The palette queues the chosen command at Input priority. Putting focus into the WebView2 before it
        // runs would open a dialog with a text box over a focused message body. So with a command chosen, the
        // return is queued behind it, and only taken while this window is active with the message still open.
        var code = Source("MainWindow.xaml.cs");
        var start = code.IndexOf("private void OpenCommandPalette()", StringComparison.Ordinal);
        Assert.True(start >= 0, "OpenCommandPalette is gone.");
        var body = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("var commandChosen = palette.ShowDialog() == true;", body, StringComparison.Ordinal);
        var chosen = body.IndexOf("else if (!commandChosen)", StringComparison.Ordinal);
        Assert.True(chosen >= 0, "Returning straight into the message must be only for a dismissed palette.");
        var queued = body[chosen..];
        Assert.Contains("Dispatcher.InvokeAsync(", queued, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Input", queued, StringComparison.Ordinal);
        Assert.Contains("if (!IsActive || !untouched) return;", queued, StringComparison.Ordinal);
        // A command that closed the message without placing focus still ends on the list, as it did when
        // OnActivated sent it there.
        Assert.Contains("else ReturnFocusToMessageList();", queued, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactivatingAfterThePalette_LeavesTheReturnToTheMessageAlone()
    {
        // Closing the palette reactivates the main window, and OnActivated sends focus that was in the message
        // body to the list. Unless it stands aside while the palette puts focus back, the list wins every time.
        var code = Source("MainWindow.xaml.cs");
        var start = code.IndexOf("private void OpenCommandPalette()", StringComparison.Ordinal);
        Assert.True(start >= 0, "OpenCommandPalette is gone.");
        var palette = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];
        var set = palette.IndexOf("_paletteRestoresFocus = fromMessageBody && _vm.IsMessageOpen;", StringComparison.Ordinal);
        var shown = palette.IndexOf("palette.ShowDialog()", StringComparison.Ordinal);
        Assert.True(set >= 0 && shown > set, "The flag must be set before the palette opens: the activation comes while it closes.");
        Assert.Contains("Dispatcher.InvokeAsync(() => _paletteRestoresFocus = false, DispatcherPriority.Input);",
                        palette[shown..], StringComparison.Ordinal);

        var activated = code.IndexOf("private void OnActivated(", StringComparison.Ordinal);
        Assert.True(activated >= 0, "OnActivated is gone.");
        var body = code[activated..code.IndexOf("\n    }", activated, StringComparison.Ordinal)];
        var skip = body.IndexOf("if (_paletteRestoresFocus) return;", StringComparison.Ordinal);
        var restore = body.IndexOf("ReturnFocusToMessageList()", StringComparison.Ordinal);
        Assert.True(skip >= 0 && restore > skip, "OnActivated must check the flag before sending focus to the list.");
    }

    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void CtrlW_IsRelayedOnlyWithShiftUp_InEitherCase(string file)
    {
        // With Caps Lock on, Ctrl+W arrives as 'W' and Ctrl+Shift+W as 'w', so the case says nothing about
        // Shift. Ctrl+W's branch has to check Shift itself and accept both cases, or one gesture is lost or
        // taken by the other.
        Assert.Contains("e.ctrlKey&&!e.shiftKey&&(e.key==='w'||e.key==='W')){window.chrome.webview.postMessage('ctrl-w')",
                        Source(file), StringComparison.Ordinal);
    }

    /// <summary>Every name the file's injected scripts pass to <c>postMessage</c>.</summary>
    private static HashSet<string> Posted(string code)
        => Regex.Matches(code, @"postMessage\(([^)]*)\)")
                .SelectMany(call => Regex.Matches(call.Groups[1].Value, @"'([a-z0-9-]+)'").Select(m => m.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);

    private static string Source(string file)
        => File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", file));

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
