using System;
using System.Text.RegularExpressions;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The second pass of the #728 security review, 2026-09-18. Removing a structural tag AFTER the
/// sanitizer's final escape rejoined forbidden tags, and <c>IsUserInitiated</c> turned out not to prove
/// a navigation was the user's. The inputs below are the reviewer's working ones.
/// </summary>
public class MessageBodyNavigationSafetyTests
{
    // ── The sanitizer ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("<me<body>ta http-equiv=\"refresh\" content=\"0;url=https://evil.example/\">", "meta")]
    [InlineData("<li<html>nk rel=preconnect href=https://tracker.example>", "link")]
    [InlineData("<sty<body>le>header{display:none}</style>", "style")]
    [InlineData("<me<title>x</title>ta http-equiv=\"refresh\" content=\"0;url=https://evil.example/\">", "meta")]
    [InlineData("<sty</head>le>header{display:none}", "style")]
    [InlineData("<p>x</p><title>everything after this", "title")]
    public void AStructuralTagRemovalCannotRejoinAForbiddenTag(string hostile, string tag)
    {
        var live = new Regex($"<{tag}(?=[\\s/>]|$)", RegexOptions.IgnoreCase);

        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedBodyFragment(hostile, out var fragment));
        Assert.DoesNotMatch(live, fragment);

        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument("s", hostile, out var document));
        var body = document[(document.IndexOf("<body", StringComparison.Ordinal) + 5)..];
        Assert.DoesNotMatch(live, body);
    }

    [Fact]
    public void AFullHtmlDocumentFromASender_StillRendersItsBody()
    {
        const string html = "<html><head><title>Newsletter</title><style>p{color:red}</style></head>" +
                            "<body><p>Hello</p></body></html>";
        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedBodyFragment(html, out var fragment));
        Assert.Equal("<p>Hello</p>", fragment);
    }

    /// <summary>An attribute join nested <paramref name="depth"/> deep: each round unwraps one level.</summary>
    private static string NestedStyle(int depth)
    {
        var inner = " style=\"x\"";
        for (var i = 0; i < depth; i++) inner = " s" + inner + "tyle=\"y\"";
        return "<p" + inner + ">text</p>";
    }

    [Fact]
    public void MarkupThatOutlastsTheRounds_FailsClosed()
    {
        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedBodyFragment(NestedStyle(1), out var shallow));
        Assert.DoesNotContain("style", shallow);

        Assert.False(MessageBodyHtmlBuilder.TryBuildSanitizedBodyFragment(NestedStyle(8), out _));
        Assert.False(MessageBodyHtmlBuilder.TryBuildSanitizedHtmlDocument("s", NestedStyle(8), out _));
    }

    [Fact]
    public void MarkupThatOutlastsTheRounds_IsSavedAsText_WithNoAttributes()
    {
        var detail = new MailMessageDetail { Subject = "s", HtmlBody = NestedStyle(8) };
        var page = MessageExport.BuildHtmlDocument(detail, new MessageSaveContext("a", "Inbox", null, DateTimeOffset.Now));
        var main = page[page.IndexOf("<main>", StringComparison.Ordinal)..];
        Assert.DoesNotContain("style=", main);
        Assert.Contains("could not be kept", main);
    }

    [Theory]
    [InlineData("<a href=\"https://example.com\" ping=\"https://tracker.example/\">x</a>", "ping")]
    [InlineData("<a href=\"https://example.com\" formaction=\"https://x\">x</a>", "formaction")]
    [InlineData("<div srcdoc=\"x\">x</div>", "srcdoc")]
    public void TrackingAndFrameAttributesAreStripped(string html, string attribute)
    {
        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedBodyFragment(html, out var fragment));
        Assert.DoesNotContain(attribute, fragment);
    }

    // ── QuickMail's own links ─────────────────────────────────────────────────

    [Fact]
    public void OwnLinks_RoundTrip_AndCarryThisRunsToken()
    {
        var link = QuickMailLinks.Build("ics-accept");
        Assert.StartsWith("quickmail:ics-accept?t=", link);
        Assert.True(QuickMailLinks.TryParse(link, out var action));
        Assert.Equal("ics-accept", action);
    }

    [Theory]
    [InlineData("quickmail:ics-accept")]
    [InlineData("quickmail:ics-accept?t=00000000000000000000000000000000")]
    [InlineData("quickmail:ics-accept?t=")]
    [InlineData("QUICKMAIL:ics-decline")]
    [InlineData("https://example.com/?t=x")]
    [InlineData(null)]
    public void ALinkQuickMailDidNotMake_DoesNothing(string? uri) =>
        Assert.False(QuickMailLinks.TryParse(uri, out _));

    [Fact]
    public void TheInvitationCard_UsesTokenedLinks()
    {
        var card = EventCardHtmlBuilder.Build(new IcsModel
        {
            Summary = "Planning", Method = "REQUEST",
            StartTime = new DateTime(2026, 9, 20, 10, 0, 0), EndTime = new DateTime(2026, 9, 20, 11, 0, 0),
        }, null);
        Assert.Contains($"href=\"{QuickMailLinks.Build("ics-accept")}\"", card);
        Assert.DoesNotContain("href=\"quickmail:ics-accept\"", card);
    }

    // ── The activation gate ───────────────────────────────────────────────────

    private sealed class Clock
    {
        public DateTime Now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public void ANavigationWithNoActivation_NeverOpens()
    {
        // A meta refresh that WebView2 reports as user-initiated because the reader pressed an arrow key.
        var clock = new Clock();
        var gate = new LinkActivationGate(() => clock.Now);
        var opened = 0;
        gate.Request("https://evil.example/", () => opened++);
        clock.Now += TimeSpan.FromSeconds(5);
        gate.NoteActivated("https://evil.example/");   // too late to pair: that one opens nothing either
        Assert.Equal(0, opened);
    }

    [Fact]
    public void AnActivatedLink_Opens_WhicheverSignalArrivesFirst()
    {
        var clock = new Clock();
        var gate = new LinkActivationGate(() => clock.Now);
        var opened = 0;

        gate.NoteActivated("https://example.com/a");
        gate.Request("https://example.com/a", () => opened++);
        Assert.Equal(1, opened);

        gate.Request("https://example.com/b", () => opened++);
        clock.Now += TimeSpan.FromMilliseconds(300);
        gate.NoteActivated("https://example.com/b");
        Assert.Equal(2, opened);
    }

    [Fact]
    public void AnActivationOnlyOpensItsOwnLink_AndOnlyOnce()
    {
        var clock = new Clock();
        var gate = new LinkActivationGate(() => clock.Now);
        var opened = 0;

        gate.NoteActivated("https://example.com/real");
        gate.Request("https://evil.example/", () => opened++);
        Assert.Equal(0, opened);

        gate.NoteActivated("https://example.com/real");
        gate.Request("https://example.com/real", () => opened++);
        gate.Request("https://example.com/real", () => opened++);   // a second navigation, no new activation
        Assert.Equal(1, opened);
    }

    [Fact]
    public void AStaleActivation_DoesNotPair()
    {
        var clock = new Clock();
        var gate = new LinkActivationGate(() => clock.Now);
        var opened = 0;
        gate.NoteActivated("https://example.com/a");
        clock.Now += LinkActivationGate.Window + TimeSpan.FromMilliseconds(1);
        gate.Request("https://example.com/a", () => opened++);
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData("https://Example.com/a", "https://example.com/a")]
    [InlineData("https://example.com", "https://example.com/")]
    public void UrlsAreComparedAsTheEngineNormalizesThem(string href, string navigated) =>
        Assert.True(LinkActivationGate.Same(href, navigated));
}
