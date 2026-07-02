using System;
using System.Threading.Tasks;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Deliberate fire-and-forget for tasks whose failure should be logged, not surfaced.
/// A bare <c>_ = SomethingAsync()</c> only reports faults through the
/// UnobservedTaskException handler at finalization time (if ever); routing through
/// <see cref="LogFaults"/> logs them promptly with a context string.
/// </summary>
public static class TaskFaultLogging
{
    public static void LogFaults(this Task task, string context)
    {
        _ = ObserveAsync(task, context);
    }

    private static async Task ObserveAsync(Task task, string context)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome for background work.
        }
        catch (Exception ex)
        {
            LogService.Log($"Background task failed: {context}", ex);
        }
    }
}
