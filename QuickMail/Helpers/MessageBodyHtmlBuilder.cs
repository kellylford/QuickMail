using System;
using System.Net;
using System.Text.RegularExpressions;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Shared HTML rendering helpers for the reading pane and standalone MessageWindow.
/// All methods are pure static; no DI required.
/// </summary>
public static class MessageBodyHtmlBuilder
{
    public static readonly TimeSpan HtmlRegexTimeout = TimeSpan.FromMilliseconds(500);
    public const int MaxRichHtmlRenderChars = 1_000_000;
    public const int MaxRichHtmlTableCount  = 500;
    public const int MaxReaderTextChars     = 140_000;

    private static readonly Regex AutoLinkUrl = new(
        @"\b((?:https?|mailto):[^\s<>""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        HtmlRegexTimeout);

    private static readonly char[] AutoLinkTrailingPunct =
        ['.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '\''];

    /// <param name="themeCss">
    /// Optional theme CSS from <see cref="IThemeService.BuildMessageCss"/> — the
    /// <c>:root { --qm-* }</c> variable block. When null the document's CSS
    /// variables are absent and the <c>var(--qm-*, fallback)</c> declarations
    /// resolve to their system-color fallbacks (the pre-theming behavior).
    /// </param>
    public static string BuildMessageHtml(MailMessageDetail detail, string? themeCss = null)
    {
        var htmlBody = detail.HtmlBody ?? string.Empty;
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
    /// The theme <c>:root</c> variable block as a style tag, or empty. Emitted
    /// before the default CSS so the <c>var(--qm-*)</c> references resolve.
    /// </summary>
    private static string ThemeStyleTag(string? themeCss) =>
        string.IsNullOrEmpty(themeCss) ? string.Empty : "<style>" + themeCss + "</style>";

    private static string ComposeSanitizedDocument(string? subject, string body, string? themeCss)
    {
        var titleTag = $"<title>{WebUtility.HtmlEncode(subject ?? string.Empty)}</title>";
        const string cspTag =
            "<meta http-equiv=\"Content-Security-Policy\" " +
            "content=\"default-src 'none'; script-src 'none'; object-src 'none'; " +
            "frame-src 'none'; img-src 'none'; media-src 'none'; connect-src 'none'; " +
            "form-action 'none'; base-uri 'none'; style-src 'unsafe-inline';\">";
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

        var headIdx = body.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headIdx >= 0)
        {
            body = RemoveTitle(body);
            body = body.Insert(headIdx + 6, "<meta charset=\"utf-8\">" + titleTag + cspTag + styleBlock);
            return EnsureBodyFocusable(body);
        }

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
    /// this stripping protects the document. Returns false when any pass timed out, in
    /// which case <paramref name="stripped"/> holds a PARTIALLY stripped document that
    /// must not be rendered.
    /// </summary>
    internal static bool TryStripHeavyHtml(string html, TimeSpan timeout, out string stripped)
    {
        var complete = true;
        string Step(string input, string pattern, RegexOptions options, string replacement = "")
        {
            if (!TryRegexReplace(input, pattern, replacement, options, timeout, out var result))
                complete = false;
            return result;
        }

        var body = html;
        // Remove elements hidden via inline display:none (e.g. newsletter preheader padding divs).
        // Must run before style-attribute stripping, which would make these visible.
        body = Step(body,
            @"<(div|span|p)\b[^>]*\bstyle\s*=\s*(?:""[^""]*display\s*:\s*none[^""]*""|'[^']*display\s*:\s*none[^']*')[^>]*>.*?</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Step(body, "<!--.*?-->", RegexOptions.Singleline);
        body = Step(body, "<script\\b.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Step(body, "<style\\b.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Step(body, "<svg\\b.*?</svg>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Step(body, "<(iframe|object|embed|video|audio|canvas|form)\\b.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Step(body, "<(img|link|base|input|button|meta)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Step(body, "\\s(on\\w+|style|src|srcset|background)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        stripped = body;
        return complete;
    }

    public static string RemoveTitle(string html) =>
        SafeRegexReplace(html, "<title[^>]*>.*?</title>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static string EnsureBodyFocusable(string html)
    {
        var bodyIdx = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyIdx < 0) return html;
        // Scope the check to the opening tag only — attribute values elsewhere in the
        // document can contain the substring "tabindex=" and must not trigger a false positive.
        var tagEnd  = html.IndexOf('>', bodyIdx);
        var openTag = tagEnd >= 0 ? html[bodyIdx..tagEnd] : html[bodyIdx..];
        if (openTag.Contains("tabindex=", StringComparison.OrdinalIgnoreCase))
            return html;
        return SafeRegexReplace(html, "<body\\b", "<body tabindex=\"0\"", RegexOptions.IgnoreCase);
    }

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
