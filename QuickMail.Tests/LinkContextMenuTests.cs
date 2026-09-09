using System.IO;
using System.Text.RegularExpressions;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #671: Shift+F10 and the Applications key did nothing on a link in a message body. A link
/// could be opened with Enter, but its destination could not be copied — or checked without going
/// there.
///
/// <para>
/// The menu is Chromium's own, curated. A WPF <c>ContextMenu</c> was tried first and does not work
/// over a WebView2: it opens and takes keyboard focus but never enters menu mode, so arrow keys go
/// unhandled and a screen reader has the popup's name to announce and no item to read. That was
/// found by using the app, not by tests — an earlier version of this file passed in full against
/// that menu, because building a menu correctly and a menu being usable are different things.
/// </para>
///
/// <para>
/// So the decisions worth pinning were carved out as pure functions, the way
/// <see cref="QuickMail.Helpers.ContextMenuFocusPolicy"/> was: which link the menu is offered for,
/// what a <c>mailto:</c> yields, and who owns an Escape. What remains as source-text assertions is
/// only the wiring, which needs a live browser to exercise.
/// </para>
/// </summary>
public class LinkContextMenuTests
{
    // ── The security gate, a pure function and tested as one ────────────────────────────────────

    /// <summary>
    /// The href comes from untrusted message content, so the scheme is gated by the same policy
    /// that governs activating a link. A grep for the call used to stand in for this test, and a
    /// mutation run showed it survived the gate being neutered — the call was still in the source,
    /// it just no longer decided anything.
    /// </summary>
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("search-ms:query=secrets")]
    [InlineData("ms-msdt:/id")]
    [InlineData("data:text/html,<script>")]
    // The event card's RSVP anchors are internal; the User Guide promises they get no menu.
    [InlineData("quickmail:ics-accept")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void NoMenuIsOfferedForADestinationOutsideTheAllowList(string? href)
        => Assert.Null(LinkContextMenuSupport.LinkFor(true, href, false, null));

    [Theory]
    [InlineData("https://example.com/x")]
    [InlineData("http://example.com/x")]
    [InlineData("mailto:someone@example.com")]
    public void AnAllowListedDestinationIsOffered(string href)
        => Assert.Equal(href, LinkContextMenuSupport.LinkFor(true, href, false, null)?.Href);

    /// <summary>No link under the gesture at all — ordinary body text.</summary>
    [Fact]
    public void WithNoLinkUri_NothingIsOffered()
        => Assert.Null(LinkContextMenuSupport.LinkFor(false, "https://example.com", true, "text"));

    [Fact]
    public void TheDisplayTextIsCollapsedOnTheWayThrough()
        => Assert.Equal("Read the notice",
            LinkContextMenuSupport.LinkFor(true, "https://example.com", true, "Read\r\n  the notice")?.Text);

    // ── mailto: recipients ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Only the address is taken. A mailto may also carry subject and body parameters, and those
    /// are message content trying to decide what the user's own message says.
    /// </summary>
    [Theory]
    [InlineData("mailto:someone@example.com", "someone@example.com")]
    [InlineData("mailto:someone@example.com?subject=Urgent&body=Send%20money", "someone@example.com")]
    // Uri.TryCreate cannot parse a percent-encoded @ in a mailto, so ExternalUriPolicy refuses the
    // whole link and no menu is offered — the same refusal that stops Enter opening it. Verified
    // against the runtime rather than assumed; the first version of this test expected "a@b.com".
    [InlineData("mailto:a%40b.com", null)]
    [InlineData("https://example.com/x", null)]
    [InlineData("quickmail:ics-accept", null)]
    [InlineData("mailto:", null)]
    [InlineData(null, null)]
    public void MailtoRecipient_TakesTheAddressAndNothingElse(string? href, string? expected)
        => Assert.Equal(expected, LinkContextMenuSupport.MailtoRecipient(href));

    /// <summary>A header-injection attempt must not reach the compose window.</summary>
    [Fact]
    public void MailtoRecipient_RejectsAnEmbeddedNewline()
        => Assert.Null(LinkContextMenuSupport.MailtoRecipient("mailto:a@b.com%0ABcc:victim@example.com"));

    // ── Which text is worth offering separately from the address ────────────────────────────────

    [Theory]
    // An auto-linked plain-text URL displays its own address; two items that copy the same string
    // are noise to read past, not a second choice.
    [InlineData("https://example.com/x", "https://example.com/x", false)]
    [InlineData("https://example.com/x", "HTTPS://EXAMPLE.COM/X", false)]
    [InlineData("mailto:a@b.com", "a@b.com", false)]
    [InlineData("https://example.com/x", "", false)]
    [InlineData("https://example.com/x", "   ", false)]
    // Text differing from the destination is exactly when it earns its place — it is also the tell
    // for a link that does not go where it says it does.
    [InlineData("https://example.com/x", "Click here", true)]
    [InlineData("https://evil.example/x", "https://bank.example", true)]
    public void HasDistinctText_TracksWhetherTheTextAddsAnything(string href, string text, bool expected)
        => Assert.Equal(expected,
            LinkContextMenuSupport.HasDistinctText(new LinkContextMenuSupport.Link(href, text)));

    /// <summary>Link text wraps in a rendered message and arrives carrying newlines.</summary>
    [Theory]
    [InlineData("Read\r\n  the notice", "Read the notice")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Collapse_FlattensWrappedLinkText(string? raw, string expected)
        => Assert.Equal(expected, LinkContextMenuSupport.Collapse(raw));

    /// <summary>The log records the scheme only — a link in a message carries tracking parameters.</summary>
    [Theory]
    [InlineData("https://example.com/x?utm_source=secret", "https")]
    [InlineData("mailto:a@b.com", "mailto")]
    [InlineData("not a uri", "unknown")]
    public void SchemeOf_NamesTheSchemeAndNothingElse(string href, string expected)
        => Assert.Equal(expected, LinkContextMenuSupport.SchemeOf(href));

    // ── The Escape claim ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One-shot: the menu takes the Escape that dismisses it, and only that one. The next press is
    /// the user closing the message and must not be swallowed.
    /// </summary>
    [Fact]
    public void TheEscapeClaim_IsTakenOnceAndOnlyOnce()
    {
        var state = new LinkContextMenuSupport.LinkMenuState();
        Assert.False(state.TryConsumeEscape());

        state.MenuShownForTests();
        Assert.True(state.TryConsumeEscape());
        Assert.False(state.TryConsumeEscape());
    }

    /// <summary>
    /// Released when the menu's message goes away. Without this, a menu dismissed by a click leaves
    /// a claim that outlives the message and eats an Escape pressed much later, from anywhere in the
    /// window, with nothing to connect it to a link.
    /// </summary>
    [Fact]
    public void TheEscapeClaim_IsDroppedWhenReleased()
    {
        var state = new LinkContextMenuSupport.LinkMenuState();
        state.MenuShownForTests();
        state.Released();

        Assert.False(state.TryConsumeEscape());
    }

    // ── The wiring, which needs a live browser to exercise ──────────────────────────────────────

    /// <summary>
    /// Both message-body surfaces must attach the menu. They each build their own WebView2 and
    /// diverge otherwise, which is exactly how issue #483 came to be fixed in one and not the other.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void EveryMessageBodySurface_AttachesTheMenu(string codeBehind)
    {
        var source = Source(Path.Combine("Views", codeBehind));
        Assert.Contains("LinkContextMenuSupport.Attach(", source, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The event is not raised at all when the default menus are off, so this setting is load
    /// bearing — and it reads like the opposite of what the codebase used to want. Chromium's own
    /// items never reach the user: the handler clears the collection before adding ours.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void EveryMessageBodySurface_LeavesDefaultContextMenusEnabled(string codeBehind)
    {
        // Whitespace-normalised: written without spaces, a literal assertion passes silently while
        // the setting is off and the event stops being raised at all.
        var normalised = Regex.Replace(Source(Path.Combine("Views", codeBehind)), @"\s+", " ");

        Assert.Contains("AreDefaultContextMenusEnabled = true", normalised, System.StringComparison.Ordinal);
        Assert.DoesNotContain("AreDefaultContextMenusEnabled = false", normalised,
                              System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The collection is cleared before anything is added, and cleared FIRST — before the target is
    /// read. Those reads touch untrusted-content state, and a throw between them would otherwise
    /// leave the handler unhandled with Chromium's default items intact.
    /// </summary>
    [Fact]
    public void ChromiumsOwnItems_AreClearedBeforeAnythingElseHappens()
    {
        var normalised = Regex.Replace(Source(Path.Combine("Views", "LinkContextMenuSupport.cs")),
                                       @"\s+", " ");

        var clear = normalised.IndexOf("args.MenuItems.Clear();", System.StringComparison.Ordinal);
        var read  = normalised.IndexOf("LinkOf(args.ContextMenuTarget)", System.StringComparison.Ordinal);
        var add   = normalised.IndexOf("args.MenuItems.Add(", System.StringComparison.Ordinal);

        Assert.True(clear >= 0, "the default menu items are no longer cleared");
        Assert.True(clear < read, "the target is read before the default items are cleared");
        Assert.True(clear < add,  "items are added before the default ones are cleared");
    }

    /// <summary>
    /// A gesture that is not on an allow-listed link shows nothing, rather than falling back to
    /// Chromium's menu, which requires Handled on that path.
    /// </summary>
    [Fact]
    public void WithNoLinkUnderTheGesture_NoMenuIsShown()
    {
        var normalised = Regex.Replace(Source(Path.Combine("Views", "LinkContextMenuSupport.cs")),
                                       @"\s+", " ");

        var branch   = normalised.IndexOf("if (link is null)", System.StringComparison.Ordinal);
        var handled  = normalised.IndexOf("args.Handled = true;", branch, System.StringComparison.Ordinal);
        var firstAdd = normalised.IndexOf("args.MenuItems.Add(", System.StringComparison.Ordinal);

        Assert.True(branch >= 0, "the no-link branch is gone");
        Assert.True(handled > branch, "the no-link branch no longer sets Handled");
        Assert.True(handled < firstAdd, "Handled is set after items are added, not on the empty path");
    }

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail",
                                      relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        // AppContext.BaseDirectory rather than the current directory: runner-independent.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
