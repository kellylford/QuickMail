namespace QuickMail.Models;

/// <summary>The editing mode of the compose body.</summary>
public enum ComposeMode
{
    /// <summary>Plain text only — sent as text/plain (default, existing behavior).</summary>
    PlainText,

    /// <summary>Markdown source in a TextBox — sent as multipart/alternative (markdown source as text/plain, rendered HTML as text/html).</summary>
    Markdown,

    /// <summary>Rich text in a RichTextBox — sent as multipart/alternative (stripped text as text/plain, serialized HTML as text/html).</summary>
    Html,
}
