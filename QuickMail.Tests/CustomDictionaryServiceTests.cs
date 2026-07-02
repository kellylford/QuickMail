using System;
using System.IO;
using System.Text;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Round-trip tests for <see cref="CustomDictionaryService"/> — the profile-level
/// custom spelling dictionary behind "Add to Dictionary".
/// </summary>
public class CustomDictionaryServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly ProfileContext _profile;

    public CustomDictionaryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"QM-CustomDictTests-{Guid.NewGuid():N}");
        _profile = new ProfileContext(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void DictionaryPath_IsCustomLexInProfileDir()
    {
        var svc = new CustomDictionaryService(_profile);
        Assert.Equal(Path.Combine(_dir, "custom.lex"), svc.DictionaryPath);
    }

    [Fact]
    public void AddWord_CreatesFileOnFirstAdd()
    {
        var svc = new CustomDictionaryService(_profile);
        Assert.False(File.Exists(svc.DictionaryPath));

        Assert.True(svc.AddWord("QuickMail"));

        Assert.True(File.Exists(svc.DictionaryPath));
        Assert.Contains("QuickMail", File.ReadAllLines(svc.DictionaryPath));
    }

    [Fact]
    public void AddWord_IsIdempotent()
    {
        var svc = new CustomDictionaryService(_profile);
        Assert.True(svc.AddWord("QuickMail"));
        Assert.False(svc.AddWord("QuickMail"));

        Assert.Single(File.ReadAllLines(svc.DictionaryPath));
    }

    [Fact]
    public void AddWord_RejectsEmptyAndWhitespaceWords()
    {
        var svc = new CustomDictionaryService(_profile);
        Assert.False(svc.AddWord(""));
        Assert.False(svc.AddWord("   "));
        Assert.False(svc.AddWord("two words"));
        Assert.False(File.Exists(svc.DictionaryPath));
    }

    [Fact]
    public void AddWord_TrimsBeforeAdding()
    {
        var svc = new CustomDictionaryService(_profile);
        Assert.True(svc.AddWord("  Kestrelworks  "));
        Assert.True(svc.Contains("Kestrelworks"));
        Assert.Contains("Kestrelworks", File.ReadAllLines(svc.DictionaryPath));
    }

    [Fact]
    public void AddWord_IsCaseSensitive()
    {
        // The spell engine applies its own casing rules, so "quickmail" and
        // "QuickMail" are distinct entries a user may legitimately both add.
        var svc = new CustomDictionaryService(_profile);
        Assert.True(svc.AddWord("QuickMail"));
        Assert.True(svc.AddWord("quickmail"));
        Assert.Equal(2, File.ReadAllLines(svc.DictionaryPath).Length);
    }

    [Fact]
    public void AddWord_RaisesDictionaryChanged()
    {
        var svc = new CustomDictionaryService(_profile);
        int raised = 0;
        svc.DictionaryChanged += () => raised++;

        svc.AddWord("QuickMail");
        Assert.Equal(1, raised);

        // A rejected duplicate must not fire the refresh.
        svc.AddWord("QuickMail");
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Words_SurviveReload()
    {
        var svc = new CustomDictionaryService(_profile);
        svc.AddWord("QuickMail");
        svc.AddWord("Kestrelworks");

        var reloaded = new CustomDictionaryService(_profile);
        Assert.True(reloaded.Contains("QuickMail"));
        Assert.True(reloaded.Contains("Kestrelworks"));
        Assert.False(reloaded.AddWord("QuickMail"));
    }

    [Fact]
    public void NonAsciiWord_RoundTrips()
    {
        var svc = new CustomDictionaryService(_profile);
        Assert.True(svc.AddWord("Åström"));
        Assert.True(svc.AddWord("naïveté"));

        var reloaded = new CustomDictionaryService(_profile);
        Assert.True(reloaded.Contains("Åström"));
        Assert.True(reloaded.Contains("naïveté"));
    }

    [Fact]
    public void File_IsUtf16LeWithBom()
    {
        // WPF reads .lex lexicons most reliably as UTF-16 LE with BOM.
        var svc = new CustomDictionaryService(_profile);
        svc.AddWord("QuickMail");

        var bytes = File.ReadAllBytes(svc.DictionaryPath);
        Assert.True(bytes.Length >= 2, "file too short to contain a BOM");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
    }

    [Fact]
    public void LexiconHeaderLines_AreIgnoredOnLoad()
    {
        // A hand-edited file may carry a #LID language header — it is not a word.
        File.WriteAllLines(Path.Combine(_dir, "custom.lex"),
            ["#LID 1033", "QuickMail"], Encoding.Unicode);

        var svc = new CustomDictionaryService(_profile);
        Assert.True(svc.Contains("QuickMail"));
        Assert.False(svc.Contains("#LID 1033"));
    }

    [Fact]
    public void MissingFile_DoesNotThrow_AndIsRecreatedOnNextAdd()
    {
        var svc = new CustomDictionaryService(_profile);
        svc.AddWord("QuickMail");
        File.Delete(svc.DictionaryPath);

        var reloadedBeforeDelete = new CustomDictionaryService(_profile);
        Assert.True(reloadedBeforeDelete.AddWord("Kestrelworks"));
        Assert.True(File.Exists(reloadedBeforeDelete.DictionaryPath));
    }
}
