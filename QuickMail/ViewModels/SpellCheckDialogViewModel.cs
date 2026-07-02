using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Session state machine for the Check Spelling dialog. Owns everything that
/// is not editor access: the ignore-all set, the change-all map, counters, the
/// current word and suggestions, and announcement/completion text. Editor
/// access goes through <see cref="ISpellCheckSource"/> (body first, then
/// subject), so the whole session is unit-testable with a scripted source.
///
/// Every verb returns true while the session has more errors to present and
/// false when it is complete; the View reacts by re-presenting or closing.
/// Ignore All and Change All match exactly (case-sensitive) — predictable, and
/// consistent with the spell engine treating differently-cased words as
/// distinct errors.
/// </summary>
public sealed partial class SpellCheckDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<ISpellCheckSource> _sources;
    private readonly ICustomDictionaryService? _dictionary;
    private readonly HashSet<string> _ignoredWords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _changeAllMap = new(StringComparer.Ordinal);
    private ISpellCheckSource? _currentSource;
    private int _sourceIndex;

    [ObservableProperty] private string _currentWord = string.Empty;
    [ObservableProperty] private string _changeToText = string.Empty;
    [ObservableProperty] private string _contextLine = string.Empty;
    [ObservableProperty] private string? _selectedSuggestion;

    public ObservableCollection<string> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>True after the scan exhausted every source (vs. canceled).</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>True once the dialog has presented at least one error.</summary>
    public bool PresentedAnyError { get; private set; }

    public int ChangedCount { get; private set; }

    /// <summary>Display name of the source owning the current word ("body", "subject").</summary>
    public string CurrentSourceName => _currentSource?.DisplayName ?? string.Empty;

    /// <summary>
    /// Display name of the source that presented the most recent error. Unlike
    /// <see cref="CurrentSourceName"/> this survives completion (when
    /// <c>_currentSource</c> is already null), so the View can return focus to
    /// the editor the user last worked in.
    /// </summary>
    public string LastPresentedSourceName { get; private set; } = string.Empty;

    /// <summary>
    /// Raised when the scan moves into a new source, with its display name
    /// ("subject"). The View announces the transition (Status category).
    /// </summary>
    public event Action<string>? CheckingSourceChanged;

    public SpellCheckDialogViewModel(IReadOnlyList<ISpellCheckSource> sources,
        ICustomDictionaryService? dictionary)
    {
        _sources = sources;
        _dictionary = dictionary;
        _currentSource = sources.Count > 0 ? sources[0] : null;
    }

    partial void OnSelectedSuggestionChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            ChangeToText = value;
    }

    /// <summary>
    /// Advances to the next error the user must decide on, applying pending
    /// change-all replacements and skipping ignored words along the way.
    /// Returns false when every source is exhausted.
    /// </summary>
    public bool MoveNext()
    {
        while (_currentSource != null)
        {
            var error = _currentSource.MoveToNextError();
            if (error == null)
            {
                _sourceIndex++;
                if (_sourceIndex >= _sources.Count)
                {
                    _currentSource = null;
                    IsCompleted = true;
                    return false;
                }
                _currentSource = _sources[_sourceIndex];
                CheckingSourceChanged?.Invoke(_currentSource.DisplayName);
                continue;
            }

            if (_ignoredWords.Contains(error.Word)) continue;

            if (_changeAllMap.TryGetValue(error.Word, out var replacement))
            {
                _currentSource.ReplaceCurrent(replacement);
                ChangedCount++;
                continue;
            }

            Present(error);
            return true;
        }

        IsCompleted = true;
        return false;
    }

    private void Present(SpellingErrorInfo error)
    {
        PresentedAnyError = true;
        LastPresentedSourceName = _currentSource?.DisplayName ?? string.Empty;
        CurrentWord = error.Word;

        Suggestions.Clear();
        foreach (var s in error.Suggestions)
            Suggestions.Add(s);
        OnPropertyChanged(nameof(HasSuggestions));

        SelectedSuggestion = Suggestions.Count > 0 ? Suggestions[0] : null;
        ChangeToText = Suggestions.Count > 0 ? Suggestions[0] : error.Word;
        ContextLine = _currentSource?.GetContextLine() ?? string.Empty;
        _currentSource?.SelectCurrent();
    }

    /// <summary>Replaces the current word with the Change-to text.</summary>
    public bool Change()
    {
        if (_currentSource == null) return false;
        var replacement = ChangeToText.Trim();
        if (replacement.Length == 0) return true;   // nothing to change with — stay on the word
        if (replacement == CurrentWord) return MoveNext();   // no-op (e.g. Enter on a no-suggestion word) — advance without editing or counting

        _currentSource.ReplaceCurrent(replacement);
        ChangedCount++;
        return MoveNext();
    }

    /// <summary>Change, plus auto-replace every later occurrence of the word this session.</summary>
    public bool ChangeAll()
    {
        if (_currentSource == null) return false;
        var replacement = ChangeToText.Trim();
        if (replacement.Length == 0) return true;
        if (replacement == CurrentWord) return MoveNext();   // no-op — a change-all to the same word would silently "count" every occurrence

        _changeAllMap[CurrentWord] = replacement;
        _currentSource.ReplaceCurrent(replacement);
        ChangedCount++;
        return MoveNext();
    }

    /// <summary>Skips this occurrence only.</summary>
    public bool Ignore() => MoveNext();

    /// <summary>Skips this word for the rest of the session.</summary>
    public bool IgnoreAll()
    {
        _ignoredWords.Add(CurrentWord);
        return MoveNext();
    }

    /// <summary>
    /// Adds the word to the custom dictionary (permanent). Without a dictionary
    /// service the word is session-ignored instead, so the verb still advances.
    /// </summary>
    public bool AddToDictionary()
    {
        if (_dictionary == null || !_dictionary.AddWord(CurrentWord))
            _ignoredWords.Add(CurrentWord);
        return MoveNext();
    }

    /// <summary>The announcement made when an error is presented.</summary>
    public string BuildErrorAnnouncement() => HasSuggestions
        ? $"Not in dictionary: {CurrentWord}."
        : $"Not in dictionary: {CurrentWord}. No suggestions.";

    /// <summary>Message shown in the completion confirmation.</summary>
    public string CompletionText => ChangedCount switch
    {
        0 when !PresentedAnyError => "The spelling check is complete. No misspellings were found.",
        0 => "The spelling check is complete. No changes were made.",
        1 => "The spelling check is complete. 1 word changed.",
        _ => $"The spelling check is complete. {ChangedCount} words changed.",
    };

    /// <summary>Announcement matching <see cref="CompletionText"/>.</summary>
    public string CompletionAnnouncement => ChangedCount switch
    {
        0 when !PresentedAnyError => "Spelling check complete. No misspellings found.",
        0 => "Spelling check complete. No changes made.",
        1 => "Spelling check complete. 1 word changed.",
        _ => $"Spelling check complete. {ChangedCount} words changed.",
    };

    /// <summary>
    /// Announcement when the dialog is closed before the scan completes.
    /// Corrections already applied are ordinary editor edits and remain in the
    /// message, so the count is reported rather than implying a rollback.
    /// </summary>
    public string CancelAnnouncement => ChangedCount switch
    {
        0 => "Spelling check canceled.",
        1 => "Spelling check canceled. 1 change kept.",
        _ => $"Spelling check canceled. {ChangedCount} changes kept.",
    };
}
