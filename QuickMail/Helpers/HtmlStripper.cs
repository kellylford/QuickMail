using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace QuickMail.Helpers;

/// <summary>
/// Converts HTML to readable plain text for the text/plain part of
/// multipart/alternative messages and for rich → plain mode conversion.
/// No external parser: a single forward scan that understands the bounded
/// HTML subset the compose pipeline produces (Markdig output and serialized
/// RichTextBox content), and degrades gracefully on anything else.
/// </summary>
public static class HtmlStripper
{
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var sb = new StringBuilder(html.Length);
        // Stack of list contexts: counter < 0 means unordered, >= 0 is the next ordinal.
        var listStack = new Stack<int>();
        int linkTextStart = -1;
        string? linkHref = null;
        int i = 0;

        while (i < html.Length)
        {
            var c = html[i];
            if (c != '<') { sb.Append(c); i++; continue; }

            int end = html.IndexOf('>', i + 1);
            if (end < 0) { sb.Append(html, i, html.Length - i); break; }

            var tag = html.Substring(i + 1, end - i - 1);
            i = end + 1;

            var isClosing = tag.StartsWith('/');
            var name = TagName(tag);

            switch (name)
            {
                case "script" or "style" or "head" when !isClosing:
                    // Skip the element's entire content.
                    var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                    if (close < 0) { i = html.Length; break; }
                    var closeEnd = html.IndexOf('>', close);
                    i = closeEnd < 0 ? html.Length : closeEnd + 1;
                    break;

                case "br":
                    sb.Append('\n');
                    break;

                case "hr":
                    AppendBlockBreak(sb);
                    sb.Append("----------\n");
                    break;

                case "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                     or "table" or "blockquote" or "pre":
                    AppendBlockBreak(sb);
                    break;

                case "tr":
                    if (isClosing) sb.Append('\n');
                    break;

                case "td" or "th":
                    if (isClosing) sb.Append('\t');
                    break;

                case "ul":
                    if (isClosing) { if (listStack.Count > 0) listStack.Pop(); AppendBlockBreak(sb); }
                    else listStack.Push(-1);
                    break;

                case "ol":
                    if (isClosing) { if (listStack.Count > 0) listStack.Pop(); AppendBlockBreak(sb); }
                    else listStack.Push(1);
                    break;

                case "li":
                    if (isClosing) break;
                    if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                    if (listStack.Count > 0 && listStack.Peek() >= 0)
                    {
                        var n = listStack.Pop();
                        listStack.Push(n + 1);
                        sb.Append(n).Append(". ");
                    }
                    else
                    {
                        sb.Append("• ");
                    }
                    break;

                case "a":
                    if (!isClosing)
                    {
                        linkHref = ExtractAttribute(tag, "href");
                        linkTextStart = sb.Length;
                    }
                    else if (linkHref != null && linkTextStart >= 0)
                    {
                        var text = sb.ToString(linkTextStart, sb.Length - linkTextStart).Trim();
                        var decodedHref = WebUtility.HtmlDecode(linkHref);
                        if (decodedHref.Length > 0
                            && !decodedHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                            && !decodedHref.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(text, decodedHref, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals("mailto:" + text, decodedHref, StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append(" (").Append(decodedHref).Append(')');
                        }
                        linkHref = null;
                        linkTextStart = -1;
                    }
                    break;

                case "img":
                    var alt = ExtractAttribute(tag, "alt");
                    if (!string.IsNullOrWhiteSpace(alt))
                        sb.Append('[').Append(WebUtility.HtmlDecode(alt)).Append(']');
                    break;
            }
        }

        var decoded = WebUtility.HtmlDecode(sb.ToString());
        return CollapseWhitespace(decoded);
    }

    private static string TagName(string tag)
    {
        int start = tag.StartsWith('/') ? 1 : 0;
        int end = start;
        while (end < tag.Length && (char.IsLetterOrDigit(tag[end]))) end++;
        return tag[start..end].ToLowerInvariant();
    }

    private static string? ExtractAttribute(string tag, string attribute)
    {
        var idx = tag.IndexOf(attribute + "=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        // Word-boundary check so e.g. data-href= doesn't match href=.
        if (idx > 0 && (char.IsLetterOrDigit(tag[idx - 1]) || tag[idx - 1] == '-')) return null;
        int v = idx + attribute.Length + 1;
        if (v >= tag.Length) return null;
        char quote = tag[v];
        if (quote is '"' or '\'')
        {
            int endQuote = tag.IndexOf(quote, v + 1);
            return endQuote < 0 ? tag[(v + 1)..] : tag[(v + 1)..endQuote];
        }
        int endSpace = tag.IndexOf(' ', v);
        return endSpace < 0 ? tag[v..] : tag[v..endSpace];
    }

    private static void AppendBlockBreak(StringBuilder sb)
    {
        if (sb.Length == 0) return;
        if (sb[^1] != '\n') { sb.Append("\n\n"); return; }
        if (sb.Length == 1 || sb[^2] != '\n') sb.Append('\n');
    }

    /// <summary>Trims each line's trailing whitespace and collapses 3+ consecutive newlines to 2.</summary>
    private static string CollapseWhitespace(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(text.Length);
        int blankRun = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                blankRun++;
                if (blankRun > 1) continue;
            }
            else
            {
                blankRun = 0;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim('\n');
    }
}
