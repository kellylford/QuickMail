namespace QuickMail.Views;

/// <summary>
/// The page script that puts focus inside an open message, shared by the reading pane, the message
/// window and the Markdown preview.
///
/// Two things pull against each other here, and both were heard live:
/// - Focusing the body as a <c>role="document"</c> (with or without a label) started a screen reader
///   reading on open, but with the body exposed as an object of its own, Ctrl+A, Ctrl+C copied only the
///   first paragraph (#786). The screen reader handles Ctrl+A itself in browse mode, so a page script
///   cannot repair the copy.
/// - Focusing the body with no role keeps the copy whole, but Chromium then reports focus on the page
///   itself, named by its title (the subject), in the same moment the page arrives, and whether reading
///   started was down to timing: often Tab was needed first. That is what 0.8.55 shipped.
///
/// So the body gets no role, and focus goes to the first element on screen that holds text. That is a
/// focus change to an object inside the page after it has arrived, which starts reading, without
/// wrapping the message in anything that would cut the copy short.
/// </summary>
public static class MessageBodyFocus
{
    /// <summary>
    /// Focuses the first rendered element holding text, or the body when there is none (a message of
    /// pictures only). Evaluates to true once the document has focus on that element.
    ///
    /// When focus is already on something inside the body (the user Tabbed to a link, then left with F6
    /// and came back) it stays there, so returning to a message does not throw the reader back to the
    /// top. A newly loaded message has focus on the body or nothing, and so starts at its first text.
    ///
    /// Text is skipped unless its element is rendered (<c>getClientRects</c>): mail often carries
    /// <c>&lt;style&gt;</c> inside the body, and hidden pre-header text, neither of which can take focus.
    /// </summary>
    public const string FocusScript =
        "(() => {" +
        "const body=document.body;" +
        "if(!body)return false;" +
        "window.focus();" +
        "const cur=document.activeElement;" +
        "if(cur&&cur!==body&&body.contains(cur)){cur.focus({preventScroll:true});return document.hasFocus()&&document.activeElement===cur;}" +
        "const w=document.createTreeWalker(body,NodeFilter.SHOW_TEXT,{acceptNode:function(n){" +
        "const p=n.parentElement;" +
        "return n.nodeValue.trim()&&p&&p.getClientRects().length?NodeFilter.FILTER_ACCEPT:NodeFilter.FILTER_SKIP;}});" +
        "const n=w.nextNode();" +
        "let el=n?n.parentElement:body;" +
        "if(el!==body&&el.tabIndex<0&&!el.hasAttribute('tabindex'))el.setAttribute('tabindex','-1');" +
        "if(el!==body)el.style.outline='none';" +
        "el.focus({preventScroll:true});" +
        "if(document.activeElement!==el){el=body;body.setAttribute('tabindex','0');body.focus({preventScroll:true});}" +
        "return document.hasFocus()&&document.activeElement===el;" +
        "})()";

    /// <summary>
    /// What the reading pane and the message window announce once focus lands: "Message body. Subject".
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
}
