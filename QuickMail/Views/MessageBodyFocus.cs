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
    /// TEST BUILD ONLY: which way of focusing the body to use, from <c>--readFocus=a</c> or
    /// <c>--readFocus=b</c> on the command line, so one build can compare both by ear. Default a.
    /// </summary>
    public static readonly string Variant = ReadVariant();

    private static string ReadVariant()
    {
        foreach (var arg in System.Environment.GetCommandLineArgs())
            if (arg.StartsWith("--readFocus=", System.StringComparison.OrdinalIgnoreCase))
                return arg["--readFocus=".Length..].Trim().ToLowerInvariant();
        return "a";
    }

    /// <summary>
    /// Script that focuses the body, evaluating to true once it has focus.
    ///
    /// Variant a: the body is focused as a labelled document, so reading starts, and the role and label
    /// come off again 1.5 seconds later, so that Ctrl+A, Ctrl+C sees a body that is not an object of its
    /// own. Variant b: the body never gets a role; focus goes to the first element holding text, so the
    /// focus moves to an object inside the page after it has arrived, without wrapping the message.
    /// </summary>
    public static string FocusBodyScript(string? label) =>
        Variant == "b" ? FocusFirstTextScript : FocusAsDocumentScript(label);

    internal static string FocusAsDocumentScript(string? label) =>
        "(() => {" +
        "const body=document.body;" +
        "if(!body)return false;" +
        "window.focus();" +
        "body.setAttribute('tabindex','0');" +
        "body.setAttribute('role','document');" +
        (string.IsNullOrEmpty(label) ? "" : $"body.setAttribute('aria-label',{JsonSerializer.Serialize(label)});") +
        "body.focus({preventScroll:true});" +
        "clearTimeout(window.__qmRoleTimer);" +
        "window.__qmRoleTimer=setTimeout(function(){body.removeAttribute('role');body.removeAttribute('aria-label');},1500);" +
        "return document.hasFocus()&&document.activeElement===body;" +
        "})()";

    internal const string FocusFirstTextScript =
        "(() => {" +
        "const body=document.body;" +
        "if(!body)return false;" +
        "window.focus();" +
        "const w=document.createTreeWalker(body,NodeFilter.SHOW_TEXT,{acceptNode:function(n){return n.nodeValue.trim()?NodeFilter.FILTER_ACCEPT:NodeFilter.FILTER_SKIP;}});" +
        "const n=w.nextNode();" +
        "let el=n&&n.parentElement?n.parentElement:body;" +
        "if(el.tabIndex<0&&!el.hasAttribute('tabindex'))el.setAttribute('tabindex','-1');" +
        "if(el===body)body.setAttribute('tabindex','0');" +
        "el.style.outline='none';" +
        "el.focus({preventScroll:true});" +
        "return document.hasFocus()&&document.activeElement===el;" +
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
