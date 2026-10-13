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
/// So the body gets no role, and this script moves focus to the first element on screen that holds text:
/// a focus change to an object inside the page after it has arrived. The host's own focus step then runs
/// (<c>TabInto</c> in <c>FocusMessageBodyHost</c>) and DOM focus ends on the body, which the message
/// builders make focusable. Measured through UI Automation in a test VM, for HTML and plain-text
/// messages alike: the focused element afterwards is the body, with the first text inside it. That
/// sequence (into the page, then onto the body) is what was heard to start reading on open, and it
/// leaves focus on nothing that wraps less than the whole message, which is what Ctrl+A needs.
/// Do not make focus stay on the first text without listening to it: a focused element around one line
/// may limit a screen reader's select-all the way the document role did.
/// </summary>
public static class MessageBodyFocus
{
    /// <summary>
    /// Focuses the first element on screen that holds text, or the body when there is none (a message of
    /// pictures only). Evaluates to true once the document has focus there.
    ///
    /// Text sitting directly in the body (every plain-text message, and HTML whose sender wrote text
    /// straight into it) has no element of its own, and focusing the body is exactly what left the reader
    /// on the page title. So its first line is wrapped in a span and that is focused: one line, never the
    /// whole message, since a wrapper around everything is what cut Ctrl+A short.
    ///
    /// Passed over: text in an element that is not rendered (a <c>&lt;style&gt;</c> in the body, a
    /// hidden pre-header), invisible, inert or hidden from screen readers, and text that is only
    /// whitespace or zero-width filler. If an element still refuses focus, its changes are undone and
    /// the next text is tried.
    ///
    /// When focus is already on something inside the body (the user Tabbed to a link, then left with F6
    /// and came back) it stays there. That keeps only a place DOM focus records: moving through the
    /// message with the screen reader's own reading keys does not move it, so F6 back from there returns
    /// to the focused element, as it always has. A newly loaded message starts at its first text.
    /// </summary>
    public const string FocusScript =
        "(() => {" +
        "const body=document.body;" +
        "if(!body)return false;" +
        "window.focus();" +
        "const cur=document.activeElement;" +
        "if(cur&&cur!==body&&body.contains(cur)){cur.focus({preventScroll:true});return document.hasFocus()&&document.activeElement===cur;}" +
        @"const ws=/[\s\u200B-\u200D\u034F\u00AD\uFEFF]/;" +
        @"const blank=/^[\s\u200B-\u200D\u034F\u00AD\uFEFF]*$/;" +
        "const shown=function(p){return p.getClientRects().length>0" +
        "&&!p.closest('[inert],[aria-hidden=\"true\"]')" +
        "&&(!p.checkVisibility||p.checkVisibility({visibilityProperty:true}));};" +
        "const w=document.createTreeWalker(body,NodeFilter.SHOW_TEXT,{acceptNode:function(t){" +
        "const p=t.parentElement;" +
        "return p&&!blank.test(t.nodeValue)&&shown(p)?NodeFilter.FILTER_ACCEPT:NodeFilter.FILTER_SKIP;}});" +
        "let n,tries=0;" +
        "while((n=w.nextNode())&&tries++<50){" +
        "let el=n.parentElement,wrapped=false;" +
        "if(el===body){" +
        "let i=0;while(i<n.nodeValue.length&&ws.test(n.nodeValue[i]))i++;" +
        "if(i>0)n=n.splitText(i);" +
        @"const e=n.nodeValue.indexOf('\n');" +
        "if(e>0)n.splitText(e);" +
        "el=document.createElement('span');" +
        "n.parentNode.insertBefore(el,n);el.appendChild(n);wrapped=true;}" +
        "const added=!el.hasAttribute('tabindex')&&el.tabIndex<0;" +
        "const outline=el.style.outline;" +
        "if(added){el.setAttribute('tabindex','-1');el.style.outline='none';}" +
        "el.focus({preventScroll:true});" +
        "if(document.activeElement===el)return document.hasFocus();" +
        "if(added){el.removeAttribute('tabindex');el.style.outline=outline;}" +
        "if(wrapped){el.parentNode.insertBefore(el.firstChild,el);el.remove();break;}" +
        "}" +
        "body.setAttribute('tabindex','0');" +
        "body.focus({preventScroll:true});" +
        "return document.hasFocus()&&document.activeElement===body;" +
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
