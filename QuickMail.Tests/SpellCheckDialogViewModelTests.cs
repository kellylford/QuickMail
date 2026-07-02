using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Unit tests for the Check Spelling session state machine, driven by scripted
/// sources — no WPF editors involved.
/// </summary>
public class SpellCheckDialogViewModelTests
{
    /// <summary>
    /// Scripted <see cref="ISpellCheckSource"/>: yields a fixed sequence of
    /// errors and records the replacements applied to each.
    /// </summary>
    private sealed class ScriptedSource : ISpellCheckSource
    {
        private readonly List<SpellingErrorInfo> _errors;
        private int _index = -1;

        public string DisplayName { get; }
        public List<(string Word, string Replacement)> Replacements { get; } = [];
        public int SelectCalls { get; private set; }

        public ScriptedSource(string displayName, params SpellingErrorInfo[] errors)
        {
            DisplayName = displayName;
            _errors = errors.ToList();
        }

        public SpellingErrorInfo? MoveToNextError()
        {
            _index++;
            return _index < _errors.Count ? _errors[_index] : null;
        }

        public void ReplaceCurrent(string replacement) =>
            Replacements.Add((_errors[_index].Word, replacement));

        public void SelectCurrent() => SelectCalls++;

        public string GetContextLine() => $"line with {_errors[_index].Word}";
    }

    private sealed class RecordingDictionary : ICustomDictionaryService
    {
        public List<string> Added { get; } = [];
        public string DictionaryPath => "unused";
        public event Action? DictionaryChanged { add { } remove { } }
        public bool Contains(string word) => Added.Contains(word);
        public bool AddWord(string word) { Added.Add(word); return true; }
    }

    private static SpellingErrorInfo Err(string word, params string[] suggestions) =>
        new(word, suggestions);

    private static SpellCheckDialogViewModel MakeVm(params ISpellCheckSource[] sources) =>
        new(sources, dictionary: null);

    [Fact]
    public void MoveNext_PresentsFirstError_WithSuggestionsAndContext()
    {
        var vm = MakeVm(new ScriptedSource("body", Err("recieve", "receive", "relieve")));

        Assert.True(vm.MoveNext());
        Assert.Equal("recieve", vm.CurrentWord);
        Assert.True(vm.HasSuggestions);
        Assert.Equal(["receive", "relieve"], vm.Suggestions);
        Assert.Equal("receive", vm.SelectedSuggestion);
        Assert.Equal("receive", vm.ChangeToText);
        Assert.Equal("line with recieve", vm.ContextLine);
        Assert.Equal("body", vm.CurrentSourceName);
    }

    [Fact]
    public void MoveNext_ReturnsFalse_WhenNoErrorsAnywhere()
    {
        var vm = MakeVm(new ScriptedSource("body"), new ScriptedSource("subject"));

        Assert.False(vm.MoveNext());
        Assert.True(vm.IsCompleted);
        Assert.False(vm.PresentedAnyError);
    }

    [Fact]
    public void Change_ReplacesAndAdvances()
    {
        var body = new ScriptedSource("body",
            Err("recieve", "receive"), Err("pacakge", "package"));
        var vm = MakeVm(body);
        vm.MoveNext();

        Assert.True(vm.Change());

        Assert.Equal([("recieve", "receive")], body.Replacements);
        Assert.Equal("pacakge", vm.CurrentWord);
        Assert.Equal(1, vm.ChangedCount);
    }

    [Fact]
    public void Change_WithEditedChangeToText_UsesTheEdit()
    {
        var body = new ScriptedSource("body", Err("xqzt"));
        var vm = MakeVm(body);
        vm.MoveNext();

        Assert.False(vm.HasSuggestions);
        Assert.Equal("xqzt", vm.ChangeToText);   // pre-filled with the word itself
        vm.ChangeToText = "exact";
        vm.Change();

        Assert.Equal([("xqzt", "exact")], body.Replacements);
    }

    [Fact]
    public void Change_WithEmptyChangeTo_StaysOnCurrentWord()
    {
        var body = new ScriptedSource("body", Err("recieve", "receive"));
        var vm = MakeVm(body);
        vm.MoveNext();
        vm.ChangeToText = "   ";

        Assert.True(vm.Change());   // session continues…
        Assert.Empty(body.Replacements);   // …but nothing was replaced
        Assert.Equal("recieve", vm.CurrentWord);
        Assert.Equal(0, vm.ChangedCount);
    }

    [Fact]
    public void ChangeAll_SilentlyReplacesLaterOccurrences_AndCountsThem()
    {
        var body = new ScriptedSource("body",
            Err("recieve", "receive"),
            Err("blorf"),
            Err("recieve", "receive"),
            Err("recieve", "receive"));
        var vm = MakeVm(body);
        vm.MoveNext();

        Assert.True(vm.ChangeAll());

        // The dialog stopped only on "blorf"; both later "recieve"s were auto-replaced
        // when Ignore advanced the scan past them.
        Assert.Equal("blorf", vm.CurrentWord);
        Assert.False(vm.Ignore());   // advancing consumes the two auto-replacements, then exhausts

        Assert.Equal(3, body.Replacements.Count);
        Assert.All(body.Replacements, r => Assert.Equal(("recieve", "receive"), r));
        Assert.Equal(3, vm.ChangedCount);
        Assert.True(vm.IsCompleted);
    }

    [Fact]
    public void IgnoreAll_SkipsEveryLaterOccurrence()
    {
        var body = new ScriptedSource("body",
            Err("Kestrelworks"),
            Err("Kestrelworks"),
            Err("zzyx"),
            Err("Kestrelworks"));
        var vm = MakeVm(body);
        vm.MoveNext();

        Assert.True(vm.IgnoreAll());
        Assert.Equal("zzyx", vm.CurrentWord);

        Assert.False(vm.Ignore());
        Assert.True(vm.IsCompleted);
        Assert.Empty(body.Replacements);
    }

    [Fact]
    public void IgnoreAll_IsCaseSensitive()
    {
        var body = new ScriptedSource("body",
            Err("blorf"),
            Err("Blorf"));
        var vm = MakeVm(body);
        vm.MoveNext();

        vm.IgnoreAll();
        // Differently-cased occurrence still stops the scan.
        Assert.Equal("Blorf", vm.CurrentWord);
    }

    [Fact]
    public void AddToDictionary_AddsWordAndAdvances()
    {
        var dictionary = new RecordingDictionary();
        var body = new ScriptedSource("body", Err("QuickMail"), Err("zzyx"));
        var vm = new SpellCheckDialogViewModel([body], dictionary);
        vm.MoveNext();

        Assert.True(vm.AddToDictionary());

        Assert.Equal(["QuickMail"], dictionary.Added);
        Assert.Equal("zzyx", vm.CurrentWord);
    }

    [Fact]
    public void AddToDictionary_WithoutService_FallsBackToSessionIgnore()
    {
        var body = new ScriptedSource("body",
            Err("QuickMail"), Err("QuickMail"), Err("zzyx"));
        var vm = MakeVm(body);
        vm.MoveNext();

        Assert.True(vm.AddToDictionary());
        Assert.Equal("zzyx", vm.CurrentWord);   // second occurrence skipped
    }

    [Fact]
    public void SourceTransition_RaisesCheckingSourceChanged()
    {
        var body = new ScriptedSource("body", Err("recieve", "receive"));
        var subject = new ScriptedSource("subject", Err("Shiping", "Shipping"));
        var vm = MakeVm(body, subject);
        vm.MoveNext();

        var transitions = new List<string>();
        vm.CheckingSourceChanged += transitions.Add;

        Assert.True(vm.Change());   // exhausts body, moves into subject
        Assert.Equal(["subject"], transitions);
        Assert.Equal("Shiping", vm.CurrentWord);
        Assert.Equal("subject", vm.CurrentSourceName);

        Assert.False(vm.Change());
        Assert.True(vm.IsCompleted);
        Assert.Equal(2, vm.ChangedCount);
    }

    [Fact]
    public void SelectedSuggestion_UpdatesChangeToText()
    {
        var vm = MakeVm(new ScriptedSource("body", Err("recieve", "receive", "relieve")));
        vm.MoveNext();

        vm.SelectedSuggestion = "relieve";
        Assert.Equal("relieve", vm.ChangeToText);
    }

    [Fact]
    public void Presenting_SelectsTheWordInTheEditor()
    {
        var body = new ScriptedSource("body", Err("recieve", "receive"));
        var vm = MakeVm(body);
        vm.MoveNext();
        Assert.Equal(1, body.SelectCalls);
    }

    [Theory]
    [InlineData(0, false, "The spelling check is complete. No misspellings were found.",
        "Spelling check complete. No misspellings found.")]
    [InlineData(0, true, "The spelling check is complete. No changes were made.",
        "Spelling check complete. No changes made.")]
    [InlineData(1, true, "The spelling check is complete. 1 word changed.",
        "Spelling check complete. 1 word changed.")]
    [InlineData(3, true, "The spelling check is complete. 3 words changed.",
        "Spelling check complete. 3 words changed.")]
    public void CompletionText_ReflectsSessionOutcome(int changes, bool presentedAny,
        string expectedText, string expectedAnnouncement)
    {
        var errors = Enumerable.Range(0, changes)
            .Select(_ => Err("recieve", "receive"))
            .Concat(presentedAny && changes == 0 ? [Err("blorf")] : Array.Empty<SpellingErrorInfo>())
            .ToArray();
        var vm = MakeVm(new ScriptedSource("body", errors));

        if (vm.MoveNext())
        {
            for (int i = 0; i < changes; i++) vm.Change();
            while (!vm.IsCompleted) vm.Ignore();
        }

        Assert.Equal(expectedText, vm.CompletionText);
        Assert.Equal(expectedAnnouncement, vm.CompletionAnnouncement);
    }

    [Fact]
    public void ErrorAnnouncement_DistinguishesNoSuggestions()
    {
        var vm = MakeVm(new ScriptedSource("body", Err("recieve", "receive"), Err("xqzt")));
        vm.MoveNext();
        Assert.Equal("Not in dictionary: recieve.", vm.BuildErrorAnnouncement());

        vm.Ignore();
        Assert.Equal("Not in dictionary: xqzt. No suggestions.", vm.BuildErrorAnnouncement());
    }

    [Fact]
    public void Change_WithUnchangedWord_AdvancesWithoutEditingOrCounting()
    {
        var body = new ScriptedSource("body", Err("xqzt"), Err("recieve", "receive"));
        var vm = MakeVm(body);
        vm.MoveNext();

        Assert.False(vm.HasSuggestions);
        Assert.Equal("xqzt", vm.ChangeToText);   // pre-filled with the word itself
        Assert.True(vm.Change());                // Enter on the default Change button

        Assert.Empty(body.Replacements);         // no self-replacement edit, no dirty draft
        Assert.Equal(0, vm.ChangedCount);
        Assert.Equal("recieve", vm.CurrentWord); // but the scan still advanced
    }

    [Fact]
    public void ChangeAll_WithUnchangedWord_DoesNotAutoReplaceLaterOccurrences()
    {
        var body = new ScriptedSource("body", Err("xqzt"), Err("xqzt"));
        var vm = MakeVm(body);
        vm.MoveNext();

        Assert.True(vm.ChangeAll());   // no-op change-all must not enter the map

        Assert.Empty(body.Replacements);
        Assert.Equal(0, vm.ChangedCount);
        Assert.Equal("xqzt", vm.CurrentWord);   // second occurrence is still presented
    }

    [Fact]
    public void CancelAnnouncement_ReportsKeptChanges()
    {
        var body = new ScriptedSource("body",
            Err("recieve", "receive"), Err("pacakge", "package"), Err("blorf"));
        var vm = MakeVm(body);
        vm.MoveNext();
        Assert.Equal("Spelling check canceled.", vm.CancelAnnouncement);

        vm.Change();
        Assert.Equal("Spelling check canceled. 1 change kept.", vm.CancelAnnouncement);

        vm.Change();
        Assert.Equal("Spelling check canceled. 2 changes kept.", vm.CancelAnnouncement);
    }

    [Fact]
    public void LastPresentedSourceName_SurvivesCompletion()
    {
        var body = new ScriptedSource("body");
        var subject = new ScriptedSource("subject", Err("Shiping", "Shipping"));
        var vm = MakeVm(body, subject);

        Assert.True(vm.MoveNext());
        Assert.Equal("subject", vm.LastPresentedSourceName);

        Assert.False(vm.Change());   // exhausts the session
        Assert.True(vm.IsCompleted);
        Assert.Equal(string.Empty, vm.CurrentSourceName);      // current source is gone at completion…
        Assert.Equal("subject", vm.LastPresentedSourceName);   // …but the last presented one survives for focus restore
    }
}
