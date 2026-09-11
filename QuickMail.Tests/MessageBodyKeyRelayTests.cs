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

        Assert.Contains("var fromMessageBody = IsMessageBodyFocused;", body, StringComparison.Ordinal);
        Assert.Contains("if (fromMessageBody && _vm.IsMessageOpen)", body, StringComparison.Ordinal);
        Assert.Contains("FocusMessageBodyHost();", body, StringComparison.Ordinal);
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
