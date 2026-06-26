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
        // Remove elements hidden via inline display:none (e.g. newsletter preheader padding divs).
        // Must run before style-attribute stripping, which would make these visible.
        body = SafeRegexReplace(body,
            @"<(div|span|p)\b[^>]*\bstyle\s*=\s*(?:""[^""]*display\s*:\s*none[^""]*""|'[^']*display\s*:\s*none[^']*')[^>]*>.*?</\1>",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<script\\b.*?</script>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<style\\b.*?</style>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<svg\\b.*?</svg>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<(iframe|object|embed|video|audio|canvas|form)\\b.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<(img|link|base|input|button|meta)\\b[^>]*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "\\s(on\\w+|style|src|srcset|background)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return body;
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
