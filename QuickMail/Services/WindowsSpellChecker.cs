using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using QuickMail.Helpers;

namespace QuickMail.Services;

/// <summary>
/// Windows' own spell checker (the Spell Checking API, <c>ISpellChecker</c>), which is also what
/// draws the wavy underlines in the hosted WinUI editor (#1099). QuickMail asks it directly for
/// its spelling announcements, Next and Previous Misspelling and the Check Spelling dialog, since
/// that editor has no API to ask where its underlines are.
///
/// <para>Created on the UI thread and only used there. If Windows has no spell checker for the
/// language, every text checks clean, as with the speller turned off.</para>
/// </summary>
internal sealed class WindowsSpellChecker : IWordSpeller
{
    private readonly ISpellChecker? _checker;

    public WindowsSpellChecker(string? languageTag = null)
    {
        try
        {
            var factory = (ISpellCheckerFactory)(object)new SpellCheckerFactoryClass();
            foreach (var tag in new[] { languageTag, CultureInfo.CurrentCulture.Name, "en-US" })
            {
                if (string.IsNullOrEmpty(tag) || !factory.IsSupported(tag)) continue;
                _checker = factory.CreateSpellChecker(tag);
                break;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or ArgumentException)
        {
            LogService.Log($"Windows spell checker is not available: {ex.Message}");
        }
    }

    /// <summary>False when Windows has no spell checker to offer; nothing is ever misspelled then.</summary>
    public bool IsAvailable => _checker != null;

    public IReadOnlyList<(int Start, int Length)> Check(string text)
    {
        var result = new List<(int Start, int Length)>();
        if (_checker == null || string.IsNullOrWhiteSpace(text)) return result;
        try
        {
            var errors = _checker.Check(text);
            while (errors.Next(out var error) == 0 && error != null)
            {
                // "Delete" marks a repeated word ("the the"), which is not a misspelling and has
                // no suggestions to offer.
                if (error.CorrectiveAction != CorrectiveAction.Delete)
                    result.Add(((int)error.StartIndex, (int)error.Length));
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            LogService.Log($"Windows spell checker failed to check a line: {ex.Message}");
        }
        return result;
    }

    public IReadOnlyList<string> Suggest(string word)
    {
        var result = new List<string>();
        if (_checker == null || string.IsNullOrWhiteSpace(word)) return result;
        try
        {
            var suggestions = _checker.Suggest(word);
            var one = new string[1];
            while (suggestions.Next(1, one, IntPtr.Zero) == 0)
            {
                // With nothing to suggest, the checker hands the word itself back.
                if (!string.IsNullOrEmpty(one[0]) && !string.Equals(one[0], word, StringComparison.Ordinal))
                    result.Add(one[0]);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            LogService.Log($"Windows spell checker failed to suggest: {ex.Message}");
        }
        return result;
    }

    private enum CorrectiveAction { None = 0, GetSuggestions = 1, Replace = 2, Delete = 3 }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private sealed class SpellCheckerFactoryClass { }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { get; }

        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);

        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    // Only the methods up to the last one used are declared; the rest of the interface follows them.
    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }

        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);

        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig]
        int Next([MarshalAs(UnmanagedType.Interface)] out ISpellingError? value);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint StartIndex { get; }
        uint Length { get; }
        CorrectiveAction CorrectiveAction { get; }
        string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    }
}
