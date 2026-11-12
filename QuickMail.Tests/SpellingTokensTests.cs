using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #902: the pieces of web and email addresses are not misspellings. The pure decision behind
/// every compose spelling feature that skips them.
/// </summary>
public class SpellingTokensTests
{
    [Theory]
    [InlineData("https://contoso.example/plan", 0)]
    [InlineData("http://contoso.example", 0)]
    [InlineData("HTTPS://CONTOSO.EXAMPLE/PLAN", 0)]
    [InlineData("ftp://files.contoso.example/report.pdf", 0)]
    [InlineData("www.contoso.example", 0)]
    [InlineData("WWW.contoso.example/plan?id=3&x=y", 0)]
    [InlineData("mailto:kelly@contoso.example", 0)]
    [InlineData("kelly@contoso.example", 0)]
    [InlineData("first.last+news@mail.contoso.example", 0)]
    [InlineData("o'brien@contoso.example", 0)]
    // A dotted host name followed by a path, without a scheme.
    [InlineData("github.com/kellylford/QuickMail", 0)]
    [InlineData("docs.contoso.example/plan", 0)]
    // Punctuation around an address in running text.
    [InlineData("(https://contoso.example/plan).", 1)]
    [InlineData("<kelly@contoso.example>,", 1)]
    [InlineData("\"www.contoso.example\"", 1)]
    [InlineData("(github.com/kellylford)", 1)]
    // A Markdown link: the address starts after "](".
    [InlineData("[plan](https://contoso.example/plan)", 7)]
    // A word run up against an address.
    [InlineData("teh(https://contoso.example)", 4)]
    public void AddressStart_FindsWhereTheAddressBegins(string token, int expected)
        => Assert.Equal(expected, SpellingTokens.AddressStart(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("recieve")]
    [InlineData("contoso")]
    [InlineData("contoso.example")]   // a bare domain looks too much like a missing space: "end.Start"
    [InlineData("end.Start")]
    [InlineData("e.g./i.e.")]         // the last label before "/" must be two or more letters
    [InlineData("1.5/2")]
    [InlineData("and/or")]
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
    public void AddressStart_NoAddress(string? token)
        => Assert.Equal(-1, SpellingTokens.AddressStart(token));

    [Theory]
    // Errors inside the address are skipped…
    [InlineData("https://recieve.example/plan", 8, true)]
    [InlineData("kelly@pacakge.example", 6, true)]
    [InlineData("tomorow@contoso.example", 0, true)]
    [InlineData("github.com/recieve", 11, true)]
    [InlineData("[recieve](https://pacakge.example)", 18, true)]
    // …but not the text of a Markdown link, or a word run up against an address.
    [InlineData("[recieve](https://contoso.example)", 1, false)]
    [InlineData("pacakge](https://contoso.example)", 0, false)]
    [InlineData("teh(https://contoso.example)", 0, false)]
    [InlineData("recieve", 0, false)]
    public void IsInAddress_OnlyFromWhereTheAddressBegins(string token, int errorOffset, bool expected)
        => Assert.Equal(expected, SpellingTokens.IsInAddress(token, errorOffset));
}
