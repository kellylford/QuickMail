using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Shared HTML rendering helpers for the reading pane and standalone MessageWindow.
/// All methods are pure static; nothing is constructed here. <see cref="IThemeService"/> is taken as
/// a parameter purely to read color tokens (plain field reads, safe off the UI thread) — the same
/// service the caller already draws <c>themeCss</c> from.
/// </summary>
public static class MessageBodyHtmlBuilder
{
    public static readonly TimeSpan HtmlRegexTimeout = TimeSpan.FromMilliseconds(500);
    public const int MaxRichHtmlRenderChars = 1_000_000;
    public const int MaxRichHtmlTableCount  = 500;
    public const int MaxReaderTextChars     = 140_000;

    /// <summary>Rounds of the stripping passes before settling; see <see cref="TryStripHeavyHtml(string, TimeSpan, out string)"/>.</summary>
    private const int MaxStripRounds = 4;

    /// <summary>
    /// An opening or closing tag of any element the stripping passes remove, as the HTML tokenizer
    /// reads a tag name: ended by whitespace, a solidus, a closing bracket, or the end of input.
    /// </summary>
    private const string ResidualForbiddenTag =
        @"<(/?(?:script|style|svg|math|iframe|frame|frameset|object|embed|applet|video|audio|source|track|" +
        @"canvas|form|img|image|link|base|meta|input|button|portal|title|html|head|body)(?=[\s/>]|$))";

    /// <summary>
    /// A start tag with something after its name: the name, then everything up to the closing
    /// bracket, where a bracket inside a quoted value does not count — as the tokenizer reads it.
    /// </summary>
    private static readonly Regex StartTagWithAttributes = new(
        @"<([a-zA-Z][^\s/>]*)((?:[\s/](?:[^>""']|""[^""]*""|'[^']*')*))>",
        RegexOptions.Compiled,
        HtmlRegexTimeout);

    /// <summary>Attributes no message keeps: script, styling, remote fetches, frames, forms, downloads.</summary>
    private static readonly System.Collections.Generic.HashSet<string> BlockedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "src", "srcset", "background", "target", "ping", "srcdoc", "formaction", "action",
        "poster", "download", "lowsrc", "dynsrc", "xlink:href", "http-equiv",
    };

    /// <summary>
    /// Rebuilds a start tag from the attributes it is allowed to keep, reading them the way the HTML
    /// tokenizer does: a name runs to whitespace, "/", "=" or "&gt;"; a value is quoted, or runs to
    /// whitespace or "&gt;". Every kept value is written back double-quoted, so nothing the sender
    /// wrote can end the value early and start a new attribute.
    /// </summary>
    private static string RebuildStartTag(Match match)
    {
        var name = match.Groups[1].Value;
        var rest = match.Groups[2].Value;
        var sb = new System.Text.StringBuilder("<").Append(name);
        var i = 0;
        while (i < rest.Length)
        {
            var c = rest[i];
            if (char.IsWhiteSpace(c) || c == '/') { i++; continue; }

            var start = i;
            // A name may begin with "=" (a tokenizer quirk); it never ends on its first character.
            i++;
            while (i < rest.Length && !char.IsWhiteSpace(rest[i]) && rest[i] != '/' && rest[i] != '=' ) i++;
            var attr = rest[start..i];

            while (i < rest.Length && char.IsWhiteSpace(rest[i])) i++;
            string? value = null;
            if (i < rest.Length && rest[i] == '=')
            {
                i++;
                while (i < rest.Length && char.IsWhiteSpace(rest[i])) i++;
                if (i < rest.Length && (rest[i] == '"' || rest[i] == '\''))
                {
                    var quote = rest[i++];
                    var end = rest.IndexOf(quote, i);
                    if (end < 0) end = rest.Length;
                    value = rest[i..end];
                    i = Math.Min(end + 1, rest.Length);
                }
                else
                {
                    var vs = i;
                    while (i < rest.Length && !char.IsWhiteSpace(rest[i])) i++;
                    value = rest[vs..i];
                }
            }

            if (attr.StartsWith("on", StringComparison.OrdinalIgnoreCase) || BlockedAttributes.Contains(attr))
                continue;
            // Only ordinary attribute names survive, so no rebuilt name can itself carry markup.
            if (!Regex.IsMatch(attr, "^[A-Za-z][A-Za-z0-9_:.-]*$")) continue;

            sb.Append(' ').Append(attr);
            if (value is not null)
                sb.Append("=\"").Append(value.Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;")).Append('"');
        }
        if (rest.TrimEnd().EndsWith('/')) sb.Append(" /");
        return sb.Append('>').ToString();
    }

    /// <summary>An end tag with anything after its name, read quote-aware as the tokenizer does.</summary>
    private const string EndTagWithAttributes =
        @"</([A-Za-z][^\s/>]*)(?:[\s/](?:[^>""']|""[^""]*""|'[^']*')*)>";

    private static readonly Regex AutoLinkUrl = new(
        @"\b((?:https?|mailto):[^\s<>""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        HtmlRegexTimeout);

    private static readonly char[] AutoLinkTrailingPunct =
        ['.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '\''];

    /// <summary>
    /// An <c>&lt;img&gt;</c> carrying a non-empty <c>alt</c>. An empty <c>alt=""</c> is deliberately
    /// NOT matched: that is the author declaring the image decorative, and the right handling is the
    /// plain removal the later pass performs.
    /// </summary>
    private static readonly Regex ImgWithAltText = new(
        @"<img\b[^>]*?\balt\s*=\s*(?:""(?<alt>[^""]+)""|'(?<alt>[^']+)'|(?<alt>[^\s""'>]+))[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        HtmlRegexTimeout);

    /// <param name="themeCss">
    /// Optional theme CSS from <see cref="IThemeService.BuildMessageCss"/> — the
    /// <c>:root { --qm-* }</c> variable block. When null the document's CSS
    /// variables are absent and the <c>var(--qm-*, fallback)</c> declarations
    /// resolve to their system-color fallbacks (the pre-theming behavior).
    /// </param>
    /// <param name="themeService">
    /// Supplies the invite card's colors when <paramref name="detail"/> carries a calendar invite.
    /// The card is built and injected HERE, from the detail's own <c>CalendarInvite</c>, rather than
    /// by each caller: a surface that forgets it renders an invitation with no date or time at all,
    /// because the "when" lives only in the ICS part and never in the body. That is exactly how the
    /// standalone MessageWindow shipped, so the obligation is not left to the call site. Null yields
    /// the card's fallback palette; no invite yields no card.
    /// </param>
    /// <param name="embeddedPictureBase">
    /// Where the host serves this message's embedded pictures, e.g.
    /// <c>https://quickmail-images.invalid/&lt;key&gt;/</c>; the Content-ID is appended. Null keeps
    /// every picture blocked, as before (#729, and the setting to turn pictures off).
    /// </param>
    public static string BuildMessageHtml(MailMessageDetail detail, string? themeCss = null,
        bool forcePlainText = false, IThemeService? themeService = null, string? embeddedPictureBase = null) =>
        BuildMessageDocument(detail, themeCss, forcePlainText, themeService,
            new PictureSources(embeddedPictureBase, null, false)).Html;

    /// <summary>
    /// As <see cref="BuildMessageHtml"/>, for a surface that also shows pictures from the web
    /// (#508): the result lists the web addresses the document's pictures stand for, which the host
    /// fetches and serves itself, and how many web pictures were left out.
    /// </summary>
    public static MessageDocument BuildMessageDocument(MailMessageDetail detail, string? themeCss,
        bool forcePlainText, IThemeService? themeService, PictureSources pictures)
    {
        var document = BuildBodyDocument(detail, themeCss, forcePlainText, pictures,
            out var webPictures, out var blockedWebPictures);
        var card = EventCardHtmlBuilder.Build(detail.CalendarInvite, themeService);
        var html = card.Length == 0 ? document : InjectEventCard(document, card);
        return new MessageDocument(html, webPictures, blockedWebPictures);
    }

    /// <summary>Injects the event card HTML just after the opening &lt;body&gt; tag.</summary>
    private static string InjectEventCard(string html, string eventCardHtml)
    {
        var bodyTag = "<body";
        var bodyIdx = html.IndexOf(bodyTag, StringComparison.OrdinalIgnoreCase);
        if (bodyIdx < 0) return eventCardHtml + html;

        var closeIdx = html.IndexOf('>', bodyIdx);
        if (closeIdx < 0) return eventCardHtml + html;

        return html.Insert(closeIdx + 1, eventCardHtml);
    }

    private static string BuildBodyDocument(MailMessageDetail detail, string? themeCss, bool forcePlainText,
        PictureSources pictures, out IReadOnlyList<string> webPictures, out int blockedWebPictures)
    {
        webPictures = [];
        blockedWebPictures = 0;
        var htmlBody = detail.HtmlBody ?? string.Empty;

        // Plain-text view (issue #34): the user asked to read this message as plain text.
        // Render the sender's original text/plain part verbatim for maximum fidelity, and only
        // when there is no plain-text part fall back to text extracted from the HTML (with a note
        // so the user knows the text was derived, not original).
        if (forcePlainText)
        {
            var hasPlain = !string.IsNullOrWhiteSpace(detail.PlainTextBody);
            var plainText = hasPlain ? detail.PlainTextBody : HtmlToText(htmlBody);
            var plainNote = !hasPlain && !string.IsNullOrWhiteSpace(htmlBody)
                ? "This message has no plain-text version; showing text extracted from the HTML."
                : null;
            return BuildPlainTextHtmlDocument(detail.Subject, plainText, plainNote, themeCss);
        }

        // Fail closed: if any stripping pass times out we cannot claim the HTML is
        // sanitized, so fall through to the plain-text (reader mode) rendering instead
        // of showing partially stripped markup.
        if (!string.IsNullOrWhiteSpace(htmlBody) && !ShouldUseReaderMode(htmlBody)
            && TryBuildSanitizedHtmlDocument(detail.Subject, htmlBody, themeCss, HtmlRegexTimeout,
                                             pictures, out var sanitized, out webPictures, out blockedWebPictures))
            return sanitized;
        webPictures = [];
        blockedWebPictures = 0;

        var text = !string.IsNullOrWhiteSpace(detail.PlainTextBody)
            ? detail.PlainTextBody
            : HtmlToText(htmlBody);

        var note = !string.IsNullOrWhiteSpace(htmlBody)
            ? "This message uses complex HTML, so QuickMail is showing a simplified body."
            : null;
        return BuildPlainTextHtmlDocument(detail.Subject, text, note, themeCss);
    }

    public static bool ShouldUseReaderMode(string html) =>
        html.Length > MaxRichHtmlRenderChars ||
        CountOccurrences(html, "<table") > MaxRichHtmlTableCount;

    /// <summary>
    /// Builds the full sanitized document for the reading pane. Returns false when any
    /// stripping pass timed out — the output must then be discarded and the caller must
    /// fall back to plain-text rendering (a partially stripped document is not sanitized).
    /// </summary>
    public static bool TryBuildSanitizedHtmlDocument(string? subject, string html, out string document) =>
        TryBuildSanitizedHtmlDocument(subject, html, null, HtmlRegexTimeout, out document);

    public static bool TryBuildSanitizedHtmlDocument(string? subject, string html, string? themeCss, out string document) =>
        TryBuildSanitizedHtmlDocument(subject, html, themeCss, HtmlRegexTimeout, out document);

    internal static bool TryBuildSanitizedHtmlDocument(
        string? subject, string html, string? themeCss, TimeSpan timeout, out string document) =>
        TryBuildSanitizedHtmlDocument(subject, html, themeCss, timeout, null, out document);

    internal static bool TryBuildSanitizedHtmlDocument(
        string? subject, string html, string? themeCss, TimeSpan timeout, string? embeddedPictureBase,
        out string document) =>
        TryBuildSanitizedHtmlDocument(subject, html, themeCss, timeout,
            new PictureSources(embeddedPictureBase, null, false), out document, out _, out _);

    internal static bool TryBuildSanitizedHtmlDocument(
        string? subject, string html, string? themeCss, TimeSpan timeout, PictureSources sources,
        out string document, out IReadOnlyList<string> webPictures, out int blockedWebPictures)
    {
        webPictures = [];
        blockedWebPictures = 0;
        List<SetAsidePicture>? pictures = null;
        if (sources.Any)
        {
            if (!SetAsidePictures(html, timeout, sources, out html, out pictures, out var web, out blockedWebPictures))
            {
                document = string.Empty;
                return false;
            }
            webPictures = web;
        }
        if (!TryStripHeavyHtml(html, timeout, out var body))
        {
            document = string.Empty;
            webPictures = [];
            blockedWebPictures = 0;
            return false;
        }
        if (pictures is { Count: > 0 })
            body = RestorePictures(body, pictures, p => p.Web
                ? sources.WebBase + p.Src
                : sources.EmbeddedBase + Uri.EscapeDataString(p.Src));
        // Written after the passes, like the pictures: QuickMail's own markup, never the sender's.
        if (blockedWebPictures > 0 && sources.NoteBlockedWebPictures)
            body = WebPicturesNotice + body;
        document = ComposeSanitizedDocument(subject, body, themeCss,
            pictures is { Count: > 0 } ? PictureOrigins(sources) : null);
        return true;
    }

    // ── Pictures sent inside the message (#729) ──────────────────────────────
    //
    // The stripping passes remove every <img>. A picture whose src is cid: — a part of this
    // message — is set aside first: the whole tag is replaced by a marker made of private-use
    // characters and digits, which no pass touches, and after the passes (and their final escape)
    // the marker becomes an <img> QuickMail writes itself. Nothing of the sender's tag survives
    // but the Content-ID, alt text and a numeric width and height: the src points at the host's
    // own picture address, and every other attribute is dropped. A tag this does not recognise is
    // left to the passes, which remove it — the failure mode is a picture not shown, never markup
    // let through.
    //
    // Pictures from the web (#508) go the same way when the user has asked for them: the marker
    // becomes an <img> whose src is again the host's own address, numbered, and the host fetches
    // the real address itself. The sender's address never reaches the document, so the WebView2
    // contacts nobody, and the CSP needs no web origin at all. A picture declared 2 pixels or
    // smaller on a side is taken for a tracking pixel: never fetched, and not counted as a picture
    // left out.

    private const char PictureMarkOpen = '\uE000';
    private const char PictureMarkClose = '\uE001';

    /// <summary>Most distinct web addresses one message's pictures are fetched from.</summary>
    public const int MaxWebPictures = 100;

    /// <summary>The words a picture from the web is left out with, before the link that loads them.</summary>
    public const string WebPicturesNoticeText = "Pictures from the web are not shown.";

    /// <summary>The <c>quickmail:</c> action the notice's link carries.</summary>
    public const string LoadPicturesAction = "load-pictures";

    // No sender styling survives the passes (style blocks and style attributes are both removed),
    // so nothing in the message can hide this notice, restyle its link, or lay it over other text.
    private static string WebPicturesNotice =>
        "<p class=\"qm-pictures-note\" style=\"margin:0 0 8px;padding:4px 8px;" +
        "border-left:3px solid var(--qm-border, #777);\">" + WebPicturesNoticeText + " <a href=\"" +
        QuickMailLinks.Build(LoadPicturesAction) + "\">Load pictures</a></p>";

    private sealed record SetAsidePicture(string Src, bool Web, string? Alt, int? Width, int? Height);

    private static readonly Regex ImgTag = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, HtmlRegexTimeout);

    private static readonly Regex ImgAttribute = new(
        @"(?:^|[\s/])(src|alt|width|height)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, HtmlRegexTimeout);

    private static readonly Regex PictureMarker = new(
        "\uE000(\\d+)\uE001", RegexOptions.Compiled, HtmlRegexTimeout);

    private static bool SetAsidePictures(string html, TimeSpan timeout, PictureSources sources, out string result,
        out List<SetAsidePicture> pictures, out List<string> webPictures, out int blockedWebPictures)
    {
        var found = new List<SetAsidePicture>();
        var web = new List<string>();
        var blocked = 0;
        pictures = found;
        webPictures = web;
        blockedWebPictures = 0;
        // A marker already in the sender's text would be taken for one of ours; remove any first.
        if (!TryRegexReplace(html, "\uE000\\d*\uE001", string.Empty, RegexOptions.None, timeout, out html))
        {
            result = html;
            return false;
        }
        var source = html;
        var ok = TryRegexReplace(html, ImgTag, match =>
        {
            // Only a picture that sits in text is set aside. One written inside another tag —
            // "<sty<img src=cid:a>le>" — is left to the passes, which remove it: set aside, its
            // marker would hide the tag name "style" from every pass, and dropping the marker
            // later would join the halves back into a live <style> (#729 security review).
            if (IsInsideTag(source, match.Index))
                return match.Value;
            string? src = null, alt = null, width = null, height = null;
            foreach (Match a in ImgAttribute.Matches(match.Value[4..]))
            {
                var value = a.Groups["v"].Value;
                switch (a.Groups[1].Value.ToLowerInvariant())
                {
                    case "src": src ??= value; break;
                    case "alt": alt ??= value; break;
                    case "width": width ??= value; break;
                    case "height": height ??= value; break;
                }
            }
            var decodedSrc = WebUtility.HtmlDecode(src ?? string.Empty).Trim();
            var decodedAlt = alt is null ? null : WebUtility.HtmlDecode(alt);
            if (decodedSrc.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) && decodedSrc.Length > 4)
            {
                if (sources.EmbeddedBase is null)
                    return match.Value; // embedded pictures are off: the passes remove it as before
                found.Add(new SetAsidePicture(decodedSrc[4..].Trim('<', '>'), false, decodedAlt,
                    PictureDimension(width), PictureDimension(height)));
                return Marker(found.Count - 1);
            }
            if (WebPictureAddress(decodedSrc) is not { } address || IsTrackerSize(width) || IsTrackerSize(height))
                return match.Value; // not a picture anyone is shown: the passes remove it as before
            if (sources.WebBase is null)
            {
                blocked++;
                return match.Value;
            }
            var index = web.IndexOf(address);
            if (index < 0)
            {
                if (web.Count >= MaxWebPictures)
                    return match.Value;
                web.Add(address);
                index = web.Count - 1;
            }
            found.Add(new SetAsidePicture(index.ToString(System.Globalization.CultureInfo.InvariantCulture), true,
                decodedAlt, PictureDimension(width), PictureDimension(height)));
            return Marker(found.Count - 1);
        }, timeout, out result);
        blockedWebPictures = blocked;
        return ok;
    }

    private static string Marker(int index) =>
        PictureMarkOpen + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + PictureMarkClose;

    /// <summary>
    /// The absolute http or https address a picture's src names, or null: a relative or
    /// protocol-relative address, another scheme, one carrying a user name or password, or one
    /// longer than any real picture address.
    /// </summary>
    internal static string? WebPictureAddress(string src)
    {
        if (src.Length is 0 or > 2048
            || !Uri.TryCreate(src, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri.AbsoluteUri;
    }

    /// <summary>True for a declared width or height of 2 pixels or less: a tracking pixel or spacer.</summary>
    private static bool IsTrackerSize(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) v = v[..^2].TrimEnd();
        return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n <= 2;
    }

    private static int? PictureDimension(string? value) =>
        int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && n is > 0 and <= 10000 ? n : null;

    /// <param name="srcFor">
    /// The address to write for a picture, or null when it cannot be had — then its description
    /// is written as text in its place, as when pictures are not shown at all.
    /// </param>
    private static string RestorePictures(string body, List<SetAsidePicture> pictures, Func<SetAsidePicture, string?> srcFor) =>
        PictureMarker.Replace(body, m =>
        {
            if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var i) || i >= pictures.Count)
                return " ";
            // A picture goes back only where it is text content. A marker inside a tag — in an
            // attribute value, or after an opening "<" whose ">" comes later — would put QuickMail's
            // own <img> inside the sender's markup, where its quotes and ">" end the sender's
            // attribute or close the sender's tag early. There the picture is dropped (#729
            // security review).
            // Dropped as a space, never as nothing: removing it outright would join the text on
            // either side, and whatever the passes saw as two harmless pieces must stay two.
            if (IsInsideTag(body, m.Index))
                return " ";
            var p = pictures[i];
            if (srcFor(p) is not { } address)
            {
                // As the passes would have left it: its words, or — never nothing — a space.
                var words = p.Alt?.Trim();
                return string.IsNullOrEmpty(words) ? " " : WebUtility.HtmlEncode(words);
            }
            var sb = new System.Text.StringBuilder("<img src=\"")
                .Append(WebUtility.HtmlEncode(address)).Append('"');
            // Described: its words. Decorative (alt="") and undescribed alike are alt="": before
            // pictures were shown, a picture with no alt text was dropped without a word, and
            // showing it to sighted readers should not add noise for everyone else.
            sb.Append(" alt=\"").Append(WebUtility.HtmlEncode(p.Alt?.Trim() ?? string.Empty)).Append('"');
            if (p.Width is { } w) sb.Append(" width=\"").Append(w).Append('"');
            if (p.Height is { } h) sb.Append(" height=\"").Append(h).Append('"');
            return sb.Append('>').ToString();
        });

    /// <summary>True when <paramref name="index"/> falls between a tag's "&lt;" and its "&gt;".</summary>
    private static bool IsInsideTag(string body, int index)
    {
        var lastOpen = body.LastIndexOf('<', Math.Max(0, index - 1));
        if (lastOpen < 0 || index == 0) return false;
        var lastClose = body.LastIndexOf('>', index - 1);
        return lastOpen > lastClose;
    }

    /// <summary>The scheme and host of each picture address, for the CSP's img-src.</summary>
    private static string PictureOrigins(PictureSources sources)
    {
        var origins = new List<string>();
        foreach (var pictureBase in new[] { sources.EmbeddedBase, sources.WebBase })
        {
            if (pictureBase is null) continue;
            var origin = new Uri(pictureBase).GetLeftPart(UriPartial.Authority);
            if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) origins.Add(origin);
        }
        return string.Join(' ', origins);
    }

    /// <summary>
    /// The sender's HTML reduced to body content by the same passes the reading pane uses — for a
    /// document someone else builds around it, the saved web page (#728). Removes the sender's own
    /// title and structural tags, as <see cref="ComposeSanitizedDocument"/> does, so nothing in the
    /// fragment can displace the host document's head. Returns false, with an empty fragment, when
    /// any pass timed out: a partially stripped body is not sanitized and must not be written.
    /// <para>The same caveat applies as everywhere else: this is defence in depth. The host document
    /// must still carry the strict CSP.</para>
    /// </summary>
    public static bool TryBuildSanitizedBodyFragment(string html, out string fragment) =>
        TryBuildSanitizedBodyFragment(html, null, null, out fragment);

    /// <summary>
    /// As <see cref="TryBuildSanitizedBodyFragment(string, out string)"/>, keeping the message's
    /// pictures (#728, #729): each picture the message shows — one of its own parts, named by
    /// Content-ID, or one on the web, named by its address — is written as an &lt;img&gt;
    /// QuickMail builds itself, with the address <paramref name="embeddedSrc"/> or
    /// <paramref name="webSrc"/> gives for it. A null resolver, or a null answer, writes the
    /// picture's description as text instead. The sender's own &lt;img&gt; markup never survives;
    /// only the Content-ID or address, the description and a numeric size are carried across.
    /// </summary>
    public static bool TryBuildSanitizedBodyFragment(string html, Func<string, string?>? embeddedSrc,
        Func<string, string?>? webSrc, out string fragment)
    {
        List<SetAsidePicture>? pictures = null;
        List<string>? web = null;
        if (embeddedSrc is not null || webSrc is not null)
        {
            // The bases only switch each kind on; the resolvers decide what is written.
            var sources = new PictureSources(embeddedSrc is null ? null : "about:", webSrc is null ? null : "about:", false);
            if (!SetAsidePictures(html, HtmlRegexTimeout, sources, out html, out pictures, out web, out _))
            {
                fragment = string.Empty;
                return false;
            }
        }
        if (!TryStripHeavyHtml(html, HtmlRegexTimeout, out var body))
        {
            fragment = string.Empty;
            return false;
        }
        if (pictures is { Count: > 0 })
        {
            var addresses = web!;
            body = RestorePictures(body, pictures, p => p.Web
                ? webSrc?.Invoke(addresses[int.Parse(p.Src, System.Globalization.CultureInfo.InvariantCulture)])
                : embeddedSrc?.Invoke(p.Src));
        }
        // The sender's title and html/head/body tags are already gone: TryStripHeavyHtml removes them
        // inside its rounds. Removing them here, after its final escape, is what let "<me<body>ta"
        // become a live <meta> again (#728 security review, second pass). Nothing below may DELETE
        // text from the markup — only escape it, which cannot join two pieces into a tag.
        // The host page's own landmarks: a "</main>" in the message would close the box the message
        // is contained in, and a "<header>" would open a second one beside the real details.
        body = SafeRegexReplace(body, @"<(/?(?:main|header)(?=[\s/>]|$))", "&lt;$1",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        fragment = body;
        return true;
    }

    /// <summary>The strict policy every saved or displayed message document carries.</summary>
    public const string StrictCspContent =
        "default-src 'none'; script-src 'none'; object-src 'none'; " +
        "frame-src 'none'; img-src 'none'; media-src 'none'; connect-src 'none'; " +
        "form-action 'none'; base-uri 'none'; style-src 'unsafe-inline';";

    /// <summary>
    /// The theme <c>:root</c> variable block as a style tag, or empty. Emitted
    /// before the default CSS so the <c>var(--qm-*)</c> references resolve.
    /// </summary>
    private static string ThemeStyleTag(string? themeCss) =>
        string.IsNullOrEmpty(themeCss) ? string.Empty : "<style>" + themeCss + "</style>";

    private static string ComposeSanitizedDocument(string? subject, string body, string? themeCss, string? pictureOrigin = null)
    {
        var titleTag = $"<title>{WebUtility.HtmlEncode(subject ?? string.Empty)}</title>";
        // Pictures sent inside the message may load from the host's own picture address and
        // nowhere else; with none, nothing may load at all.
        var csp = pictureOrigin is null
            ? StrictCspContent
            : StrictCspContent.Replace("img-src 'none';", "img-src " + pictureOrigin + ";", StringComparison.Ordinal);
        var cspTag = "<meta http-equiv=\"Content-Security-Policy\" content=\"" + csp + "\">";
        // Defaults only — sender-styled HTML still wins unless the user opts into
        // force-theme (which arrives inside themeCss as !important rules). The
        // var() fallbacks are the CSS system colors, so with no theme CSS the
        // document renders exactly as before theming existed.
        const string css =
            "<style>html,body{margin:0;padding:8px 12px;" +
            "font-family:var(--qm-font, 'Segoe UI', Arial, sans-serif);" +
            "font-size:var(--qm-font-size, 13px);line-height:1.45;word-break:break-word;" +
            "background:var(--qm-bg, Canvas);color:var(--qm-text, CanvasText);}" +
            "table{max-width:100%;border-collapse:collapse;}td,th{vertical-align:top;}" +
            "img{max-width:100%;height:auto;}" +
            "a{color:var(--qm-link, #0645ad);}</style>";
        var styleBlock = ThemeStyleTag(themeCss) + css;

        // The document is ALWAYS one this method builds, and the sender's markup is always content
        // inside its body. An earlier version spliced the head block in at the first literal
        // "<head>" found anywhere in the message, which a sender could simply write in the middle
        // of a paragraph: the CSP meta then landed in body content, where a browser ignores it, and
        // the document rendered with no policy at all. A meta CSP is only a policy when it is a
        // child of the real head, so the real head is the only place this may put it.
        //
        // The sender's own structural tags are removed rather than left to the parser. A stray
        // <html>/<head>/<body> start tag in body content is ignored, but its ATTRIBUTES are merged
        // onto the existing element — so "<body onload=…>" buried in a message would be writing
        // attributes onto the document's real body. The on* handler is stripped above; nothing
        // should depend on that being the only such attribute anyone ever finds.
        // (The sender's title and html/head/body tags were removed inside TryStripHeavyHtml's rounds.
        // Removing them here, after its final escape, rejoined tags — see TryBuildSanitizedBodyFragment.)

        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
               titleTag + cspTag + styleBlock +
               "</head><body tabindex=\"0\">" + body + "</body></html>";
    }

    public static string BuildPlainTextHtmlDocument(string? subject, string text, string? note, string? themeCss = null)
    {
        var clipped  = Truncate(text ?? string.Empty, MaxReaderTextChars);
        var encoded  = WebUtility.HtmlEncode(clipped);
        var linked   = AutoLinkPlainTextUrls(encoded);
        var titleTag = $"<title>{WebUtility.HtmlEncode(subject ?? string.Empty)}</title>";
        var noteHtml = string.IsNullOrWhiteSpace(note)
            ? string.Empty
            : "<p class=\"note\">" + WebUtility.HtmlEncode(note) + "</p>";

        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
               titleTag +
               "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline';\">" +
               ThemeStyleTag(themeCss) +
               "<style>html,body{margin:0;padding:8px 12px;" +
               "font-family:var(--qm-font, 'Segoe UI', Arial, sans-serif);" +
               "font-size:var(--qm-font-size, 13px);white-space:pre-wrap;word-break:break-word;" +
               "background:var(--qm-bg, Canvas);color:var(--qm-text, CanvasText);}" +
               "a{color:var(--qm-link, #0645ad);}" +
               ".note{white-space:normal;border-left:3px solid var(--qm-border, #777);" +
               "padding-left:8px;margin:0 0 12px 0;color:var(--qm-text-muted, #555);}</style>" +
               "</head><body tabindex=\"0\">" + noteHtml + linked + "</body></html>";
    }

    public static string AutoLinkPlainTextUrls(string encoded)
    {
        try
        {
            return AutoLinkUrl.Replace(encoded, m =>
            {
                var url      = m.Value;
                var trailing = string.Empty;
                while (url.Length > 0 && Array.IndexOf(AutoLinkTrailingPunct, url[^1]) >= 0)
                {
                    trailing = url[^1] + trailing;
                    url = url[..^1];
                }
                if (url.Length == 0) return m.Value;
                return $"<a href=\"{url}\" rel=\"nofollow noreferrer\">{url}</a>{trailing}";
            });
        }
        catch (RegexMatchTimeoutException) { return encoded; }
    }

    /// <summary>
    /// Best-effort stripping for non-rendered consumers (e.g. HtmlToText, whose output is
    /// HTML-encoded before display). Rendering paths must use <see cref="TryStripHeavyHtml"/>
    /// and fail closed on a false return.
    /// </summary>
    public static string StripHeavyHtml(string html)
    {
        TryStripHeavyHtml(html, HtmlRegexTimeout, out var body);
        return body;
    }

    public static bool TryStripHeavyHtml(string html, out string stripped) =>
        TryStripHeavyHtml(html, HtmlRegexTimeout, out stripped);

    /// <summary>
    /// SECURITY NOTE: this regex pass is defense-in-depth, not the security boundary.
    /// Regex-based HTML stripping is structurally bypassable (e.g. slash-separated
    /// attributes, malformed nesting); the strict CSP injected by
    /// ComposeSanitizedDocument — script-src 'none', default-src 'none' — is what actually
    /// prevents execution and remote loads. Never weaken that CSP on the assumption that
    /// this stripping protects the document.
    ///
    /// <para>
    /// The obvious extra layer, <c>CoreWebView2Settings.IsScriptEnabled = false</c> on the two
    /// message-body surfaces, was measured against WebView2 1.0.4022.49 and MUST NOT be adopted.
    /// It does block page script, and <c>ExecuteScriptAsync</c> and the top level of an
    /// <c>AddScriptToExecuteOnDocumentCreatedAsync</c> script still run — but any CALLBACK those
    /// register never fires. That silently removes the reading pane's keydown relay (Escape, F6,
    /// Ctrl+W, Alt+A all become unreachable from inside the document, where focus lands when a
    /// message opens) and the link menu's status region, which is appended on DOMContentLoaded.
    /// Trading a keyboard trap and a dead live region for defence in depth behind a CSP that
    /// already denies script is not a trade worth making. If a second layer is wanted, the one to
    /// build is a CSP HEADER — serve the message body through <c>WebResourceRequested</c> rather
    /// than <c>NavigateToString</c> — since a header cannot be displaced by document structure the
    /// way a meta element can.
    /// </para>
    ///
    /// Returns false when any pass timed out, in
    /// which case <paramref name="stripped"/> holds a PARTIALLY stripped document that
    /// must not be rendered.
    /// </summary>
    /// <summary>
    /// An end tag for <paramref name="name"/> as the HTML tokenizer accepts one: the name must be
    /// followed by whitespace, a solidus, or the closing bracket — so <c>&lt;/script&gt;</c>,
    /// <c>&lt;/script &gt;</c>, <c>&lt;/script/&gt;</c> and <c>&lt;/script foo="bar"&gt;</c> all
    /// match, while <c>&lt;/scriptable&gt;</c> (a different element) does not.
    /// <paramref name="name"/> may be a backreference such as <c>\1</c>.
    /// </summary>
    private static string EndTag(string name) => "</" + name + "(?=[\\s/>])[^>]*>";

    internal static bool TryStripHeavyHtml(string html, TimeSpan timeout, out string stripped)
    {
        var complete = true;
        string Step(string input, string pattern, RegexOptions options, string replacement = "")
        {
            if (!TryRegexReplace(input, pattern, replacement, options, timeout, out var result))
                complete = false;
            return result;
        }

        string StepEval(string input, Regex regex, MatchEvaluator evaluator)
        {
            if (!TryRegexReplace(input, regex, evaluator, timeout, out var result))
                complete = false;
            return result;
        }

        // The passes run to a fixed point, not once. Each pass removes text, and removing text can join
        // the pieces either side of it into a tag an EARLIER pass exists to remove: "<me<link>ta
        // http-equiv=refresh …>" is a <meta> refresh once the <link> pass has run, and
        // "<me style=x ta …>" is one once the attribute pass has. A single ordered sweep handed those
        // straight to the parser: a saved page that redirected when opened, and a reading pane that
        // launched the URL in the browser on preview. Found by the #728 security review, 2026-09-18.
        var body = html;
        for (var round = 0; round < MaxStripRounds; round++)
        {
            var before = body;
            // The sender's own document structure. A stray <html>/<head>/<body> start tag in content
            // would have its attributes merged onto the host document's real element, and a <title>
            // would set the document title the reading pane announces. Inside the rounds, like every
            // other removal, so what a removal joins together is examined again.
            body = Step(body, "<title[^>]*>.*?" + EndTag("title"), RegexOptions.IgnoreCase | RegexOptions.Singleline);
            body = Step(body, "</?(html|head|body)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // Remove elements hidden via inline display:none (e.g. newsletter preheader padding divs).
            // Must run before style-attribute stripping, which would make these visible.
            body = Step(body,
                @"<(div|span|p)\b[^>]*\bstyle\s*=\s*(?:""[^""]*display\s*:\s*none[^""]*""|'[^']*display\s*:\s*none[^']*')[^>]*>.*?" + EndTag("\\1"),
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            body = Step(body, "<!--.*?-->", RegexOptions.Singleline);
            // End tags use EndTag() rather than a literal "</script>": the HTML tokenizer closes an
            // element at "</script >", "</script/>" and "</script foo>" as readily as at "</script>",
            // and a pattern that accepts only the last of those leaves the other three to be stripped
            // by nobody and executed by the parser. Reported privately 2026-09-09 with a working
            // proof of concept; the same asymmetry applied to every rule below.
            body = Step(body, "<script\\b.*?" + EndTag("script"), RegexOptions.IgnoreCase | RegexOptions.Singleline);
            body = Step(body, "<style\\b.*?" + EndTag("style"), RegexOptions.IgnoreCase | RegexOptions.Singleline);
            body = Step(body, "<svg\\b.*?" + EndTag("svg"), RegexOptions.IgnoreCase | RegexOptions.Singleline);
            body = Step(body, "<(iframe|object|embed|video|audio|canvas|form)\\b.*?" + EndTag("\\1"), RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // Substitute each image's alt text before images are removed (issue #163). Removing the
            // element outright discards the only name the image has: the CSP blocks the pixels either
            // way, so what is lost is not the picture but the words describing it. Worst where the
            // image is the whole content of a link — the anchor is left empty, has no accessible name,
            // and is announced from its href instead, so a row of social icons reads as whatever the
            // tracking URLs happen to spell ("redirect", "c/1pfGAI30…") rather than "Facebook".
            body = StepEval(body, ImgWithAltText, ImageAltReplacement);
            body = Step(body, "<(img|link|base|input|button|meta)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // target is stripped so that anchors navigate in-place: a target="_blank" link raises
            // WebView2's NewWindowRequested rather than NavigationStarting, and any host that
            // forgets to handle that event silently opens the link in an in-app popup instead of
            // the user's default browser (issue #483). Hosts handle both events; this keeps the
            // rendered document from depending on that.
            body = Step(body, "\\s(on\\w+|style|src|srcset|background|target|ping|srcdoc|formaction|action|poster|download)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // The pass above finds an attribute only after whitespace, but the HTML tokenizer also
            // starts one after "/" or straight after a closing quote: <p/style=…>, <a href="x"ping=…>.
            // So every start tag is also re-read attribute by attribute, as the tokenizer reads it, and
            // rebuilt from the attributes that are allowed (#728 security review, third pass).
            body = StepEval(body, StartTagWithAttributes, RebuildStartTag);
            // An end tag has no use for attributes, and a quoted one can hide a ">" that a plain
            // "<"/">" scan takes for the end of the tag: "</a x=\"><img …>\">" then put a restored
            // picture inside the tag, where its own quotes broke out of the attribute (#728 review).
            body = Step(body, EndTagWithAttributes, RegexOptions.None, "</$1>");
            if (!complete || string.Equals(before, body, StringComparison.Ordinal)) break;
            // Still changing after the last round: markup nested to defeat the rounds. Fail closed —
            // the caller shows the message as plain text rather than trust what is left.
            if (round == MaxStripRounds - 1) complete = false;
        }

        // Whatever the rounds left, no opener of a removed element survives as markup: its "<" is
        // escaped, so it renders as text. This also covers a <style> or <script> with NO end tag,
        // which the paired patterns above never match: an unclosed <style> turns the whole rest of
        // the document into a stylesheet that can hide and replace everything around the message.
        // The escape is the guarantee; the rounds are what keep ordinary mail looking as it did.
        body = Step(body, ResidualForbiddenTag, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, "&lt;$1");
        var settled = body;
        body = StepEval(settled, TagOpener, m => ClosedTagAt(settled, m.Index) ? m.Value : "&lt;");
        stripped = body;
        return complete;
    }

    /// <summary>A "&lt;" that begins a start or end tag, as the tokenizer reads one.</summary>
    private static readonly Regex TagOpener = new(@"<(?=/?[A-Za-z])", RegexOptions.Compiled, HtmlRegexTimeout);

    /// <summary>
    /// A complete tag at the current position, by the same quote-aware reading as
    /// <see cref="StartTagWithAttributes"/>: a "&gt;" inside a quoted value does not end it.
    /// </summary>
    private static readonly Regex ClosedTag = new(
        @"\G</?[A-Za-z][^\s/>]*(?:[\s/](?:[^>""']|""[^""]*""|'[^']*')*)?>",
        RegexOptions.Compiled, HtmlRegexTimeout);

    /// <summary>
    /// Every tag the passes left is one they could read whole — and a start tag they read whole
    /// has been rebuilt from its allowed attributes. A tag they could NOT read whole (its "&gt;"
    /// missing, or only inside a quoted value) was never rebuilt, so its attributes — a
    /// <c>style</c> written after "/", say — would reach the parser untouched the moment anything
    /// supplied a closing bracket: the host document's own "&lt;/body&gt;", or a restored picture.
    /// A sender could end a message with <c>&lt;div/style="position:fixed;inset:0"</c> and lay a
    /// page of their own over it. Each such "&lt;" is escaped, so it reads as text (#729
    /// security review). After this no tag in the body is left open, which is what lets
    /// <see cref="IsInsideTag"/> trust a plain "&lt;"/"&gt;" scan.
    /// </summary>
    private static bool ClosedTagAt(string body, int index) => ClosedTag.IsMatch(body, index);

    /// <summary>
    /// The text that stands in for an image: its alt text and nothing else, so a link whose content
    /// is an icon reads by its label ("Facebook link") exactly as it does in a client that shows the
    /// picture. No "Image:" prefix — the marker would be spoken on every icon in a footer row, and
    /// the cost of that repetition outweighs telling the reader the words arrived from a graphic.
    ///
    /// Decoded then re-encoded rather than spliced through: the attribute may hold entities that are
    /// already correct as text (<c>&amp;amp;</c>), but it may equally hold a bare <c>&lt;</c>, which
    /// is legal inside a quoted attribute and would open a tag once moved into content.
    /// </summary>
    private static string ImageAltReplacement(Match match)
    {
        var alt = match.Groups["alt"].Value.Trim();
        return alt.Length == 0 ? string.Empty : WebUtility.HtmlEncode(WebUtility.HtmlDecode(alt));
    }

    /// <summary>
    /// Removes any &lt;title&gt; the sender wrote. It must go even though the sender's markup ends
    /// up in the document's BODY: a title start tag in body content is handled by the parser's
    /// in-head rules, so a sender one would otherwise set document.title — which the reading pane
    /// uses for the message's own subject.
    /// </summary>
    public static string RemoveTitle(string html) =>
        SafeRegexReplace(html, "<title[^>]*>.*?" + EndTag("title"), string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = StripHeavyHtml(html);
        text = SafeRegexReplace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = SafeRegexReplace(text, "</(p|div|tr|li|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        text = SafeRegexReplace(text, "<[^>]+>", " ", RegexOptions.Singleline);
        text = WebUtility.HtmlDecode(text);
        text = SafeRegexReplace(text, "[ \\t\\r\\f\\v]+", " ", RegexOptions.None);
        text = SafeRegexReplace(text, "\\n\\s+|\\s+\\n", "\n", RegexOptions.None);
        text = SafeRegexReplace(text, "\\n{3,}", "\n\n", RegexOptions.None);
        return text.Trim();
    }

    public static string SafeRegexReplace(string input, string pattern, string replacement, RegexOptions options)
    {
        TryRegexReplace(input, pattern, replacement, options, HtmlRegexTimeout, out var result);
        return result;
    }

    /// <summary>
    /// Single home for the timeout-guarded regex replace: on timeout logs the pattern,
    /// hands back the input unchanged, and returns false.
    /// </summary>
    private static bool TryRegexReplace(
        string input, string pattern, string replacement, RegexOptions options,
        TimeSpan timeout, out string result)
    {
        try
        {
            result = Regex.Replace(input, pattern, replacement, options, timeout);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            LogService.Log($"HTML cleanup timed out for pattern: {pattern}");
            result = input;
            return false;
        }
    }

    /// <summary>
    /// As <see cref="TryRegexReplace(string, string, string, RegexOptions, TimeSpan, out string)"/>,
    /// for a pre-built <see cref="Regex"/> whose replacement is computed per match.
    /// </summary>
    private static bool TryRegexReplace(
        string input, Regex regex, MatchEvaluator evaluator, TimeSpan timeout, out string result)
    {
        try
        {
            // The compiled Regex carries its own timeout; honor a caller-supplied one that differs
            // (the tests drive a deliberately tiny value through this path).
            var effective = regex.MatchTimeout == timeout
                ? regex
                : new Regex(regex.ToString(), regex.Options & ~RegexOptions.Compiled, timeout);
            result = effective.Replace(input, evaluator);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            LogService.Log($"HTML cleanup timed out for pattern: {regex}");
            result = input;
            return false;
        }
    }

    public static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        return value[..maxChars] + "\n\n[Message truncated for display.]";
    }

    public static int CountOccurrences(
        string value, string needle, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, comparison)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
