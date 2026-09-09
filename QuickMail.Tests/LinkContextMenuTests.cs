using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// A header-injection attempt must not reach the compose window. The input matters: the
    /// obvious one, mailto:a@b.com%0ABcc:..., carries two @ signs, so it fails to parse and is
    /// refused by the allow-list before the newline guard is ever reached. A review showed the
    /// old test passed with that guard deleted. These parse, so only the guard can stop them.
    /// </summary>
    [Theory]
    [InlineData("mailto:a%0Ab@example.com")]
    [InlineData("mailto:a%0D%0Ab@example.com")]
    public void MailtoRecipient_RejectsAnEmbeddedNewlineThatWouldOtherwiseParse(string href)
    {
        // Guard the guard: if this stops parsing, the test silently stops testing anything.
        Assert.True(System.Uri.TryCreate(href, System.UriKind.Absolute, out _),
                    "input no longer parses, so it no longer exercises the newline guard");

        Assert.Null(LinkContextMenuSupport.MailtoRecipient(href));
    }

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

        state.MenuShown();
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
        state.MenuShown();
        state.Released();

        Assert.False(state.TryConsumeEscape());
    }

    // ── The in-document status region ───────────────────────────────────────────────────────────

    /// <summary>
    /// The handle is OWNED, not merely named. Every element with an id becomes a property of
    /// <c>window</c> under HTML's named-access rules, with no script involved — so a message
    /// carrying <c>&lt;div id="__qmLinkStatus" hidden&gt;</c> would be handed the write, and a hidden
    /// element announces nothing. Two earlier versions of this feature were exploitable that way,
    /// the second while its comment asserted the CSP made it safe.
    ///
    /// What makes it safe is the explicit assignment (an own data property shadows named access)
    /// plus the <c>__qmOwned</c> expando, which message HTML cannot create. Asserting the absence of
    /// <c>getElementById</c> pins none of that — the exploitable version had no getElementById.
    /// </summary>
    [Fact]
    public void TheStatusRegion_HandleIsOwnedNotJustNamed()
    {
        Assert.Contains("window.__qmLinkStatus=d", LinkContextMenuSupport.StatusRegionScript,
                        System.StringComparison.Ordinal);
        Assert.Contains("d.__qmOwned=1", LinkContextMenuSupport.StatusRegionScript,
                        System.StringComparison.Ordinal);

        // And the write refuses anything it does not own.
        Assert.Contains("s.__qmOwned!==1", LinkContextMenuSupport.TextScript("x"),
                        System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Liveness and text are two separate steps, and the host runs them in that order: a region made
    /// live in the same pass as its content is not reliably announced, which is why the region is
    /// created at document load in the first place.
    /// </summary>
    [Fact]
    public void LivenessAndTextAreSeparateSteps()
    {
        Assert.Contains("setAttribute('aria-live'", LinkContextMenuSupport.LiveScript(live: true),
                        System.StringComparison.Ordinal);
        Assert.Contains("removeAttribute('aria-live'", LinkContextMenuSupport.LiveScript(live: false),
                        System.StringComparison.Ordinal);

        var text = LinkContextMenuSupport.TextScript("Could not copy the link address.");
        Assert.DoesNotContain("aria-live", text, System.StringComparison.Ordinal);
        Assert.Contains("QuickMail: Could not copy the link address.", text,
                        System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A clear is never made live — the hosts pass live:false for empty text — and writes no prefix.
    /// Removing the text is the retraction; an empty string is the case Announce refuses outright.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void AClear_IsNeverLive(string codeBehind)
    {
        var source = Source(Path.Combine("Views", codeBehind));
        Assert.Contains("text.Length > 0 && AccessibilityHelper.WouldAnnounce", source,
                        System.StringComparison.Ordinal);

        Assert.DoesNotContain("QuickMail:", LinkContextMenuSupport.TextScript(string.Empty),
                              System.StringComparison.Ordinal);
    }
    /// <summary>
    /// The region is in the document, and live, from load — a region inserted and made live in the
    /// same pass as its text is not reliably announced. The write only changes text (and toggles
    /// aria-live off when the user has results announcements disabled).
    /// </summary>
    [Fact]
    public void TheStatusRegion_IsLiveFromCreation()
    {
        Assert.Contains("aria-live", LinkContextMenuSupport.StatusRegionScript,
                        System.StringComparison.Ordinal);
        Assert.DoesNotContain("createElement", LinkContextMenuSupport.TextScript("x"),
                              System.StringComparison.Ordinal);
    }
    // -- The menu itself: labels, order, and what each item does --------------------------------

    /// <summary>
    /// The menu, in order, for a web link whose wording differs from its address. Three documents
    /// promise this order and nothing held it while composition lived inside the event handler,
    /// where reordering the calls left every test green.
    /// </summary>
    [Fact]
    public void AWebLink_GetsOpenThenCopyAddressThenCopyText()
        => Assert.Equal(new[] { "Open", "Copy Address", "Copy Text" },
            LinkContextMenuSupport.ItemsFor(new LinkContextMenuSupport.Link(
                "https://example.com/x", "Click here")).Select(i => i.Label));

    /// <summary>A link whose text is its own address drops Copy Text.</summary>
    [Fact]
    public void ALinkThatIsItsOwnAddress_DropsCopyText()
        => Assert.Equal(new[] { "Open", "Copy Address" },
            LinkContextMenuSupport.ItemsFor(new LinkContextMenuSupport.Link(
                "https://example.com/x", "https://example.com/x")).Select(i => i.Label));

    /// <summary>
    /// New Message to This Address is LAST, so the items before it keep fixed positions on every
    /// link. It used to be second, which made Down-Down-Enter copy an address on a web link and
    /// open a compose window on a mailto one.
    /// </summary>
    [Fact]
    public void AMailtoLink_PutsNewMessageLast()
        => Assert.Equal(new[] { "Open", "Copy Address", "New Message to This Address" },
            LinkContextMenuSupport.ItemsFor(new LinkContextMenuSupport.Link(
                "mailto:someone@example.com", "someone@example.com")).Select(i => i.Label));

    /// <summary>
    /// A successful copy reports empty text, which clears any earlier failure, and never a
    /// confirmation. Asserting one literal string missed rewordings; this drives the real action.
    /// </summary>
    [Fact]
    public void ASuccessfulCopy_ReportsNothingButClears()
    {
        var clipboard = new FakeClipboard();
        var reported = new List<string>();

        Run("Copy Address", "https://example.com/x", clipboard, reported.Add);

        Assert.Equal("https://example.com/x", clipboard.Text);
        Assert.Equal(new[] { string.Empty }, reported);
    }

    /// <summary>A failed copy reports, and the text names what failed.</summary>
    [Fact]
    public void AFailedCopy_Reports()
    {
        var clipboard = new FakeClipboard { Fails = true };
        var reported = new List<string>();

        Run("Copy Address", "https://example.com/x", clipboard, reported.Add);

        Assert.Equal(new[] { "Could not copy the link address." }, reported);
    }

    /// <summary>Copy Address copies the ADDRESS. Swapping it for the text defeats the point.</summary>
    [Fact]
    public void CopyAddress_CopiesTheAddressNotTheText()
    {
        var clipboard = new FakeClipboard();
        Run("Copy Address", "https://evil.example/x", clipboard, _ => { }, "https://bank.example");

        Assert.Equal("https://evil.example/x", clipboard.Text);
    }

    [Fact]
    public void CopyText_CopiesTheText()
    {
        var clipboard = new FakeClipboard();
        Run("Copy Text", "https://evil.example/x", clipboard, _ => { }, "https://bank.example");

        Assert.Equal("https://bank.example", clipboard.Text);
    }

    /// <summary>Records what was copied. No Windows clipboard, no apartment requirement.</summary>
    private sealed class FakeClipboard : QuickMail.Services.IClipboardService
    {
        public string Text { get; private set; } = string.Empty;
        public bool Fails { get; init; }
        public bool SetText(string text) { if (Fails) return false; Text = text; return true; }
        public string GetText() => Text;
    }

    private static void Run(string label, string href, FakeClipboard clipboard,
                            System.Action<string> report, string text = "Click here")
    {
        var entry = LinkContextMenuSupport.ItemsFor(new LinkContextMenuSupport.Link(href, text))
                                          .Single(i => i.Label == label);
        entry.Run(new LinkContextMenuSupport.MenuContext(clipboard, report, _ => { }));
    }

    /// <summary>
    /// The text is JSON-escaped and assigned to textContent, so a string that ever became
    /// attacker-influenced could not break out of the script or inject markup.
    /// </summary>
    [Fact]
    public void TheStatusRegion_EscapesItsText()
    {
        var script = LinkContextMenuSupport.TextScript("</script><img src=x onerror=alert(1)>");

        Assert.DoesNotContain("</script>", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, System.StringComparison.Ordinal);
        Assert.Contains("textContent", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The status bar is shared — message counts, sync progress, connection state. A link-menu
    /// failure is mirrored there through the SILENT setter (a plain StatusText assignment raises its
    /// own announcement, in the Status category), and a later success clears only what this feature
    /// put there. Clearing unconditionally destroyed whatever a sync had just reported, every time a
    /// copy worked.
    /// </summary>
    [Fact]
    public void TheStatusBar_IsSetSilentlyAndClearedOnlyWhenItIsOurs()
    {
        var source = Source(Path.Combine("Views", "MainWindow.xaml.cs"));
        var report = source[source.IndexOf("private void UpdateLinkMenuStatusBar",
                                           System.StringComparison.Ordinal)..];
        report = report[..report.IndexOf("\n    }", System.StringComparison.Ordinal)];

        Assert.Contains("SetStatusWithoutSpeaking", report, System.StringComparison.Ordinal);
        // A single = would be a direct assignment, which announces in the Status category. The
        // trailing space keeps this from matching the == comparison in the clear below.
        Assert.DoesNotContain("_vm.StatusText = ", report, System.StringComparison.Ordinal);

        // The clear is conditional on the standing text still being ours.
        Assert.Contains("_vm.StatusText == mine", report, System.StringComparison.Ordinal);
    }
    /// <summary>
    /// Both surfaces must drop the claim when the message a menu belonged to goes away. MainWindow
    /// did and MessageWindow did not, which left a dismissed menu able to swallow the Escape meant
    /// to close the window after navigating on with Alt+Left / Alt+Right.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void EveryMessageBodySurface_ReleasesTheClaimWhenTheMessageChanges(string codeBehind)
    {
        var source = Source(Path.Combine("Views", codeBehind));
        Assert.Contains("Released()", source, System.StringComparison.Ordinal);
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
        // Whitespace-normalised so the assertions survive reformatting; the ordering below is what
        // actually holds the invariant, and it fails loudly if a write is removed.
        var normalised = Regex.Replace(Source(Path.Combine("Views", codeBehind)), @"\s+", " ");

        // It must END true — the event is not raised otherwise — but it is turned off first so the
        // window before the handler is attached is closed rather than at the SDK default of true.
        var off = normalised.IndexOf("AreDefaultContextMenusEnabled = false", System.StringComparison.Ordinal);
        var attach = normalised.IndexOf("LinkContextMenuSupport.Attach(", System.StringComparison.Ordinal);
        var on = normalised.IndexOf("AreDefaultContextMenusEnabled = true", System.StringComparison.Ordinal);

        Assert.True(off >= 0, "the setting is never turned off, so the pre-attach window is open");
        Assert.True(off < attach, "the setting is turned off after the handler is attached");
        Assert.True(attach < on, "the setting is turned on before the handler is attached");
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

    /// <summary>
    /// The gate asks about the RESULT category — a copy outcome is an action result, not background
    /// progress. Hardcoding true, or asking about Status, would silently ignore the setting the
    /// user actually set for this kind of message.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void TheSpokenDelivery_FollowsTheResultSetting(string codeBehind)
    {
        var source = Source(Path.Combine("Views", codeBehind));
        Assert.Contains("WouldAnnounce(AnnouncementCategory.Result)", source,
                        System.StringComparison.Ordinal);
    }


    /// <summary>
    /// The four-item case, which is what the fixture message actually produces: a mailto link whose
    /// wording differs from its address. No test covered it, so swapping the two conditional blocks
    /// — putting New Message before Copy Text — stayed green.
    /// </summary>
    [Fact]
    public void AMailtoLinkWithDistinctText_GetsAllFourInOrder()
        => Assert.Equal(new[] { "Open", "Copy Address", "Copy Text", "New Message to This Address" },
            LinkContextMenuSupport.ItemsFor(new LinkContextMenuSupport.Link(
                "mailto:ava@example.com", "Email Ava")).Select(i => i.Label));

    /// <summary>Copy Text names the text, not the address, when it fails.</summary>
    [Fact]
    public void AFailedCopyText_NamesTheText()
    {
        var reported = new List<string>();
        Run("Copy Text", "https://example.com/x", new FakeClipboard { Fails = true },
            reported.Add, "Click here");

        Assert.Equal(new[] { "Could not copy the link text." }, reported);
    }


    /// <summary>
    /// The DOM primitives are captured before any sender markup is parsed, and used from the capture
    /// — not re-read off document at write time. An unclosed &lt;form name="createElement"&gt; survives the
    /// sanitizer and clobbers document.createElement by named access, which would throw before the
    /// ownership assignment and silently suppress the failure report.
    /// </summary>
    [Fact]
    public void TheStatusRegion_UsesCapturedDomPrimitives()
    {
        var script = LinkContextMenuSupport.StatusRegionScript;
        var prologue = script[..script.IndexOf("addEventListener", System.StringComparison.Ordinal)];
        var body = script[script.IndexOf("addEventListener", System.StringComparison.Ordinal)..];

        Assert.Contains("document.createElement.bind(document)", prologue, System.StringComparison.Ordinal);
        Assert.Contains("Node.prototype.appendChild", prologue, System.StringComparison.Ordinal);

        // After the capture, nothing goes back to the live document for them.
        Assert.DoesNotContain("document.createElement(", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain(".appendChild(", body, System.StringComparison.Ordinal);
    }

    /// <summary>Open retracts on success too, so a retry retires the earlier notice.</summary>
    [Fact]
    public void Open_ReportsEmptyOnSuccess()
    {
        var reported = new List<string>();
        var entry = LinkContextMenuSupport.ItemsFor(
            new LinkContextMenuSupport.Link("https://example.com/x", "Click here"))
            .Single(i => i.Label == "Open");

        entry.Run(new LinkContextMenuSupport.MenuContext(new FakeClipboard(), reported.Add, _ => { }));

        // Either outcome is a report; what must never happen is silence on one and text on the other.
        Assert.Single(reported);
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
