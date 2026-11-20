using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using QuickMail.Helpers;

namespace QuickMail.Services;

/// <summary>
/// Markdig-based implementation of <see cref="IMarkdownService"/>.
/// Stateless and thread-safe; the pipeline is built once.
/// </summary>
public sealed class MarkdownService : IMarkdownService
{
    // Explicit, bounded extension set: pipe tables, strikethrough, and auto-links.
    // This is deliberately narrower than UseAdvancedExtensions so every construct
    // the pipeline can emit round-trips losslessly through the rich editor and
    // produces accessible markup (e.g. task lists are excluded because they render
    // as unlabeled <input> checkboxes, which fail WCAG 4.1.2 and are stripped by
    // most mail clients). Soft line breaks render as <br /> (email convention —
    // people expect their line breaks to survive). Raw HTML in Markdown is disabled
    // so pasted markup cannot smuggle script or active content into the rendered body.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    public string ToHtml(string markdown) =>
        string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, Pipeline);

    public string ToPlainText(string markdown) =>
        HtmlStripper.ToPlainText(ToHtml(markdown));

    public string HtmlToPlainText(string html) =>
        HtmlStripper.ToPlainText(html);

    /// <summary>
    /// Plain text as HTML paragraphs. Lines that begin with "&gt;" — the way plain
    /// text has always marked quoted mail, and how a reply quotes the original —
    /// become a real <c>&lt;blockquote&gt;</c>, nested by the number of markers.
    /// Otherwise a reply switched to HTML sends literal "&gt;" characters.
    /// </summary>
    public string PlainTextToHtml(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var lines = plainText.Replace("\r\n", "\n").Split('\n');
        var parsed = lines.Select(line =>
        {
            var prefix = MarkdownEditing.QuotePrefixLength(line, out int depth);
            return (Depth: depth, Text: line[prefix..]);
        }).ToList();
        if (parsed.All(l => l.Depth == 0))
            return ParagraphsHtml(plainText.Replace("\r\n", "\n"));

        var sb = new StringBuilder(plainText.Length + 64);
        int open = 0;
        int i = 0;
        while (i < parsed.Count)
        {
            // A run of lines at one quote depth is rendered as paragraphs at that depth.
            int depth = parsed[i].Depth;
            var run = new List<string>();
            while (i < parsed.Count && parsed[i].Depth == depth)
                run.Add(parsed[i++].Text);

            // Blank lines that only separate the run from a quote are not content.
            // Leading blank lines at the very start are kept: a reply opens with them
            // so the caret has somewhere to type above the quote.
            while (run.Count > 0 && run[^1].Length == 0)
                run.RemoveAt(run.Count - 1);
            if (depth > 0 || sb.Length > 0)
                while (run.Count > 0 && run[0].Length == 0)
                    run.RemoveAt(0);
            if (run.Count == 0) continue;

            for (; open < depth; open++) sb.Append("<blockquote>\n");
            for (; open > depth; open--) sb.Append("</blockquote>\n");
            sb.Append(ParagraphsHtml(string.Join("\n", run)));
        }
        for (; open > 0; open--) sb.Append("</blockquote>\n");
        return sb.ToString();
    }

    private static string ParagraphsHtml(string text)
    {
        var sb = new StringBuilder(text.Length + 32);
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.None))
        {
            if (paragraph.Length == 0) { sb.Append("<p><br /></p>\n"); continue; }
            sb.Append("<p>");
            sb.Append(WebUtility.HtmlEncode(paragraph).Replace("\n", "<br />"));
            sb.Append("</p>\n");
        }
        return sb.ToString();
    }

    public string WrapDocument(string htmlFragment, string? title = null)
    {
        // A complete, valid HTML5 document: doctype, lang (WCAG 3.1.1), charset,
        // and a title (WCAG 2.4.2) taken from the message subject when available.
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (string.IsNullOrWhiteSpace(lang)) lang = "en";
        var docTitle = string.IsNullOrWhiteSpace(title) ? "Email message" : title;

        return "<!DOCTYPE html>\n"
            + $"<html lang=\"{WebUtility.HtmlEncode(lang)}\">\n"
            + "<head>\n"
            + "<meta charset=\"utf-8\" />\n"
            + $"<title>{WebUtility.HtmlEncode(docTitle)}</title>\n"
            + "</head>\n"
            + "<body style=\"font-family: Segoe UI, Arial, sans-serif; font-size: 13px;\">\n"
            + htmlFragment
            + "\n</body>\n</html>";
    }
}
