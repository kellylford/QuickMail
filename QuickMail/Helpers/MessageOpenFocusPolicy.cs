using System;
using System.Threading.Tasks;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>What the page reported for one attempt to focus a newly opened message (issue #959).</summary>
public enum FocusScriptResult
{
    /// <summary>The focus script put focus in this render's document (it returned true).</summary>
    Focused,
    /// <summary>This render's document is showing and ready, but focus did not land (it returned false).</summary>
    NotFocused,
    /// <summary>The WebView2 is still showing an earlier document.</summary>
    Stale,
    /// <summary>This render's document is showing but has not finished loading.</summary>
    Loading,
    /// <summary>The readiness check found this render's document ready to be focused.</summary>
    Ready,
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
    /// <summary>Stop trying.</summary>
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
/// Now the page carries a token naming the render it belongs to, and only this render's own
/// NavigationCompleted (matched by NavigationId) counts. After that completion, or
/// <see cref="LoadGrace"/> without it, the loop asks the page whether it is ready: "stale" while the
/// WebView2 is still showing anything else, "loading" while this render's page has not finished
/// loading. Nothing is touched until it is ready; then the host's focus step and the focus script run,
/// as they always did. Near the end of <see cref="Budget"/> a page still loading is focused anyway,
/// since by then waiting longer only leaves the reader nowhere.
///
/// The decision is a pure function, and the loop takes its clock, pause and page as delegates, so both
/// can be tested without a WebView2.
/// </summary>
public static class MessageOpenFocusPolicy
{
    /// <summary>
    /// How long to wait for this render's NavigationCompleted before looking at the page anyway: four
    /// seconds, what the wait was before #959. Pictures no longer hold the load, so in the ordinary case
    /// the completion comes long before; this is only the ceiling for a page that is slow for some other
    /// reason, and lowering it would focus such a page sooner while it is still loading.
    /// </summary>
    public static readonly TimeSpan LoadGrace = TimeSpan.FromSeconds(4);

    /// <summary>How long the loop waits for this render's page to be on show and loaded, from when the loop starts.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    /// <summary>After this long in the loop, a page of this render's that is still loading is focused anyway.</summary>
    public static readonly TimeSpan FocusAnywayAfter = TimeSpan.FromSeconds(2);

    /// <summary>The pause between attempts.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>A ceiling on attempts, whatever the clock says.</summary>
    public const int MaxAttempts = 60;

    /// <summary>
    /// How many times this render's ready page may refuse focus (or the script fail) before giving up:
    /// five, as before #959, so the announcement after it is not held back. It is also the fewest
    /// attempts the loop ever makes, however long each takes: on a busy UI thread, which is when #959
    /// happens, one slow attempt must not use up the whole budget.
    /// </summary>
    public const int MaxUnfocusedAttempts = 5;

    /// <summary>True while focus should wait for this render's page to finish loading.</summary>
    public static bool WaitForLoad(TimeSpan elapsed) => elapsed < FocusAnywayAfter;

    /// <summary>The next step after an attempt that produced <paramref name="result"/>.</summary>
    /// <param name="result">What the page reported.</param>
    /// <param name="attempt">Zero-based number of this attempt, of any kind.</param>
    /// <param name="unfocusedAttempts">How many attempts so far, this one included, found this render's ready page refusing focus or the script failing.</param>
    /// <param name="elapsed">Time since the loop started.</param>
    public static FocusDecision Next(FocusScriptResult result, int attempt, int unfocusedAttempts, TimeSpan elapsed)
    {
        if (result == FocusScriptResult.Focused)
            return FocusDecision.Focus;
        if (attempt + 1 >= MaxAttempts)
            return FocusDecision.GiveUp;
        if (result is FocusScriptResult.NotFocused or FocusScriptResult.Error)
            return unfocusedAttempts >= MaxUnfocusedAttempts ? FocusDecision.GiveUp : FocusDecision.Retry;
        // Stale or still loading (or "ready" from the focus script, which never says it): waited for
        // until the budget is spent, but never fewer than the minimum attempts, so one attempt slowed
        // by a busy UI thread cannot end it.
        return elapsed >= Budget && attempt + 1 >= MaxUnfocusedAttempts
            ? FocusDecision.GiveUp
            : FocusDecision.Retry;
    }

    /// <summary>
    /// Whether, once the loop is over, the window should run its host focus step and announce
    /// "Message body. Subject". Not when focus never reached this render's page (the last answer was
    /// "stale": the page on show is another message's), and not when the user has moved focus away
    /// from the message body during the wait — Tab to a header, Ctrl+1 to the folders — since pulling
    /// them back would undo what they chose.
    /// </summary>
    public static bool ShouldFinish(FocusScriptResult result, bool focusStillOnBody) =>
        result != FocusScriptResult.Stale && focusStillOnBody;

    /// <summary>Reads the JSON <c>ExecuteScriptAsync</c> returned for the readiness or focus script.</summary>
    public static FocusScriptResult Parse(string? json) => json?.Trim() switch
    {
        "true" => FocusScriptResult.Focused,
        "false" => FocusScriptResult.NotFocused,
        "\"stale\"" => FocusScriptResult.Stale,
        "\"loading\"" => FocusScriptResult.Loading,
        "\"ready\"" => FocusScriptResult.Ready,
        _ => FocusScriptResult.Error,
    };

    /// <summary>
    /// Focuses this render's document once it is on show and loaded, or the time allowed runs out.
    /// Each attempt first asks the page whether it is ready, which touches nothing; only a ready page
    /// gets the same pair as before #959, the host's own focus step and then the focus script. While
    /// the previous message's page is still on show, nothing moves focus into it. Logs every attempt's
    /// result under <c>/debug</c>, and always logs giving up.
    /// </summary>
    /// <param name="isCurrent">False once a newer render (or closing the message) has taken over: the loop stops quietly.</param>
    /// <param name="focusHost">The window's host focus step (<c>TabInto</c>), run on the UI thread.</param>
    /// <param name="readiness">Runs the readiness script; its argument is whether to wait for the page to finish loading.</param>
    /// <param name="focus">Runs the focus script for this render; the same argument.</param>
    /// <param name="elapsed">Time since the loop started.</param>
    /// <param name="delay">The pause between attempts.</param>
    public static async Task<FocusScriptResult> RunAsync(
        Func<bool> isCurrent, Func<Task> focusHost,
        Func<bool, Task<string?>> readiness, Func<bool, Task<string?>> focus,
        Func<TimeSpan> elapsed, Func<TimeSpan, Task> delay, string where)
    {
        var result = FocusScriptResult.Error;
        var unfocused = 0;
        for (var attempt = 0; ; attempt++)
        {
            if (!isCurrent()) return result;
            var waitForLoad = WaitForLoad(elapsed());
            try
            {
                result = Parse(await readiness(waitForLoad));
                if (result == FocusScriptResult.Ready)
                {
                    await focusHost();
                    if (!isCurrent()) return result;
                    result = Parse(await focus(waitForLoad));
                }
            }
            catch (Exception ex)
            {
                result = FocusScriptResult.Error;
                LogService.Debug($"[FOCUS] {where}: attempt {attempt} threw: {ex.Message}");
            }
            if (!isCurrent()) return result;

            if (result is FocusScriptResult.NotFocused or FocusScriptResult.Error)
                unfocused++;
            var now = elapsed();
            LogService.Debug($"[FOCUS] {where}: attempt {attempt}, {now.TotalMilliseconds:0} ms: {result}" +
                             (waitForLoad ? string.Empty : " (not waiting for the load)"));
            switch (Next(result, attempt, unfocused, now))
            {
                case FocusDecision.Focus:
                    return result;
                case FocusDecision.GiveUp:
                    LogService.Log($"{where}: focus did not land in the message after {attempt + 1} attempts " +
                                   $"and {now.TotalMilliseconds:0} ms (last result {result}).");
                    return result;
            }

            await delay(RetryDelay);
        }
    }
}
