using System;

namespace QuickMail.Services;

/// <summary>
/// Manages the user's custom spelling dictionary — the words added via
/// "Add to Dictionary" in the spell check dialog. The dictionary is a WPF
/// lexicon (.lex) file in the profile directory; Views register it on their
/// editors through <c>SpellCheck.CustomDictionaries</c> and re-register when
/// <see cref="DictionaryChanged"/> fires (WPF only re-reads a lexicon on
/// remove + re-add of its Uri).
/// </summary>
public interface ICustomDictionaryService
{
    /// <summary>Absolute path of the custom dictionary lexicon file.
    /// The file may not exist yet — it is created by the first <see cref="AddWord"/>.</summary>
    string DictionaryPath { get; }

    /// <summary>Raised after a word is added, so registered editors can refresh.</summary>
    event Action? DictionaryChanged;

    /// <summary>True when <paramref name="word"/> is already in the custom dictionary (exact match).</summary>
    bool Contains(string word);

    /// <summary>
    /// Adds <paramref name="word"/> to the dictionary, creating the file on first use.
    /// Returns false (and changes nothing) when the word is empty, contains whitespace,
    /// or is already present.
    /// </summary>
    bool AddWord(string word);
}
