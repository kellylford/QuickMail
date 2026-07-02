using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class AttachmentSafetyTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("photo with spaces.jpg", "photo with spaces.jpg")]
    [InlineData("Ünïcödé name.txt", "Ünïcödé name.txt")]
    public void SanitizeFileName_LeavesOrdinaryNamesAlone(string input, string expected)
    {
        Assert.Equal(expected, AttachmentSafety.SanitizeFileName(input));
    }

    [Theory]
    [InlineData(@"..\..\Startup\evil.exe", "evil.exe")]
    [InlineData("../../Startup/evil.exe", "evil.exe")]
    [InlineData(@"C:\Users\victim\evil.exe", "evil.exe")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData(@"\\server\share\payload.dll", "payload.dll")]
    [InlineData(@"sub\dir\name.txt", "name.txt")]
    public void SanitizeFileName_StripsDirectoryComponents(string input, string expected)
    {
        Assert.Equal(expected, AttachmentSafety.SanitizeFileName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(@"trailing\")]
    public void SanitizeFileName_FallsBackWhenNothingUsableRemains(string? input)
    {
        Assert.Equal("attachment", AttachmentSafety.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidFileNameChars()
    {
        var result = AttachmentSafety.SanitizeFileName("bad<name>|with:chars?.txt");
        Assert.DoesNotContain('<', result);
        Assert.DoesNotContain('>', result);
        Assert.DoesNotContain('|', result);
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('?', result);
        Assert.EndsWith(".txt", result);
    }

    [Fact]
    public void SanitizeFileName_TrimsTrailingDotsAndSpaces()
    {
        // Windows silently drops trailing dots/spaces, which enables extension spoofing
        // ("evil.exe ." would be created as "evil.exe").
        Assert.Equal("evil.exe", AttachmentSafety.SanitizeFileName("evil.exe . "));
        Assert.Equal("report.pdf", AttachmentSafety.SanitizeFileName("report.pdf..."));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]
    [InlineData("script.ps1")]
    [InlineData("shortcut.lnk")]
    [InlineData("page.hta")]
    [InlineData("legacy.com")]
    [InlineData("macro.wsf")]
    [InlineData("encoded.vbe")]
    [InlineData("snapin.msc")]
    [InlineData("panel.cpl")]
    [InlineData("keys.reg")]
    [InlineData("mount-me.iso")]
    [InlineData("installer.application")]
    public void IsDangerousExtension_FlagsExecutableTypes(string name)
    {
        Assert.True(AttachmentSafety.IsDangerousExtension(name));
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("photo.jpg")]
    [InlineData("notes.txt")]
    [InlineData("data.csv")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData(null)]
    [InlineData("")]
    public void IsDangerousExtension_AllowsDocumentTypes(string? name)
    {
        Assert.False(AttachmentSafety.IsDangerousExtension(name));
    }
}
