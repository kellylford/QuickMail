namespace QuickMail.Models;

/// <summary>
/// The rich (HTML-mode) editor content serialized into all three body
/// representations. Produced by the View's rich editor on demand so the
/// ViewModel can convert or send without referencing any UI type.
/// </summary>
/// <param name="Html">HTML fragment serialized from the editor document.</param>
/// <param name="Markdown">Markdown equivalent of the editor document.</param>
/// <param name="PlainText">Plain-text rendering of the editor document.</param>
public sealed record RichBodySnapshot(string Html, string Markdown, string PlainText)
{
    public static readonly RichBodySnapshot Empty = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(PlainText) && string.IsNullOrWhiteSpace(Html);
}
