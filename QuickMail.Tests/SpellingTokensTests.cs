using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #902: web and email addresses are not misspellings. The pure decision behind every compose
/// spelling feature that skips them.
/// </summary>
public class SpellingTokensTests
{
    [Theory]
    [InlineData("https://contoso.example/plan")]
    [InlineData("http://contoso.example")]
    [InlineData("HTTPS://CONTOSO.EXAMPLE/PLAN")]
    [InlineData("ftp://files.contoso.example/report.pdf")]
    [InlineData("www.contoso.example")]
    [InlineData("WWW.contoso.example/plan?id=3&x=y")]
    [InlineData("mailto:kelly@contoso.example")]
    [InlineData("kelly@contoso.example")]
    [InlineData("first.last+news@mail.contoso.example")]
    [InlineData("o'brien@contoso.example")]
    // Punctuation around an address in running text.
    [InlineData("(https://contoso.example/plan).")]
    [InlineData("<kelly@contoso.example>,")]
    [InlineData("\"www.contoso.example\"")]
    [InlineData("https://contoso.example/plan?")]
    // A Markdown link is one whitespace-free token.
    [InlineData("[plan](https://contoso.example/plan)")]
    public void Addresses_AreRecognised(string token)
        => Assert.True(SpellingTokens.IsWebOrEmailAddress(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("recieve")]
    [InlineData("contoso")]
    [InlineData("contoso.example")]   // a bare domain looks too much like "end.Start" of a sentence
    [InlineData("awww.nice")]         // www. inside a word is not an address
    [InlineData("https")]
    [InlineData("https:")]
    [InlineData("https://")]
    [InlineData("mailto:")]
    [InlineData("www.")]
    [InlineData("@kelly")]
    [InlineData("kelly@")]
    [InlineData("kelly@contoso")]     // no dot in the domain
    [InlineData("a@b@c.example")]
    [InlineData("(recieve).")]
    public void OtherWords_AreNot(string? token)
        => Assert.False(SpellingTokens.IsWebOrEmailAddress(token));
}
