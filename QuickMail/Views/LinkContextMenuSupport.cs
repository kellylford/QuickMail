using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using QuickMail.Helpers;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// The link context menu for message-body documents — Shift+F10, the Applications key, or a
/// right-click on a link (issue #671).
///
/// <para>
/// The menu is Chromium's own, curated, rather than a WPF <c>ContextMenu</c> shown over the
/// WebView2. A WPF popup was tried first and does not work here: it opens and takes keyboard focus
/// but never enters menu mode over the WebView2's child HWND, so arrow keys go unhandled and a
/// screen reader is left with the popup's own name to announce and no item to read. The native menu
/// is a real menu with the platform's own accessibility.
/// </para>
///
/// <para>
/// This requires <c>AreDefaultContextMenusEnabled</c> to stay TRUE. That is an OBSERVED
/// requirement, not a documented one: the event is not raised with the setting off. Chromium's own
/// items never reach the user — the collection is cleared before ours are added — so Save as,
/// Inspect and Open link in new window are gone by construction rather
/// than by the setting. If a runtime update ever changed that, the failure would be silent, which
/// is why <c>LinkContextMenuTests</c> pins the setting on both surfaces.
/// </para>
///
/// <para>
/// A gesture that is not on an allow-listed link produces no menu at all, which is how body text
/// behaved before this feature and what issue #672 settled on. The event card's <c>quickmail:</c>
/// RSVP anchors are excluded by the same gate.
/// </para>
/// </summary>
public static class LinkContextMenuSupport
{
    /// <summary>
    /// Wires the link menu to a message-body WebView2. Call once per surface, after
    /// <c>EnsureCoreWebView2Async</c>. The returned state lets the host tell an Escape that
    /// dismisses this menu from one that means "close the message".
    /// </summary>
    /// <param name="composeTo">
    /// Opens QuickMail's own compose window addressed to a <c>mailto:</c> link's recipient. Without
    /// it such a link would only ever be handed to the OS default mail handler, which for most
    /// people is a different mail client.
    /// </param>
    public static LinkMenuState Attach(CoreWebView2 core, CoreWebView2Environment environment,
                                       IClipboardService clipboard, Action<string> reportFailure,
                                       Action<string> composeTo)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentNullException.ThrowIfNull(composeTo);

        var context = new MenuContext(clipboard, reportFailure, composeTo);
        var state = new LinkMenuState();

        core.ContextMenuRequested += (_, args) =>
        {
            // Cleared FIRST, and the rest wrapped, so any failure reading this untrusted-content
            // state ends with an empty handled menu rather than falling through to Chromium's
            // default one — the fail-closed posture the HTML sanitizer follows.
            args.MenuItems.Clear();

            try
            {
                var link = LinkOf(args.ContextMenuTarget);
                if (link is null)
                {
                    args.Handled = true;
                    return;
                }

                LogService.Debug($"LinkContextMenu: menu for a {SchemeOf(link.Href)} link");

                foreach (var entry in ItemsFor(link))
                    args.MenuItems.Add(Item(environment, state, entry.Label,
                        () => entry.Run(context)));

                // Taken only once every item exists. Set before the build, a throw from
                // CreateContextMenuItem would leave a claim standing with no menu to explain it,
                // and the next Escape anywhere would be swallowed.
                state.MenuShown();
            }
            catch (Exception ex)
            {
                LogService.Log("LinkContextMenu: building the menu failed", ex);
                state.Released();
                args.MenuItems.Clear();
                args.Handled = true;
            }
        };

        return state;
    }

    /// <summary>One entry in the menu: what it is called and what choosing it does.</summary>
    /// <summary>
    /// What a menu item needs to do its work. A record rather than two positional
    /// <c>Action&lt;string&gt;</c> parameters: those are the same type, so swapping them compiled, passed
    /// every test, and would have made a failed copy open a compose window addressed to
    /// "Could not copy the link address."
    /// </summary>
    public sealed record MenuContext(
        IClipboardService Clipboard,
        Action<string> Report,
        Action<string> ComposeTo);

    /// <summary>One entry in the menu: what it is called and what choosing it does.</summary>
    public sealed record MenuEntry(string Label, Action<MenuContext> Run);

    /// <summary>
    /// The menu for a link, in order. A pure function so the labels, the order, and which items a
    /// given link gets are all testable — three documents make promises about exactly that, and
    /// nothing held them while this lived inside the event handler.
    ///
    /// <para>
    /// <b>New Message to This Address</b> is LAST rather than second so the earlier items keep fixed
    /// positions on every link: Copy Address is item 2 whatever the link is, and the one item that
    /// moves the user out of the message is the one furthest from a mis-press.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MenuEntry> ItemsFor(Link link)
    {
        ArgumentNullException.ThrowIfNull(link);

        var items = new List<MenuEntry>
        {
            new("Open", ctx =>
                // Reports the one failure the user would otherwise see as nothing happening at
                // all — no browser registered, no mail handler, a shell error — and reports empty
                // on success so a retry retires the earlier notice, exactly as Copy does.
                ctx.Report(ExternalUriPolicy.TryOpenExternal(link.Href)
                    ? string.Empty
                    : "Could not open the link.")),
            new("Copy Address", ctx => Copy(ctx, link.Href, "Link address")),
        };

        if (HasDistinctText(link))
            items.Add(new("Copy Text", ctx => Copy(ctx, link.Text, "Link text")));

        if (MailtoRecipient(link.Href) is { } recipient)
            items.Add(new("New Message to This Address", ctx => ctx.ComposeTo(recipient)));

        return items;
    }

    /// <summary>A link under the context-menu gesture: its destination and its display text.</summary>
    public sealed record Link(string Href, string Text);

    /// <summary>Adapter over the SDK type, which cannot be constructed in a test.</summary>
    public static Link? LinkOf(CoreWebView2ContextMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return LinkFor(target.HasLinkUri, target.LinkUri, target.HasLinkText, target.LinkText);
    }

    /// <summary>
    /// The allow-listed link under the gesture, or null when there is none. The scheme is gated by
    /// <see cref="ExternalUriPolicy"/> because the href comes from untrusted message content — the
    /// same gate that governs activating a link. Kept a pure function so the decision can be
    /// unit-tested, as <see cref="ContextMenuFocusPolicy"/> is and for the same reason.
    /// </summary>
    public static Link? LinkFor(bool hasLinkUri, string? linkUri, bool hasLinkText, string? linkText)
    {
        if (!hasLinkUri) return null;
        if (!ExternalUriPolicy.IsAllowed(linkUri)) return null;

        return new Link(linkUri!, Collapse(hasLinkText ? linkText : null));
    }

    /// <summary>
    /// The recipient of a <c>mailto:</c> link, or null for any other scheme. Only the address is
    /// taken: a mailto may carry subject and body parameters, and those are message content
    /// deciding what a composed message says.
    /// </summary>
    public static string? MailtoRecipient(string? href)
    {
        if (!ExternalUriPolicy.IsAllowed(href)) return null;
        if (!href!.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return null;

        // Taken from the raw string rather than through Uri: for a mailto, .NET puts the address in
        // UserInfo and Host and leaves AbsolutePath empty, so the obvious reading is silently wrong.
        var address = href["mailto:".Length..];

        var separator = address.IndexOf('?');
        if (separator >= 0) address = address[..separator];

        address = Uri.UnescapeDataString(address).Trim();

        // A percent-encoded newline is a header-injection attempt; it must not reach compose.
        return address.Length == 0
            || address.Contains('\r') || address.Contains('\n')
            ? null
            : address;
    }

    /// <summary>
    /// True when the link's display text is worth offering separately from its destination. An
    /// auto-linked plain-text URL displays its own address, and two items that copy the same string
    /// are noise to read past rather than a second choice.
    /// </summary>
    public static bool HasDistinctText(Link link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrWhiteSpace(link.Text)) return false;
        if (string.Equals(link.Text, link.Href, StringComparison.OrdinalIgnoreCase)) return false;

        // "user@example.com" displayed for "mailto:user@example.com" is the same string twice.
        return !string.Equals("mailto:" + link.Text, link.Href, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whitespace-collapsed display text; wrapped link text arrives carrying newlines.</summary>
    internal static string Collapse(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The scheme alone, for logging — never the URL, which carries tracking parameters.</summary>
    internal static string SchemeOf(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.Scheme : "unknown";

    /// <summary>
    /// Creates the live region a copy failure is written into, on every document, as soon as the
    /// body exists. Appended to each surface's document-created script.
    ///
    /// <para>
    /// Created here rather than at write time because a live region that is inserted and made live
    /// in the same pass as its text is not reliably announced — the invite card's region (issue
    /// #329) is rendered empty into the document for the same reason. <c>aria-live</c> is set HERE
    /// too, so by the time any text is written the element has been a live region since load.
    /// </para>
    ///
    /// <para>
    /// What protects the handle is the explicit <c>window.__qmLinkStatus = d</c> assignment, NOT the
    /// sanitizer's CSP. Every element with an id becomes a property of <c>window</c> by HTML's named
    /// access rules, with no script involved — so a message carrying
    /// <c>&lt;div id="__qmLinkStatus" hidden&gt;</c> would otherwise be handed the write, and a hidden
    /// element is out of the accessibility tree, where <c>aria-live</c> announces nothing. An own
    /// data property shadows named access and cannot be re-clobbered by markup inserted later. The
    /// <c>__qmOwned</c> expando is the belt to that braces: message HTML can create an element, but
    /// not a JavaScript property on one, so a write can tell our region from a look-alike even if
    /// this script never ran.
    /// </para>
    ///
    /// <para>
    /// An earlier version looked the region up by element id, and the version after it trusted the
    /// <c>window</c> property without owning it. Both were the same defect wearing a different name.
    /// </para>
    /// </summary>
    public const string StatusRegionScript =
        // Every DOM primitive this needs is captured HERE, at document-created, before any sender
        // markup is parsed — and then only the captures are used.
        //
        // HTML named access lets an element claim a property of document: <object
        // name="createElement"> or an unclosed <form name="getElementsByTagName"> both survive the
        // sanitizer (its element rule needs a closing tag, and name is not stripped) and replace
        // the function. Calling one later throws, before the ownership assignment, and every
        // failure report after that is silently dropped — the exact outcome this region exists to
        // prevent. Capturing createElement but then reading getElementsByTagName off the live
        // document only moved the hole, which is how the second version of this shipped.
        //
        // The inline display/visibility are !important because the sanitizer's <style> rule needs
        // a literal </style>, and the tokenizer also accepts "</style >" — so a sender stylesheet
        // survives and [aria-live]{display:none} would take the region out of the accessibility
        // tree. An inline !important beats an author !important.
        "(function(){" +
        "var C=document.createElement.bind(document);" +
        "var G=document.getElementsByTagName.bind(document);" +
        "var A=Node.prototype.appendChild;var D=document;" +
        "D.addEventListener('DOMContentLoaded',function(){try{" +
        "var b=G('body')[0];if(!b)return;" +
        "var d=C('div');" +
        "d.setAttribute('aria-live','assertive');d.setAttribute('aria-atomic','true');" +
        "d.style.cssText='margin-top:12px;font-weight:600;'+" +
        "'display:block !important;visibility:visible !important';" +
        "d.__qmOwned=1;A.call(b,d);window.__qmLinkStatus=d;" +
        "}catch(e){window.__qmLinkStatusError=String(e);}});})();";

    /// <summary>
    /// Reports a copy FAILURE into that region. There is no success message by design: choosing a
    /// menu item is expected to do what it says, and announcing that it did is noise on every use to
    /// cover the rare case.
    ///
    /// <para>
    /// The TEXT is always written — it is content in the document, the same way status-bar text
    /// is content, and it stays there to be found. Whether the region is LIVE, and so spoken
    /// without being sought, follows the user's AnnounceResults preference like every other
    /// action outcome. Nothing here overrides a setting.
    /// </para>
    ///
    /// The text names QuickMail because the region sits after the sender's content: an unattributed
    /// line at the end of a message reads as something the sender wrote.
    /// </summary>
    /// <summary>
    /// Sets or removes the region's <c>aria-live</c>, following the user's AnnounceResults
    /// preference. Run as its OWN step, before the text: a region made live in the same pass as
    /// its content is not reliably announced, which is the whole reason the region is created at
    /// load rather than on demand. Toggling and writing together would have reintroduced that on
    /// the fail, retry-succeeds, fail-again path.
    /// </summary>
    public static string LiveScript(bool live) =>
        "(function(){var s=window.__qmLinkStatus;" +
        "if(!s||!s.isConnected||s.__qmOwned!==1)return;" +
        (live
            ? "s.setAttribute('aria-live','assertive');"
            : "s.removeAttribute('aria-live');") +
        "})();";

    /// <summary>
    /// Writes an outcome into the region. Empty text means the action succeeded and retires any
    /// notice an earlier failure left — success itself says nothing, because choosing a menu item
    /// is expected to do what it says.
    ///
    /// The text names QuickMail because the region sits after the sender's content: an
    /// unattributed line at the end of a message reads as something the sender wrote.
    /// </summary>
    public static string TextScript(string text) =>
        "(function(){var s=window.__qmLinkStatus;" +
        "if(!s||!s.isConnected||s.__qmOwned!==1)return;" +
        "s.textContent=" + JsonSerializer.Serialize(text.Length == 0 ? "" : "QuickMail: " + text) +
        ";})();";

    /// <summary>
    /// Copies, and reports only a failure. Success is deliberately silent: choosing a menu item is
    /// expected to do what it says, and confirming it every time is noise to cover the rare case.
    ///
    /// A success does clear any earlier failure, though — it writes empty text rather than a
    /// confirmation. Without that, a copy that failed and then succeeded on retry would leave the
    /// message ending in "Could not copy…", the only statement about the copy anywhere and no longer
    /// true. Silence has no other way to retract.
    /// </summary>
    /// <summary>
    /// Copies, and reports only a failure. Success is deliberately silent: choosing a menu item is
    /// expected to do what it says, and confirming it every time is noise to cover the rare case.
    ///
    /// A success does report EMPTY text, which retires any notice an earlier failure left. Without
    /// that, a copy that failed and then succeeded on retry would leave the message ending in
    /// "Could not copy…" — the only statement about the copy anywhere, and no longer true. Silence
    /// has no other way to retract.
    /// </summary>
    private static void Copy(MenuContext ctx, string text, string what)
    {
        ctx.Report(ctx.Clipboard.SetText(text)
            ? string.Empty
            : $"Could not copy the {what.ToLowerInvariant()}.");
    }

    private static CoreWebView2ContextMenuItem Item(CoreWebView2Environment environment,
                                                    LinkMenuState state,
                                                    string label, Action onSelected)
    {
        var item = environment.CreateContextMenuItem(label, null,
                                                     CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) =>
        {
            // The menu is gone once an item is chosen, so it has no claim on the next Escape.
            state.Released();
            onSelected();
        };
        return item;
    }

    /// <summary>
    /// Whether a link menu is up, so the Escape that dismisses it is not also read as "close the
    /// message".
    ///
    /// <para>
    /// WebView2 raises no "menu closed" event, so the claim is one-shot: taken when a menu is shown,
    /// released by an Escape or by activating an item. It must also be released whenever the thing
    /// the menu belonged to goes away — a new message rendering, the pane closing — or a menu
    /// dismissed some other way (a click elsewhere) leaves a claim that outlives the message and
    /// swallows an Escape pressed much later, from anywhere in the window, with nothing to connect
    /// it to a link. That is the same staleness class <see cref="ContextMenuFocusPolicy"/> had to
    /// close, and the hosts call <see cref="Released"/> on those transitions for the same reason.
    /// </para>
    /// </summary>
    public sealed class LinkMenuState
    {
        private bool _shown;

        internal void MenuShown() => _shown = true;

        /// <summary>Drops any outstanding claim. Safe to call when there is none.</summary>
        public void Released() => _shown = false;

        /// <summary>
        /// True when this Escape belongs to a link menu being dismissed, and should not also act on
        /// the message behind it. Consumes the claim.
        /// </summary>
        public bool TryConsumeEscape()
        {
            var claimed = _shown;
            _shown = false;
            return claimed;
        }
    }
}
