using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Views;

namespace QuickMail.Tests;

/// <summary>
/// The parts of the editor experiment (#1099) that need no window: where the misspellings are in
/// a plain-text body, how document positions in the hosted editor map to indexes in QuickMail's
/// text, and the setting that turns the editor on.
/// </summary>
public class NewEditorSupportTests
{
    /// <summary>Flags every word in <see cref="Wrong"/>, wherever it is in the text it is given.</summary>
    private sealed class FakeSpeller : IWordSpeller
    {
        public HashSet<string> Wrong { get; } = new(StringComparer.Ordinal) { "teh", "recieve", "wrld" };
        public List<string> Checked { get; } = new();

        public IReadOnlyList<(int Start, int Length)> Check(string text)
        {
            Checked.Add(text);
            return Regex.Matches(text, @"[A-Za-z]+")
                .Where(m => Wrong.Contains(m.Value))
                .Select(m => (m.Index, m.Length))
                .ToList();
        }

        public IReadOnlyList<string> Suggest(string word) => new[] { word + "-fixed" };
    }

    private const string Text = "I recieve teh mail\r\n\r\nhello wrld\r\nall fine here";

    // ── PlainTextSpelling ────────────────────────────────────────────────────

    [Fact]
    public void ErrorAt_FindsTheMisspellingTheIndexIsIn_WithPositionsInTheWholeText()
    {
        var spelling = new PlainTextSpelling(new FakeSpeller());
        int wrld = Text.IndexOf("wrld", StringComparison.Ordinal);

        Assert.Equal((wrld, 4), spelling.ErrorAt(Text, wrld));
        Assert.Equal((wrld, 4), spelling.ErrorAt(Text, wrld + 3));
        Assert.Null(spelling.ErrorAt(Text, wrld + 4));                    // just past its end
        Assert.Null(spelling.ErrorAt(Text, Text.IndexOf("hello", StringComparison.Ordinal)));
        Assert.Null(spelling.ErrorAt(Text, -1));
        Assert.Null(spelling.ErrorAt(Text, Text.Length));
    }

    [Fact]
    public void NextErrorStart_Forward_CountsTheWordTheIndexIsIn_ThenCrossesLines()
    {
        var spelling = new PlainTextSpelling(new FakeSpeller());
        int recieve = Text.IndexOf("recieve", StringComparison.Ordinal);
        int teh = Text.IndexOf("teh", StringComparison.Ordinal);
        int wrld = Text.IndexOf("wrld", StringComparison.Ordinal);

        Assert.Equal(recieve, spelling.NextErrorStart(Text, 0, forward: true));
        Assert.Equal(recieve, spelling.NextErrorStart(Text, recieve + 2, forward: true));
        Assert.Equal(teh, spelling.NextErrorStart(Text, recieve + 7, forward: true));
        // From the end of the first line, across the blank line, to the third.
        Assert.Equal(wrld, spelling.NextErrorStart(Text, teh + 3, forward: true));
        Assert.Equal(-1, spelling.NextErrorStart(Text, wrld + 4, forward: true));
    }

    [Fact]
    public void NextErrorStart_Backward_FindsTheNearestStartingAtOrBeforeTheIndex()
    {
        var spelling = new PlainTextSpelling(new FakeSpeller());
        int recieve = Text.IndexOf("recieve", StringComparison.Ordinal);
        int teh = Text.IndexOf("teh", StringComparison.Ordinal);
        int wrld = Text.IndexOf("wrld", StringComparison.Ordinal);

        Assert.Equal(wrld, spelling.NextErrorStart(Text, Text.Length - 1, forward: false));
        Assert.Equal(teh, spelling.NextErrorStart(Text, wrld - 1, forward: false));
        Assert.Equal(recieve, spelling.NextErrorStart(Text, teh - 1, forward: false));
        Assert.Equal(-1, spelling.NextErrorStart(Text, recieve - 1, forward: false));
    }

    [Fact]
    public void NextErrorStart_OnEmptyText_FindsNothing()
    {
        var spelling = new PlainTextSpelling(new FakeSpeller());
        Assert.Equal(-1, spelling.NextErrorStart(string.Empty, 0, forward: true));
        Assert.Equal(-1, spelling.NextErrorStart(string.Empty, 0, forward: false));
    }

    [Fact]
    public void WordsInTheUsersDictionary_AreNotMisspellings_AndTheAnswerFollowsTheDictionary()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        var spelling = new PlainTextSpelling(new FakeSpeller(), known.Contains);
        int teh = Text.IndexOf("teh", StringComparison.Ordinal);

        Assert.NotNull(spelling.ErrorAt(Text, teh));
        known.Add("teh");   // added mid-session: the kept line results must not hide it
        Assert.Null(spelling.ErrorAt(Text, teh));
    }

    [Fact]
    public void ALine_IsCheckedOnce_HoweverManyTimesItIsAsked()
    {
        var speller = new FakeSpeller();
        var spelling = new PlainTextSpelling(speller);
        int teh = Text.IndexOf("teh", StringComparison.Ordinal);

        for (int i = 0; i < 5; i++) spelling.ErrorAt(Text, teh);

        Assert.Equal(new[] { "I recieve teh mail" }, speller.Checked);
    }

    [Fact]
    public void Suggestions_ComeFromTheSpeller()
    {
        var spelling = new PlainTextSpelling(new FakeSpeller());
        Assert.Equal(new[] { "teh-fixed" }, spelling.Suggestions("teh"));
    }

    // ── RichEditOffsets ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "")]
    [InlineData("\r", "")]                              // an empty document is one paragraph mark
    [InlineData("abc\r", "abc")]
    [InlineData("abc\rdef\r", "abc\r\ndef")]
    [InlineData("abc\r\rdef\r", "abc\r\n\r\ndef")]      // a blank line
    [InlineData("abc\r\r", "abc\r\n")]                  // the text itself ends with a line break
    [InlineData("abc\vdef\r", "abc\r\ndef")]            // a soft break is a line break too
    public void ToText_DropsTheFinalParagraphMark_AndWritesBreaksAsCrLf(string raw, string expected) =>
        Assert.Equal(expected, RichEditOffsets.ToText(raw));

    [Theory]
    [InlineData("abc\r\ndef", "abc\rdef")]
    [InlineData("abc\ndef", "abc\rdef")]
    [InlineData("abc\r\n\r\ndef\r\n", "abc\r\rdef\r")]
    public void ToRaw_WritesOneCarriageReturnPerLineBreak(string text, string expected) =>
        Assert.Equal(expected, RichEditOffsets.ToRaw(text));

    [Fact]
    public void PositionsAndIndexes_RoundTrip_AcrossLineBreaks()
    {
        const string text = "ab\r\ncd\r\n\r\nef";
        // index -> position: each "\r\n" before the index is one position, not two.
        Assert.Equal(0, RichEditOffsets.ToPosition(text, 0));
        Assert.Equal(2, RichEditOffsets.ToPosition(text, 2));    // end of "ab"
        Assert.Equal(3, RichEditOffsets.ToPosition(text, 4));    // start of "cd"
        Assert.Equal(6, RichEditOffsets.ToPosition(text, 8));    // the blank line
        Assert.Equal(7, RichEditOffsets.ToPosition(text, 10));   // start of "ef"
        Assert.Equal(9, RichEditOffsets.ToPosition(text, text.Length));

        foreach (int index in new[] { 0, 1, 2, 4, 5, 6, 8, 10, 11, 12 })
            Assert.Equal(index, RichEditOffsets.ToIndex(text, RichEditOffsets.ToPosition(text, index)));

        // A position past the end (the final paragraph mark, selected by Select All) is the end.
        Assert.Equal(text.Length, RichEditOffsets.ToIndex(text, 99));
    }

    // ── The body's line breaks ───────────────────────────────────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("no breaks", "no breaks")]
    [InlineData("a\nb", "a\r\nb")]
    [InlineData("a\r\nb", "a\r\nb")]
    [InlineData("a\rb", "a\r\nb")]
    [InlineData("a\n\r\nb\r", "a\r\n\r\nb\r\n")]
    public void NormalizeLineBreaks_MakesEveryBreakCrLf(string? text, string expected) =>
        Assert.Equal(expected, ComposeWindow.NormalizeLineBreaks(text));

    // ── Windows' spell checker: the real call, not a fake ────────────────────

    [Fact]
    public void WindowsSpellChecker_FindsAMisspelling_AndSuggestsTheWord()
    {
        var speller = new WindowsSpellChecker("en-US");
        Assert.SkipUnless(speller.IsAvailable, "Windows has no en-US spell checker on this machine.");

        const string line = "I recieve mail";
        var errors = speller.Check(line);

        var error = Assert.Single(errors);
        Assert.Equal("recieve", line.Substring(error.Start, error.Length));
        Assert.Contains("receive", speller.Suggest("recieve"));
        Assert.Empty(speller.Check("I receive mail"));
        Assert.Empty(speller.Check("   "));
    }

    // ── Keys the hosted editor keeps from RichEdit ───────────────────────────

    [Theory]
    [InlineData(System.Windows.Input.Key.E, System.Windows.Input.ModifierKeys.Control, true)]       // center
    [InlineData(System.Windows.Input.Key.R, System.Windows.Input.ModifierKeys.Control, true)]       // right-align
    [InlineData(System.Windows.Input.Key.D2, System.Windows.Input.ModifierKeys.Control, true)]      // double spacing
    [InlineData(System.Windows.Input.Key.L, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift, true)] // bullets
    [InlineData(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control, false)]      // copy stays
    [InlineData(System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control, false)]      // undo stays
    [InlineData(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control, false)]      // select all stays
    [InlineData(System.Windows.Input.Key.E, System.Windows.Input.ModifierKeys.None, false)]         // the letter itself
    public void RichEditFormattingKeys_AreKeptFromTheEditor_AndEditingKeysAreNot(
        System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers, bool blocked) =>
        Assert.Equal(blocked, QuickMail.Controls.WinUiEditorHost.IsRichEditFormattingKey(key, modifiers));

    // ── The setting ──────────────────────────────────────────────────────────

    [Fact]
    public void UseNewEditor_DefaultsOn_AndRoundTripsOff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-neweditor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var profile = new ProfileContext(dir);
            var service = new ConfigService(profile);

            var config = service.Load();
            Assert.True(config.UseNewEditor);
            Assert.True(new ConfigModel().UseNewEditor);

            config.UseNewEditor = false;
            service.Save(config);

            Assert.False(new ConfigService(profile).Load().UseNewEditor);
            Assert.Contains("NewMessageEditor = off", File.ReadAllText(Path.Combine(dir, "config.ini")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
