using System;
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
/// Inspect and Open link in new window (the issue #483 concerns) are gone by construction rather
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
                                       IClipboardService clipboard, Action<string, bool> report,
                                       Action<string> composeTo)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(composeTo);

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
                state.MenuShown();

                args.MenuItems.Add(Item(environment, state, "Open",
                    () => ExternalUriPolicy.TryOpenExternal(link.Href)));

                if (MailtoRecipient(link.Href) is { } recipient)
                    args.MenuItems.Add(Item(environment, state, "Compose to This Address",
                        () => composeTo(recipient)));

                args.MenuItems.Add(Item(environment, state, "Copy Address",
                    () => Copy(clipboard, report, link.Href, "Link address")));

                if (HasDistinctText(link))
                    args.MenuItems.Add(Item(environment, state, "Copy Text",
                        () => Copy(clipboard, report, link.Text, "Link text")));
            }
            catch (Exception ex)
            {
                LogService.Log("LinkContextMenu: building the menu failed", ex);
                args.MenuItems.Clear();
                args.Handled = true;
            }
        };

        return state;
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
    /// Writes feedback into the open document rather than raising it on the host window. A
    /// host-window notification is dropped while focus is inside the WebView2 (issue #329), and
    /// focus is inside it for the whole life of this menu. The region is created on demand and
    /// positioned off-screen, so it exists for any message rather than only an invite card.
    /// </summary>
    public static string StatusScript(string text) =>
        "(function(){var s=document.getElementById('qm-link-status');" +
        "if(!s){s=document.createElement('div');s.id='qm-link-status';" +
        "s.setAttribute('aria-live','assertive');s.setAttribute('aria-atomic','true');" +
        "s.style.cssText='position:absolute;left:-10000px;width:1px;height:1px;overflow:hidden';" +
        "document.body.appendChild(s);}" +
        "s.textContent=" + JsonSerializer.Serialize(text) + ";})();";

    private static void Copy(IClipboardService clipboard, Action<string, bool> report,
                             string text, string what)
    {
        var copied = clipboard.SetText(text);
        report(copied ? $"{what} copied." : $"Could not copy the {what.ToLowerInvariant()}.", copied);
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

        /// <summary>Test seam: the production setter runs only inside the menu build.</summary>
        internal void MenuShownForTests() => MenuShown();

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
