using System;
using System.Globalization;
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

    public string PlainTextToHtml(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var sb = new StringBuilder(plainText.Length + 64);
        var paragraphs = plainText.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.None);
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length == 0) continue;
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
