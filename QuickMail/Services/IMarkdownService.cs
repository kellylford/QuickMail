namespace QuickMail.Services;

/// <summary>
/// Converts between the three compose body representations:
/// Markdown source, HTML, and plain text.
/// </summary>
public interface IMarkdownService
{
    /// <summary>Renders Markdown source to an HTML fragment (no html/body wrapper).</summary>
    string ToHtml(string markdown);

    /// <summary>Renders Markdown to HTML, then strips tags — used when downgrading Markdown → plain text.</summary>
    string ToPlainText(string markdown);

    /// <summary>Strips tags and decodes entities — used for the text/plain part of HTML-mode messages.</summary>
    string HtmlToPlainText(string html);

    /// <summary>Escapes plain text and wraps paragraphs in &lt;p&gt; — used when upgrading plain text → HTML mode.</summary>
    string PlainTextToHtml(string plainText);

    /// <summary>
    /// Wraps an HTML fragment in a complete, valid HTML5 document (doctype, lang,
    /// charset, title) with default email styling for the text/html MIME part.
    /// <paramref name="title"/> is the document title — pass the message subject.
    /// </summary>
    string WrapDocument(string htmlFragment, string? title = null);
}
