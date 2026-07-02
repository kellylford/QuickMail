using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QuickMail.Services;

/// <summary>
/// File-backed implementation of <see cref="ICustomDictionaryService"/>.
/// Owns <c>{ProfileDir}\custom.lex</c>: one word per line, UTF-16 LE with BOM
/// (the encoding WPF's spell engine reads most reliably). Word comparison is
/// exact (ordinal) — the spell engine applies its own casing rules, so "QuickMail"
/// and "quickmail" are distinct entries a user may legitimately both add.
/// Holds no unmanaged state; nothing to dispose.
/// </summary>
public sealed class CustomDictionaryService : ICustomDictionaryService
{
    private readonly HashSet<string> _words = new(StringComparer.Ordinal);

    public string DictionaryPath { get; }

    public event Action? DictionaryChanged;

    public CustomDictionaryService(ProfileContext profile)
    {
        DictionaryPath = Path.Combine(profile.ProfileDir, "custom.lex");
        LoadExistingWords();
    }

    private void LoadExistingWords()
    {
        try
        {
            if (!File.Exists(DictionaryPath)) return;
            // ReadAllLines honours the BOM; '#' lines are lexicon headers (e.g. #LID 1033).
            foreach (var line in File.ReadAllLines(DictionaryPath))
            {
                var word = line.Trim();
                if (word.Length > 0 && !word.StartsWith('#'))
                    _words.Add(word);
            }
        }
        catch (Exception ex)
        {
            // A corrupt or unreadable file must not block compose windows from opening;
            // the first successful AddWord recreates it.
            LogService.Log("CustomDictionaryService load", ex);
        }
    }

    public bool Contains(string word) => _words.Contains(word.Trim());

    public bool AddWord(string word)
    {
        var trimmed = word?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace)) return false;
        if (!_words.Add(trimmed)) return false;

        try
        {
            // AppendAllText writes the UTF-16 LE BOM when it creates the file
            // (StreamWriter emits the preamble at stream position 0) and appends
            // without a BOM thereafter.
            File.AppendAllText(DictionaryPath, trimmed + Environment.NewLine, Encoding.Unicode);
        }
        catch (Exception ex)
        {
            _words.Remove(trimmed);
            LogService.Log("CustomDictionaryService add", ex);
            return false;
        }

        DictionaryChanged?.Invoke();
        return true;
    }
}
