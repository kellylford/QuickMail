using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class ExternalUriPolicyTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?query=1#frag")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("mailto:kelly@example.com")]
    [InlineData("mailto:kelly@example.com?subject=Hi%20there")]
    public void IsAllowed_PermitsHttpHttpsAndMailto(string uri)
    {
        Assert.True(ExternalUriPolicy.IsAllowed(uri));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("file://attacker-server/share/payload.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("search-ms:query=foo")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("ms-settings:network")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("ftp://example.com/file")]
    [InlineData("skype:someone?call")]
    [InlineData("ldap://example.com")]
    [InlineData("quickmail:ics-accept")]
    [InlineData("about:blank")]
    public void IsAllowed_BlocksEverythingElse(string uri)
    {
        Assert.False(ExternalUriPolicy.IsAllowed(uri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri at all")]
    [InlineData("example.com/no-scheme")]
    [InlineData("//protocol-relative.example.com")]
    public void IsAllowed_BlocksMissingOrRelativeUris(string? uri)
    {
        Assert.False(ExternalUriPolicy.IsAllowed(uri));
    }

    [Theory]
    // Scheme confusion attempts: the allowed scheme name embedded somewhere it doesn't count.
    [InlineData("file://http/https")]
    [InlineData("vbscript:http://example.com")]
    [InlineData(" javascript:alert(1)")]
    public void IsAllowed_IsNotFooledByEmbeddedSchemeNames(string uri)
    {
        Assert.False(ExternalUriPolicy.IsAllowed(uri));
    }

    [Fact]
    public void TryOpenExternal_ReturnsFalseForBlockedUri_WithoutThrowing()
    {
        Assert.False(ExternalUriPolicy.TryOpenExternal("file:///C:/evil.exe"));
        Assert.False(ExternalUriPolicy.TryOpenExternal(null));
    }
}
