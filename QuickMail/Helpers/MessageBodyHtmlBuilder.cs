using System;
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
    public static string BuildMessageHtml(MailMessageDetail detail, string? themeCss = null,
        bool forcePlainText = false, IThemeService? themeService = null)
    {
        var document = BuildBodyDocument(detail, themeCss, forcePlainText);
        var card = EventCardHtmlBuilder.Build(detail.CalendarInvite, themeService);
        return card.Length == 0 ? document : InjectEventCard(document, card);
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

    private static string BuildBodyDocument(MailMessageDetail detail, string? themeCss, bool forcePlainText)
    {
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
            && TryBuildSanitizedHtmlDocument(detail.Subject, htmlBody, themeCss, out var sanitized))
            return sanitized;

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
        string? subject, string html, string? themeCss, TimeSpan timeout, out string document)
    {
        if (!TryStripHeavyHtml(html, timeout, out var body))
        {
            document = string.Empty;
            return false;
        }
        document = ComposeSanitizedDocument(subject, body, themeCss);
        return true;
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
    public static bool TryBuildSanitizedBodyFragment(string html, out string fragment)
    {
        if (!TryStripHeavyHtml(html, HtmlRegexTimeout, out var body))
        {
            fragment = string.Empty;
            return false;
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

    private static string ComposeSanitizedDocument(string? subject, string body, string? themeCss)
    {
        var titleTag = $"<title>{WebUtility.HtmlEncode(subject ?? string.Empty)}</title>";
        const string cspTag =
            "<meta http-equiv=\"Content-Security-Policy\" content=\"" + StrictCspContent + "\">";
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
        stripped = body;
        return complete;
    }

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
