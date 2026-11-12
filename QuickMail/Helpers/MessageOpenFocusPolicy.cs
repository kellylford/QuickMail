using System;

namespace QuickMail.Helpers;

/// <summary>What the focus script reported for one attempt (issue #959).</summary>
public enum FocusScriptResult
{
    /// <summary>The script put focus in this render's document (it returned true).</summary>
    Focused,
    /// <summary>This render's document is showing, but focus did not land (it returned false).</summary>
    NotFocused,
    /// <summary>The WebView2 is still showing an earlier document, or this one has not been parsed yet.</summary>
    Stale,
    /// <summary>The script threw or returned something unexpected.</summary>
    Error,
}

/// <summary>What the message-open focus loop does next.</summary>
public enum FocusDecision
{
    /// <summary>Focus has landed in this render's document: stop trying.</summary>
    Focus,
    /// <summary>Wait a moment and try again.</summary>
    Retry,
    /// <summary>The time allowed is up: stop trying.</summary>
    GiveUp,
}

/// <summary>
/// When a newly opened message is ready to be focused (issue #959, #851).
///
/// Before this, opening a message waited for NavigationCompleted or four seconds, whichever came first,
/// and then tried the focus script five times, 100 ms apart. NavigationCompleted waits for the page's
/// load event, and that waited for the message's pictures, which can mean downloading the whole
/// original over a busy connection. Past four seconds, focus went into a page still loading, and a
/// screen reader read the page title (the subject) and went no further. Escape and Enter again worked
/// because by then the pictures were in memory.
///
/// Now the page carries a token naming the render it belongs to, the focus script answers "stale" when
/// the WebView2 is showing anything else, and only this render's own NavigationCompleted (matched by
/// NavigationId) counts. The script is run once that completion arrives, or once <see cref="LoadGrace"/>
/// has passed without it, and retried until it lands or <see cref="Budget"/> runs out.
///
/// Kept a pure function so the decision can be tested without a WebView2.
/// </summary>
public static class MessageOpenFocusPolicy
{
    /// <summary>How long to wait for this render's NavigationCompleted before focusing a page that has not finished loading.</summary>
    public static readonly TimeSpan LoadGrace = TimeSpan.FromSeconds(3);

    /// <summary>The longest the whole attempt may take, from the moment the document was handed to the WebView2.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>The pause between attempts.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>A ceiling on attempts, whatever the clock says.</summary>
    public const int MaxAttempts = 60;

    /// <summary>
    /// How many times this render's own page may refuse focus (or the script fail) before giving up:
    /// five, as before #959. Only a stale page is waited for up to <see cref="Budget"/>; a page that is
    /// this render's and will not take focus (the window is not in the foreground, say) gives up as
    /// quickly as it always did, so the announcement after it is not held back.
    /// </summary>
    public const int MaxUnfocusedAttempts = 5;

    /// <summary>The next step after an attempt that produced <paramref name="result"/>.</summary>
    /// <param name="result">What the script reported.</param>
    /// <param name="attempt">Zero-based number of this attempt, stale ones included.</param>
    /// <param name="unfocusedAttempts">How many attempts so far, this one included, were this render's page refusing focus or the script failing.</param>
    /// <param name="elapsed">Time since the document was handed to the WebView2.</param>
    /// <remarks>
    /// The script is first run once this render's NavigationCompleted has arrived or
    /// <see cref="LoadGrace"/> has passed without it. A stale answer is retried even after that
    /// completion: the token, not the event, is what proves the page on show is this one.
    /// </remarks>
    public static FocusDecision Next(FocusScriptResult result, int attempt, int unfocusedAttempts, TimeSpan elapsed)
    {
        if (result == FocusScriptResult.Focused)
            return FocusDecision.Focus;
        if (elapsed >= Budget || attempt + 1 >= MaxAttempts || unfocusedAttempts >= MaxUnfocusedAttempts)
            return FocusDecision.GiveUp;
        return FocusDecision.Retry;
    }

    /// <summary>Reads the JSON <c>ExecuteScriptAsync</c> returned for the focus script.</summary>
    public static FocusScriptResult Parse(string? json) => json?.Trim() switch
    {
        "true" => FocusScriptResult.Focused,
        "false" => FocusScriptResult.NotFocused,
        "\"stale\"" => FocusScriptResult.Stale,
        _ => FocusScriptResult.Error,
    };
}
