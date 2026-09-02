using System;
using System.Threading.Tasks;

namespace QuickMail.Helpers;

/// <summary>
/// Runs a command that may rebuild a grouped view with a one-shot focus-landing listener armed
/// across it, and takes the listener back off once the rebuild has either landed or been ruled out
/// (#637).
/// </summary>
/// <remarks>
/// Lives here rather than in the window so the rule can be tested. It was written inline first, and
/// a deletion probe showed the whole mechanism could be removed with the suite still green: the
/// view model's half was pinned, the window's half was not, and nothing joined them.
/// </remarks>
internal static class RebuildLanding
{
    /// <summary>
    /// Arms, runs, waits for the rebuild to settle, and disarms — in that order, and disarms even
    /// if the command throws.
    /// </summary>
    /// <param name="mark">Identifies the rebuild in flight before the listener is armed.</param>
    /// <param name="settledSince">
    /// Given that mark, completes once a rebuild scheduled SINCE it has landed — and at once if
    /// none was.
    /// </param>
    /// <param name="arm">Registers the landing listener and returns the action that removes it.</param>
    /// <param name="command">The command that may rebuild the view.</param>
    /// <remarks>
    /// Arming BEFORE the command is what keeps a refused command — or one the user answers No to —
    /// from leaving a listener that fires on an unrelated rebuild minutes later. Waiting on
    /// <paramref name="settled"/> is what keeps the opposite from happening: the command completing
    /// is not the same event as the rebuild landing, since the rebuild is built on the thread pool
    /// and posted back, and an all-local draft delete has no network leg and completes without ever
    /// yielding. Disarming there tore the listener off before the rebuild could fire, and focus was
    /// left on nothing.
    /// </remarks>
    /// <remarks>
    /// The mark is taken HERE, before arming, rather than by the caller: taking it is half the
    /// rule, and a caller that forgot to would wait on whatever rebuild anyone else had in flight
    /// with nothing to catch it. Both halves live together so both can be pinned.
    /// </remarks>
    public static async Task RunAsync(
        Func<object> mark, Func<object, Task> settledSince, Func<Action> arm, Func<Task> command)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(settledSince);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(command);

        var before = mark();
        var disarm = arm();
        try
        {
            await command();
            await settledSince(before);
        }
        finally
        {
            // A no-op once the listener has fired; the point is every path where it never does.
            disarm();
        }
    }
}
