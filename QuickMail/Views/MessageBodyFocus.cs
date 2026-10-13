using System.Text.Json;

namespace QuickMail.Views;

/// <summary>
/// The page scripts that put focus on an open message's body, shared by the reading pane, the message
/// window and the Markdown preview.
///
/// The body is given <c>role="document"</c> before it is focused. Without it Chromium does not expose
/// the body as an object of its own, so focusing it reports focus on the page — named by its title, the
/// subject — in the same moment the new page arrives, and whether a screen reader then started reading
/// was down to timing: often the user had to press Tab first. That is what 0.8.55 shipped, having
/// removed the role because it cut Ctrl+A, Ctrl+C down to the first paragraph (#786).
///
/// <see cref="SelectAllScript"/> does Ctrl+A itself so the copy can stay whole. Whether that is enough
/// depends on the screen reader passing Ctrl+A to the page: one that handles it in its own browse
/// mode never lets the script run, and has to be checked live.
/// </summary>
public static class MessageBodyFocus
{
    /// <summary>
    /// Script that focuses the body as a document, with <paramref name="label"/> as its name when one is
    /// given. Evaluates to true once the document has focus and the body is its active element.
    /// </summary>
    public static string FocusBodyScript(string? label) =>
        "(() => {" +
        "const body=document.body;" +
        "if(!body)return false;" +
        "window.focus();" +
        "body.setAttribute('tabindex','0');" +
        "body.setAttribute('role','document');" +
        (string.IsNullOrEmpty(label) ? "" : $"body.setAttribute('aria-label',{JsonSerializer.Serialize(label)});") +
        "body.focus({preventScroll:true});" +
        "return document.hasFocus()&&document.activeElement===body;" +
        "})()";

    /// <summary>
    /// The name the reading pane and the message window give the body: "Message body. Subject".
    /// </summary>
    public static string Label(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "Message body";

        var trimmed = subject.Trim();
        if (trimmed.Length > 120)
            trimmed = trimmed[..120] + "...";

        return $"Message body. {trimmed}";
    }

    /// <summary>
    /// Ctrl+A selects the whole body through the Selection API. With a screen reader running and a role
    /// or label on the body, Ctrl+A, Ctrl+C copied only the first paragraph (#786); without a screen
    /// reader Chromium's own select-all takes everything either way, so where the cut came from is not
    /// known. Selecting the body's children does not depend on the role. Injected at document creation,
    /// so the message's CSP does not apply. A field that takes text keeps its own Ctrl+A. Matched on the
    /// key's position, not its character, so it works on any keyboard layout, as Chromium's own does.
    /// </summary>
    public const string SelectAllScript =
        "window.addEventListener('keydown',function(e){" +
        "if(!e.ctrlKey||e.shiftKey||e.altKey||e.metaKey||e.code!=='KeyA')return;" +
        "var t=e.target;" +
        "if(t&&(t.isContentEditable||/^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)))return;" +
        "var b=document.body;" +
        "if(!b)return;" +
        "e.preventDefault();" +
        "window.getSelection().selectAllChildren(b);" +
        "},true);";
}
