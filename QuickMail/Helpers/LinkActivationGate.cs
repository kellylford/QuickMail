using System;

namespace QuickMail.Helpers;

/// <summary>
/// Decides whether a navigation out of a message body is one the user asked for (#728 security
/// review). A navigation is let through only when it matches a link the user just activated — a
/// click, or Enter on a focused link, reported by the host script as it happens.
/// <para>WebView2's <c>IsUserInitiated</c> is not enough on its own: the engine counts any keypress or
/// click inside the page as "user activity" for several seconds, and a meta refresh that fires in
/// that window reports itself as user-initiated. Reading a message with the arrow keys was enough.
/// A refresh cannot, though, make the page report a link activation — the page runs no script — so
/// matching the two closes that gap.</para>
/// <para>The two signals arrive separately and in no guaranteed order, so whichever comes second
/// completes the pair: a navigation with no activation yet waits briefly for one, and an activation
/// with no navigation yet waits for its navigation. Unmatched, either expires and nothing opens.</para>
/// </summary>
public sealed class LinkActivationGate
{
    /// <summary>
    /// Host script (injected with AddScriptToExecuteOnDocumentCreated, so the page's CSP does not
    /// apply to it) that reports each link the user activates: a click — which is also what Enter on a
    /// focused link, and a screen reader's default action, dispatch — or a middle click. Only trusted
    /// events count; the page itself runs no script and could not fake one anyway.
    /// </summary>
    public const string ReportActivationsScript =
        "(function(){function r(e){if(!e.isTrusted)return;var t=e.target;" +
        "var a=t&&t.closest?t.closest('a[href]'):null;" +
        "if(a)window.chrome.webview.postMessage('activate:'+a.href);}" +
        "document.addEventListener('click',r,true);document.addEventListener('auxclick',r,true);})();";

    /// <summary>The web message the script sends, before the link's address.</summary>
    public const string ActivationMessagePrefix = "activate:";

    /// <summary>How far apart the activation and its navigation may arrive and still pair up.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _now;
    private (string Uri, DateTime At)? _activated;
    private (string Uri, DateTime At, Action Open)? _pending;

    public LinkActivationGate(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>The host script reported that the user activated a link to <paramref name="href"/>.</summary>
    public void NoteActivated(string href)
    {
        var now = _now();
        if (_pending is { } p && now - p.At <= Window && Same(p.Uri, href))
        {
            _pending = null;
            _activated = null;
            p.Open();
            return;
        }
        _activated = (href, now);
    }

    /// <summary>
    /// A navigation to <paramref name="uri"/> is starting. Runs <paramref name="open"/> now if it
    /// matches a fresh activation, or holds it for one that may still be on its way.
    /// </summary>
    public void Request(string uri, Action open)
    {
        var now = _now();
        if (_activated is { } a && now - a.At <= Window && Same(a.Uri, uri))
        {
            _activated = null;
            open();
            return;
        }
        _pending = (uri, now, open);
    }

    /// <summary>
    /// Whether two URLs name the same place: compared in the form the engine normalizes them to, so
    /// the page's resolved <c>href</c> and the navigation's URI agree however the sender wrote it.
    /// </summary>
    internal static bool Same(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        return Uri.TryCreate(a, UriKind.Absolute, out var ua)
            && Uri.TryCreate(b, UriKind.Absolute, out var ub)
            && string.Equals(ua.AbsoluteUri, ub.AbsoluteUri, StringComparison.Ordinal);
    }
}
