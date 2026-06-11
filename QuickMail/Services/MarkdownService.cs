using System;
using System.Net;
using System.Text;
using Markdig;
using QuickMail.Helpers;

namespace QuickMail.Services;

/// <summary>
/// Markdig-based implementation of <see cref="IMarkdownService"/>.
/// Stateless and thread-safe; the pipeline is built once.
/// </summary>
public sealed class MarkdownService : IMarkdownService
{
    // Advanced extensions: tables, fenced code, strikethrough, task lists, auto-links.
    // Soft line breaks render as <br> (email convention — people expect their line
    // breaks to survive). Raw HTML in Markdown is disabled so pasted markup cannot
    // smuggle script or active content into the rendered body.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
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

    public string WrapDocument(string htmlFragment) =>
        "<html><body style=\"font-family: Segoe UI, Arial, sans-serif; font-size: 13px;\">\n"
        + htmlFragment
        + "\n</body></html>";
}
