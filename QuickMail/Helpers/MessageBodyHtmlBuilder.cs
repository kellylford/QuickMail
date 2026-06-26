using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
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

    // CSS properties allowed through the inline style= allowlist.
    // Chosen to be safe (no network requests, no code execution) while preserving
    // sender-intended layout: visibility, spacing, color, typography, table structure.
    private static readonly HashSet<string> AllowedCssProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        // Visibility — critical: preserves display:none on preheader/quoted-reply divs
        "display", "visibility", "opacity",
        // Box model
        "width", "height", "min-width", "max-width", "min-height", "max-height",
        "overflow", "overflow-x", "overflow-y",
        // Spacing
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        // Color (background-image intentionally absent — url() risk)
        "color", "background-color",
        // Typography
        "font", "font-family", "font-size", "font-weight", "font-style", "font-variant",
        "line-height", "letter-spacing",
        "text-align", "text-decoration", "text-transform", "text-indent",
        "vertical-align", "white-space", "word-break", "word-wrap",
        // Border
        "border", "border-top", "border-right", "border-bottom", "border-left",
        "border-width", "border-style", "border-color", "border-radius", "border-collapse",
        // Table layout
        "table-layout", "border-spacing", "caption-side",
        // Flex / grid
        "flex", "flex-direction", "flex-wrap", "justify-content", "align-items", "align-self", "gap",
        // Floats
        "float", "clear",
    };

    // Patterns that make a CSS value dangerous regardless of the property name.
    private static readonly string[] DangerousValueTokens =
        ["url(", "expression(", "behavior:", "javascript:"];

    public static string BuildMessageHtml(MailMessageDetail detail)
    {
        var htmlBody = detail.HtmlBody ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(htmlBody) && !ShouldUseReaderMode(htmlBody))
            return BuildSanitizedHtmlDocument(detail.Subject, htmlBody);

        var text = !string.IsNullOrWhiteSpace(detail.PlainTextBody)
            ? detail.PlainTextBody
            : HtmlToText(htmlBody);

        var note = !string.IsNullOrWhiteSpace(htmlBody)
            ? "This message uses complex HTML, so QuickMail is showing a simplified body."
            : null;
        return BuildPlainTextHtmlDocument(detail.Subject, text, note);
    }

    public static bool ShouldUseReaderMode(string html) =>
        html.Length > MaxRichHtmlRenderChars ||
        CountOccurrences(html, "<table") > MaxRichHtmlTableCount;

    public static string BuildSanitizedHtmlDocument(string? subject, string html)
    {
        var body     = StripHeavyHtml(html);
        var titleTag = $"<title>{WebUtility.HtmlEncode(subject ?? string.Empty)}</title>";
        const string cspTag =
            "<meta http-equiv=\"Content-Security-Policy\" " +
            "content=\"default-src 'none'; script-src 'none'; object-src 'none'; " +
            "frame-src 'none'; img-src 'none'; media-src 'none'; connect-src 'none'; " +
            "form-action 'none'; base-uri 'none'; style-src 'unsafe-inline';\">";
        const string css =
            "<style>html,body{margin:0;padding:8px 12px;font-family:Segoe UI,Arial,sans-serif;" +
            "font-size:13px;line-height:1.45;word-break:break-word;background:Window;color:WindowText;}" +
            "table{max-width:100%;border-collapse:collapse;}td,th{vertical-align:top;}a{color:#0645ad;}</style>";

        var headIdx = body.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headIdx >= 0)
        {
            body = RemoveTitle(body);
            body = body.Insert(headIdx + 6, "<meta charset=\"utf-8\">" + titleTag + cspTag + css);
            return EnsureBodyFocusable(body);
        }

        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
               titleTag + cspTag + css +
               "</head><body tabindex=\"0\">" + body + "</body></html>";
    }

    public static string BuildPlainTextHtmlDocument(string? subject, string text, string? note)
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
               "<style>html,body{margin:0;padding:8px 12px;font-family:Segoe UI,Arial,sans-serif;" +
               "font-size:13px;white-space:pre-wrap;word-break:break-word;background:Window;color:WindowText;}" +
               "a{color:#0645ad;}" +
               ".note{white-space:normal;border-left:3px solid #777;padding-left:8px;margin:0 0 12px 0;color:#555;}</style>" +
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

    public static string StripHeavyHtml(string html)
    {
        var body = html;

        // 1. Remove elements hidden via inline display:none — must run before style filtering.
        //    Covers all element types (not just div/span/p) so <td>, <section>, etc. are handled.
        body = SafeRegexReplace(body,
            @"<([a-zA-Z][\w-]*)\b[^>]*\bstyle\s*=\s*(?:""[^""]*display\s*:\s*none[^""]*""|'[^']*display\s*:\s*none[^']*')[^>]*>.*?</\1>",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 2. Remove elements hidden via inline visibility:hidden.
        body = SafeRegexReplace(body,
            @"<([a-zA-Z][\w-]*)\b[^>]*\bstyle\s*=\s*(?:""[^""]*visibility\s*:\s*hidden[^""]*""|'[^']*visibility\s*:\s*hidden[^']*')[^>]*>.*?</\1>",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 3. Strip HTML comments.
        body = SafeRegexReplace(body, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        // 4. Strip <script> blocks entirely.
        body = SafeRegexReplace(body, "<script\\b.*?</script>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 5. Sanitize <style> blocks — strip dangerous rules, preserve safe layout rules.
        body = SafeRegexReplace(body,
            @"<style\b[^>]*>(.*?)</style>",
            m =>
            {
                var cleaned = CleanStyleBlock(m.Groups[1].Value);
                return string.IsNullOrWhiteSpace(cleaned) ? string.Empty : $"<style>{cleaned}</style>";
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 6. Strip dangerous embedded content.
        body = SafeRegexReplace(body, "<svg\\b.*?</svg>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<(iframe|object|embed|video|audio|canvas|form)\\b.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 7. Strip void elements that pull external resources or create interactivity.
        body = SafeRegexReplace(body, "<(img|link|base|input|button|meta)\\b[^>]*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 8. Strip event handlers and resource-loading attributes wholesale.
        //    Note: 'style' is intentionally absent here — handled by step 9 via allowlist.
        body = SafeRegexReplace(body, "\\s(on\\w+|src|srcset|background)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 9. Filter inline style= attributes via CSS property allowlist.
        //    Safe layout properties (display, color, font-*, margin, etc.) are preserved.
        //    Dangerous properties (position, content, background-image) and any value
        //    containing url() / expression() / behavior: are removed.
        body = SafeRegexReplace(body,
            @"\bstyle\s*=\s*(?:""([^""]*)""|'([^']*)')",
            m =>
            {
                var value    = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                var filtered = FilterStyleAttribute(value);
                return filtered is null ? string.Empty : $"style=\"{filtered}\"";
            },
            RegexOptions.IgnoreCase);

        return body;
    }

    /// <summary>
    /// Filters an inline CSS style attribute value through the property allowlist.
    /// Returns the sanitized declaration string, or null if no declarations survive.
    /// </summary>
    internal static string? FilterStyleAttribute(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 4000) return null; // Abnormally long; drop entirely.

        var sb = new StringBuilder();
        foreach (var part in value.Split(';'))
        {
            var decl = part.Trim();
            if (decl.Length == 0) continue;

            var colon = decl.IndexOf(':');
            if (colon < 0) continue;

            var property = decl[..colon].Trim();
            var val      = decl[(colon + 1)..].Trim();

            if (!AllowedCssProperties.Contains(property)) continue;

            var dangerous = false;
            foreach (var token in DangerousValueTokens)
            {
                if (val.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    dangerous = true;
                    break;
                }
            }
            if (dangerous) continue;

            if (sb.Length > 0) sb.Append("; ");
            sb.Append(property).Append(": ").Append(val);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Removes dangerous content from a CSS style block while preserving safe rules.
    /// Strips @import, url() references, expression(), background-image, and -moz-binding.
    /// Preserves class rules, ID rules, media queries, and safe typography declarations.
    /// </summary>
    internal static string CleanStyleBlock(string css)
    {
        if (string.IsNullOrWhiteSpace(css)) return string.Empty;

        // Strip CSS comments (may conceal dangerous content).
        css = SafeRegexReplace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        // Remove @import rules (external stylesheet loading).
        css = SafeRegexReplace(css, @"@import\b[^;]*;?", string.Empty, RegexOptions.IgnoreCase);

        // Remove any declaration whose value contains a dangerous token.
        css = SafeRegexReplace(css,
            @"[\w-]+\s*:[^;{}]*(?:url\s*\(|expression\s*\(|behavior\s*:|javascript\s*:)[^;{}]*;?",
            string.Empty, RegexOptions.IgnoreCase);

        // Remove background-image and -moz-binding unconditionally.
        css = SafeRegexReplace(css, @"background-image\s*:[^;{}]*;?", string.Empty, RegexOptions.IgnoreCase);
        css = SafeRegexReplace(css, @"-moz-binding\s*:[^;{}]*;?", string.Empty, RegexOptions.IgnoreCase);

        return css.Trim();
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
        try { return Regex.Replace(input, pattern, replacement, options, HtmlRegexTimeout); }
        catch (RegexMatchTimeoutException)
        {
            LogService.Log($"HTML cleanup timed out for pattern: {pattern}");
            return input;
        }
    }

    public static string SafeRegexReplace(string input, string pattern, MatchEvaluator evaluator, RegexOptions options)
    {
        try { return Regex.Replace(input, pattern, evaluator, options, HtmlRegexTimeout); }
        catch (RegexMatchTimeoutException)
        {
            LogService.Log($"HTML cleanup timed out for pattern: {pattern}");
            return input;
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
