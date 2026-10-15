using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Choosing the word each field says (#807), suggested on #800: a count can say just its number,
/// Unread count can say "of", a state can say a shorter word, and a text field's label can change.
/// The test case the user gave: "6 of 12 Budget review Lunch tomorrow?".
/// </summary>
[Collection("WpfTests")]
public class RowFieldWordsTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static List<MailMessageSummary> Messages(int unread, int total) =>
        Enumerable.Range(0, total).Select(i => new MailMessageSummary
        {
            MessageId = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AccountId = Guid.Empty, FolderName = "INBOX", From = "Chris Lee",
            Subject = "Budget review", Preview = "Lunch tomorrow?", Date = DateTimeOffset.Now,
            IsRead = i >= unread,
        }).ToList();

    private static object?[] Conversation(int unread, int total) =>
        RowFieldCatalog.ValuesFor(RowKind.Conversation,
            new ConversationGroup { NormalizedSubject = "budget review", Messages = Messages(unread, total) });

    /// <summary>A layout with only these fields on, in this order, with these words.</summary>
    private static List<RowFieldSetting> Only(RowKind kind, params (string Id, string? Word)[] fields)
    {
        var layout = RowFieldCatalog.DefaultLayout(kind);
        foreach (var f in layout) f.Enabled = false;
        var on = fields.Select(x =>
        {
            var f = layout.First(s => s.Id == x.Id);
            f.Enabled = true;
            f.Word = x.Word;
            return f;
        }).ToList();
        return on.Concat(layout.Where(s => !fields.Any(x => x.Id == s.Id))).ToList();
    }

    private static object?[] Message(bool unread = true) =>
        ["", "unread", false, "Chris Lee", "Budget review", "Lunch tomorrow?", "2:14 PM",
         unread, false, false, "", "", false, false];

    // ── the #800 example ──────────────────────────────────────────────────────

    [Fact]
    public void TheCustomersRowReadsExactlyAsAsked()
    {
        var layout = Only(RowKind.Conversation,
            ("unreadcount", "of"), ("count", ""), ("subject", null), ("preview", null));

        Assert.Equal("6 of 12 Budget review Lunch tomorrow?",
            RowSpeechBuilder.Build(RowKind.Conversation, Conversation(6, 12), layout,
                                   separator: RowFieldSeparator.Space));
    }

    [Fact]
    public void AndWhenEverythingIsRead_TheRowStartsWithTheTotal()
    {
        var layout = Only(RowKind.Conversation,
            ("unreadcount", "of"), ("count", ""), ("subject", null), ("preview", null));

        Assert.Equal("12 Budget review Lunch tomorrow?",
            RowSpeechBuilder.Build(RowKind.Conversation, Conversation(0, 12), layout,
                                   separator: RowFieldSeparator.Space));
    }

    // ── each kind of field ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 1, "1 message.")]
    [InlineData(null, 3, "3 messages.")]
    [InlineData("msgs", 1, "1 msgs.")]
    [InlineData("msgs", 3, "3 msgs.")]
    [InlineData("", 3, "3.")]
    [InlineData("  in thread  ", 3, "3 in thread.")]
    public void MessageCount(string? word, int total, string expected)
    {
        var layout = Only(RowKind.Conversation, ("count", word));
        Assert.Equal(expected, RowSpeechBuilder.Build(RowKind.Conversation, Conversation(0, total), layout));
    }

    [Theory]
    [InlineData(null, "2 of 5 unread.")]
    [InlineData("", "2 of 5.")]
    [InlineData("new", "2 of 5 new.")]
    public void UnreadOfTotal(string? word, string expected)
    {
        var layout = Only(RowKind.Conversation, ("unreadoftotal", word));
        Assert.Equal(expected, RowSpeechBuilder.Build(RowKind.Conversation, Conversation(2, 5), layout));
    }

    [Fact]
    public void AStateSaysItsChosenWord_AndABlankWordSaysNothing()
    {
        var newWord = Only(RowKind.Message, ("unread", "new"), ("subject", null));
        var blank   = Only(RowKind.Message, ("unread", ""), ("subject", null));

        Assert.Equal("new. Budget review.", RowSpeechBuilder.Build(RowKind.Message, Message(), newWord));
        Assert.Equal("Budget review.", RowSpeechBuilder.Build(RowKind.Message, Message(), blank));
    }

    [Fact]
    public void AStatesFalseWordIsChosenSeparately()
    {
        var layout = Only(RowKind.Message, ("unread", null), ("subject", null));
        var unread = layout.First(f => f.Id == "unread");
        unread.SpeakMode = SpeakMode.Always;
        unread.FalseWord = "seen";

        Assert.Equal("seen. Budget review.", RowSpeechBuilder.Build(RowKind.Message, Message(unread: false), layout));
        Assert.Equal("unread. Budget review.", RowSpeechBuilder.Build(RowKind.Message, Message(unread: true), layout));
    }

    [Fact]
    public void ATextFieldsWordIsItsLabel_SpokenOnlyWithLabelsOn()
    {
        var layout = Only(RowKind.Message, ("from", "Sender"), ("subject", ""));

        Assert.Equal("Sender: Chris Lee. Budget review.",
            RowSpeechBuilder.Build(RowKind.Message, Message(), layout, showLabels: true));
        Assert.Equal("Chris Lee. Budget review.",
            RowSpeechBuilder.Build(RowKind.Message, Message(), layout, showLabels: false));
    }

    [Fact]
    public void UntouchedLayoutsSayExactlyWhatTheyAlwaysSaid()
    {
        foreach (var kind in new[] { RowKind.Message, RowKind.Conversation, RowKind.SenderGroup })
        {
            var layout = RowFieldCatalog.DefaultLayout(kind);
            Assert.All(layout, f => { Assert.Null(f.Word); Assert.Null(f.FalseWord); });
        }
        Assert.Equal("unread. Chris Lee. Budget review. Lunch tomorrow?. 2:14 PM.",
            RowSpeechBuilder.Build(RowKind.Message, Message(), RowFieldCatalog.DefaultLayout(RowKind.Message)));
    }

    // ── what may be stored ────────────────────────────────────────────────────

    [Fact]
    public void TypingTheShippedWordStoresNothing()
    {
        Assert.Null(RowFieldWords.Override("unread", "unread"));
        Assert.Null(RowFieldWords.Override(" unread ", "unread"));
        Assert.Equal("of", RowFieldWords.Override("of", "unread"));
        Assert.Equal(string.Empty, RowFieldWords.Override("", "unread"));
    }

    [Fact]
    public void WordsLoseControlAndFormatCharacters_AndAreCapped()
    {
        Assert.Equal("ab", RowFieldWords.Clean("a\r\nb"));
        Assert.Equal("ab", RowFieldWords.Clean("a‮b"));                // right-to-left override
        Assert.Equal("not read ", RowFieldWords.Clean("not read "));        // the space before a 2nd word
        Assert.Equal(RowFieldWords.MaxLength, RowFieldWords.Clean(new string('x', 99)).Length);
    }

    [Fact]
    public void EachKindOfFieldShowsItsShippedWord()
    {
        string Default(RowKind k, string id) => RowFieldWords.DefaultWord(RowFieldCatalog.Find(k, id)!);

        Assert.Equal("unread", Default(RowKind.Message, "unread"));
        Assert.Equal("From", Default(RowKind.Message, "from"));
        Assert.Equal("messages", Default(RowKind.Conversation, "count"));
        Assert.Equal("unread", Default(RowKind.Conversation, "unreadcount"));
        Assert.Equal("unread", Default(RowKind.Conversation, "unreadoftotal"));
    }

    // ── saved with the layout ─────────────────────────────────────────────────

    [Fact]
    public void WordsSurviveASaveAndLoad_AndAnUntouchedFieldWritesNoWord()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-words-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new RowLayoutService(new ProfileContext(dir), new StubConfigService());
            var layouts = service.Load();
            layouts.Conversation.First(f => f.Id == "count").Word = "";
            layouts.Conversation.First(f => f.Id == "unreadcount").Word = "of";
            service.Save(layouts);

            var json = File.ReadAllText(Path.Combine(dir, "rowlayout.json"));
            var reloaded = new RowLayoutService(new ProfileContext(dir), new StubConfigService()).Load();

            Assert.Equal("", reloaded.Conversation.First(f => f.Id == "count").Word);
            Assert.Equal("of", reloaded.Conversation.First(f => f.Id == "unreadcount").Word);
            Assert.Null(reloaded.Conversation.First(f => f.Id == "subject").Word);
            // Two words chosen, two written: an untouched field adds nothing to the file.
            Assert.Equal(2, json.Split("\"Word\"").Length - 1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    // ── the chooser ───────────────────────────────────────────────────────────

    private static (RowFieldsViewModel vm, StubRowLayoutService layouts) Chooser(RowKind kind)
    {
        var layouts = new StubRowLayoutService();
        var vm = new RowFieldsViewModel(layouts, new StubConfigService());
        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == kind);
        return (vm, layouts);
    }

    [Fact]
    public void TheBoxShowsTheShippedWord_AndAChangeIsSavedAndPreviewed()
    {
        var (vm, layouts) = Chooser(RowKind.Conversation);
        vm.SelectedField = vm.Fields.First(f => f.Id == "count");
        Assert.Equal("messages", vm.SelectedField!.Word);
        Assert.False(vm.RestoreDefaultWordCommand.CanExecute(null));

        vm.SelectedField.Word = "";

        Assert.Equal("", layouts.Load().Conversation.First(f => f.Id == "count").Word);
        Assert.DoesNotContain("messages", vm.Preview, StringComparison.Ordinal);
        Assert.True(vm.RestoreDefaultWordCommand.CanExecute(null));
    }

    [Fact]
    public void DefaultWordPutsTheShippedWordBack()
    {
        var (vm, layouts) = Chooser(RowKind.Conversation);
        vm.SelectedField = vm.Fields.First(f => f.Id == "count");
        vm.SelectedField!.Word = "msgs";

        vm.RestoreDefaultWordCommand.Execute(null);

        Assert.Equal("messages", vm.SelectedField.Word);
        Assert.Null(layouts.Load().Conversation.First(f => f.Id == "count").Word);
        Assert.False(vm.RestoreDefaultWordCommand.CanExecute(null));
    }

    [Fact]
    public void ResetToDefaultsRestoresTheWordsToo()
    {
        var (vm, _) = Chooser(RowKind.Conversation);
        vm.Fields.First(f => f.Id == "count").Word = "";

        vm.ResetDefaultsCommand.Execute(null);

        Assert.Equal("messages", vm.Fields.First(f => f.Id == "count").Word);
    }

    [Fact]
    public void WhenOffAppearsOnlyForAStateWithAFalseWord_UnderAlwaysSpeak()
    {
        var (vm, _) = Chooser(RowKind.Message);

        vm.SelectedField = vm.Fields.First(f => f.Id == "unread");
        vm.SelectedField!.Enabled = true;
        Assert.False(vm.SelectedShowsFalseWord);
        vm.SpeakAlways = true;
        Assert.True(vm.SelectedShowsFalseWord);
        vm.SpeakWhenTrue = true;
        Assert.False(vm.SelectedShowsFalseWord);

        // Attachments has nothing to say when false, and a text field has no state.
        vm.SelectedField = vm.Fields.First(f => f.Id == "attachments");
        vm.SpeakAlways = true;
        Assert.False(vm.SelectedShowsFalseWord);
        vm.SelectedField = vm.Fields.First(f => f.Id == "subject");
        Assert.False(vm.SelectedShowsFalseWord);
    }

    // ── the window ────────────────────────────────────────────────────────────

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [StaFact]
    public void TheWindowsWordBoxIsNamed_AndEditsTheSelectedField()
    {
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService());
        var window = new RowFieldsWindow(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var box = window.FindName("WordBox") as TextBox;
            Assert.NotNull(box);
            Assert.Equal("Spoken as", UIElementAutomationPeer.CreatePeerForElement(box!).GetName());

            vm.SelectedField = vm.Fields.First(f => f.Id == "unread");
            DrainDispatcher();
            Assert.Equal("unread", box!.Text);

            box.Text = "new";
            Assert.Equal("new", vm.SelectedField!.Word);

            var falseBox = window.FindName("FalseWordBox") as TextBox;
            Assert.NotNull(falseBox);
            Assert.False(falseBox!.IsVisible, "When off belongs only to Always speak");

            var button = window.FindName("DefaultWordButton") as Button;
            Assert.NotNull(button);
            Assert.Equal("Default word", UIElementAutomationPeer.CreatePeerForElement(button!).GetName());
        }
        finally
        {
            window.Close();
            System.Windows.Input.Keyboard.ClearFocus();
            DrainDispatcher();
        }
    }
}
