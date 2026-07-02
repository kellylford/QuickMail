using System;

namespace QuickMail.Services;

/// <summary>
/// Marshals work onto the UI thread. ViewModels take this instead of touching
/// System.Windows.Threading.Dispatcher directly (CLAUDE.md MVVM rules), which also
/// lets tests run VM code paths without a live WPF Application.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs the action on the UI thread synchronously (blocks until done).</summary>
    void Invoke(Action action);

    /// <summary>Queues the action onto the UI thread without waiting for it.</summary>
    void Post(Action action);
}

/// <summary>
/// WPF implementation. Marshals only when the current Application is the real QuickMail
/// App: unit tests create a vanilla System.Windows.Application on a StaFact STA thread
/// that never runs a WPF message pump, so queueing onto that dispatcher would park the
/// work forever (30s test timeouts). With no pumped dispatcher available the action runs
/// inline on the calling thread — there is no UI thread to protect.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action)
    {
        var dispatcher = PumpedDispatcher();
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Post(Action action)
    {
        var dispatcher = PumpedDispatcher();
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.InvokeAsync(action);
    }

    private static System.Windows.Threading.Dispatcher? PumpedDispatcher()
    {
        var dispatcher = System.Windows.Application.Current is App app ? app.Dispatcher : null;
        return dispatcher is { Thread.IsAlive: true } ? dispatcher : null;
    }
}
