// What the compose window's spelling features say (bug bash of 30 October 2026):
//   #898 — Next Misspelling (Ctrl+F7) said "Misspelling: …" twice: its own announcement, and the
//          navigation announcement for the caret landing in the word it had just selected.
//   #902 — web and email addresses were announced as misspellings.
// Announcements are read through AccessibilityHelper.AnnouncementObserver, so these tests see
// exactly what would have been handed to the screen reader.

using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ComposeSpellingSpeechTests
{
    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static List<string> Listen()
    {
        var heard = new List<string>();
        AccessibilityHelper.AnnouncementObserver = (text, _) => heard.Add(text);
        return heard;
    }

    private static int Misspellings(List<string> heard) =>
        heard.Count(t => t.StartsWith("Misspelling:", StringComparison.Ordinal));

    // ── #898 ────────────────────────────────────────────────────────────────────

    [StaFact]
    public void NextMisspelling_PlainText_SaysTheMisspellingOnce()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        window.Show();
        try
        {
            Drain();
            body.Text = "I recieve the pacakge";
            body.CaretIndex = 0;
            body.Focus();
            Drain();
            var heard = Listen();

            window.NavigateSpellingError(forward: true);
            Drain();
            Assert.Equal("recieve", body.SelectedText);
            Assert.Equal(1, Misspellings(heard));

            heard.Clear();
            window.NavigateSpellingError(forward: true);
            Drain();
            Assert.Equal("pacakge", body.SelectedText);
            Assert.Equal(1, Misspellings(heard));
            Assert.StartsWith("Misspelling: pacakge", heard.Single(), StringComparison.Ordinal);

            heard.Clear();
            window.NavigateSpellingError(forward: false);
            Drain();
            Assert.Equal("recieve", body.SelectedText);
            Assert.Equal(1, Misspellings(heard));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void NextMisspelling_Html_SaysTheMisspellingOnce()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, vm) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var rich = (RichTextBox)window.FindName("RichBodyBox");
        window.Show();
        try
        {
            Drain();
            vm.Body = "I recieve the pacakge";
            vm.SetMode(ComposeMode.Html);
            window.UpdateLayout();
            Drain();
            rich.CaretPosition = rich.Document.ContentStart;
            rich.Focus();
            Drain();
            var heard = Listen();

            window.NavigateSpellingError(forward: true);
            Drain();
            Assert.Equal("recieve", rich.Selection.Text);
            Assert.Equal(1, Misspellings(heard));

            heard.Clear();
            window.NavigateSpellingError(forward: true);
            Drain();
            Assert.Equal("pacakge", rich.Selection.Text);
            Assert.Equal(1, Misspellings(heard));

            // Previous Misspelling goes back to the one before, not the word already selected.
            heard.Clear();
            window.NavigateSpellingError(forward: false);
            Drain();
            Assert.Equal("recieve", rich.Selection.Text);
            Assert.Equal(1, Misspellings(heard));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void ArrowingIntoAMisspelling_StillSaysIt()
    {
        // The #898 fix quiets the navigation announcement only for Ctrl+F7's own selection.
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        window.Show();
        try
        {
            Drain();
            body.Text = "I recieve mail";
            body.CaretIndex = 0;
            body.Focus();
            Drain();
            var heard = Listen();

            body.CaretIndex = 4;   // inside "recieve"
            Drain();

            Assert.Equal(1, Misspellings(heard));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    // ── #902 ────────────────────────────────────────────────────────────────────

    // Built from misspelled words. The tests only mean something where the speller flags a word
    // inside an address, so each checks that it does first and skips visibly if not.
    private const string Url = "https://recieve.example/pacakge";
    private const string Email = "tomorow@pacakge.example";

    [StaFact]
    public void ArrowingOverAnAddress_PlainText_SaysNoMisspelling()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        window.Show();
        try
        {
            Drain();
            var text = $"See {Url} or {Email} now";
            body.Text = text;
            body.CaretIndex = 0;
            body.Focus();
            Drain();
            SpellCheckSourceTests.SkipUnlessFlagsInsideAnAddress(body);
            var heard = Listen();

            for (int i = 1; i <= text.Length; i++)
            {
                body.CaretIndex = i;
                Drain();
            }

            Assert.Equal(0, Misspellings(heard));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void ArrowingOverAnAddress_Html_SaysNoMisspelling()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, vm) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var rich = (RichTextBox)window.FindName("RichBodyBox");
        window.Show();
        try
        {
            Drain();
            vm.Body = $"See {Url} or {Email} now";
            vm.SetMode(ComposeMode.Html);
            window.UpdateLayout();
            Drain();
            rich.CaretPosition = rich.Document.ContentStart;
            rich.Focus();
            Drain();
            SpellCheckSourceTests.SkipUnlessFlagsInsideAnAddress(rich);
            var heard = Listen();

            var pos =rich.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            while (pos.GetNextInsertionPosition(LogicalDirection.Forward) is { } next)
            {
                rich.CaretPosition = pos = next;
                Drain();
            }

            Assert.Equal(0, Misspellings(heard));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void NextMisspelling_SkipsAddresses_AndSaysNoneWhenOnlyAddressesAreLeft()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        window.Show();
        try
        {
            Drain();
            body.Text = $"See {Url} or {Email} and teh rest";
            body.CaretIndex = 0;
            body.Focus();
            Drain();
            SpellCheckSourceTests.SkipUnlessFlagsInsideAnAddress(body);
            var heard = Listen();

            window.NavigateSpellingError(forward: true);
            Drain();
            Assert.Equal("teh", body.SelectedText);
            Assert.StartsWith("Misspelling: teh.", Assert.Single(heard), StringComparison.Ordinal);

            body.Text = $"See {Url} or {Email}";
            body.CaretIndex = 0;
            Drain();
            heard.Clear();
            window.NavigateSpellingError(forward: true);
            Drain();
            Assert.Equal("No misspellings found.", Assert.Single(heard));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void NextMisspelling_Html_SkipsAddresses()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, vm) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var rich = (RichTextBox)window.FindName("RichBodyBox");
        window.Show();
        try
        {
            Drain();
            vm.Body = $"See {Url} or {Email} and teh rest";
            vm.SetMode(ComposeMode.Html);
            window.UpdateLayout();
            Drain();
            rich.CaretPosition = rich.Document.ContentStart;
            rich.Focus();
            Drain();
            SpellCheckSourceTests.SkipUnlessFlagsInsideAnAddress(rich);

            window.NavigateSpellingError(forward: true);
            Assert.Equal("teh", rich.Selection.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void MisspelledMarkdownLinkText_IsStillSaid()
    {
        ComposeWindowSpellCheckTests.SkipWithoutSpellingEngine();
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        window.Show();
        try
        {
            Drain();
            const string text = "Read [recieve](https://pacakge.example/plan) now";
            int linkText = text.IndexOf("recieve", StringComparison.Ordinal);
            body.Text = text;
            body.CaretIndex = 0;
            body.Focus();
            Drain();
            Assert.SkipUnless(body.GetSpellingError(linkText) != null,
                "The speller does not flag the text of a Markdown link on this machine.");
            var heard = Listen();

            body.CaretIndex = linkText + 2;   // arrowed into the link text
            Drain();
            Assert.Equal(1, Misspellings(heard));

            body.CaretIndex = 0;
            Drain();
            heard.Clear();
            window.NavigateSpellingError(forward: true);
            Drain();
            Assert.Equal(1, Misspellings(heard));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    // ── #901 ────────────────────────────────────────────────────────────────────

    [StaFact]
    public void AutoSaveNotice_WaitsForAPause_AndIsSaidOnce()
    {
        var (window, _) = ComposeWindowSpellCheckTests.NewHeadlessComposeWindowWithVm();
        var body = (TextBox)window.FindName("BodyBox");
        var now = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        window.NoticeClock = () => now;
        window.Show();
        try
        {
            Drain();
            body.Text = "Friday? Or Monday.";
            body.Focus();
            Drain();
            var heard = new List<(string Text, AnnouncementCategory Category)>();
            AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));

            const string notice = "Auto-save is keeping your draft on this computer until you're online.";
            window.QueueAutoSaveNotice(notice);
            window.FlushAutoSaveNoticeIfQuiet();
            Assert.Empty(heard);

            // The user keeps moving through the body: each caret move starts the pause again.
            for (int i = 1; i <= 5; i++)
            {
                now = now.AddSeconds(1.5);
                body.CaretIndex = i;
                Drain();
                window.FlushAutoSaveNoticeIfQuiet();
                Assert.Empty(heard);
            }

            now = now.AddSeconds(1.9);
            window.FlushAutoSaveNoticeIfQuiet();
            Assert.Empty(heard);

            now = now.AddSeconds(0.2);   // two seconds since the last caret move
            window.FlushAutoSaveNoticeIfQuiet();
            Assert.Equal((notice, AnnouncementCategory.Status), Assert.Single(heard));

            now = now.AddSeconds(10);
            window.FlushAutoSaveNoticeIfQuiet();
            Assert.Single(heard);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }
}
