using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using QuickMail.Helpers;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// One message document handed to a WebView2 with NavigateToString, and its own NavigationCompleted
/// (issue #959). Matching by NavigationId means a completion left over from a navigation this one
/// stopped can no longer release the wait early; see <see cref="NavigationMatcher"/>. The clock starts
/// when the document is handed over, and is what the grace for its completion is measured against.
/// </summary>
internal sealed class MessageBodyNavigation : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly NavigationMatcher _matcher = new();
    private readonly TaskCompletionSource<bool> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    private MessageBodyNavigation(CoreWebView2 core)
    {
        _core = core;
        Clock = Stopwatch.StartNew();
    }

    /// <summary>Time since the document was handed to the WebView2.</summary>
    public Stopwatch Clock { get; }

    /// <summary>True once this navigation's own NavigationCompleted has arrived.</summary>
    public bool IsCompleted => _completed.Task.IsCompleted;

    /// <summary>
    /// Stops whatever the WebView2 is loading and shows <paramref name="html"/>, watching for this
    /// navigation's own completion. Dispose it when done watching.
    /// </summary>
    public static MessageBodyNavigation Navigate(CoreWebView2 core, string html, string where)
    {
        var navigation = new MessageBodyNavigation(core);
        // Subscribed before NavigateToString, so the id of the navigation it starts is seen.
        core.NavigationStarting += navigation.OnStarting;
        core.NavigationCompleted += navigation.OnCompleted;
        // Cancel any prior in-flight navigation before starting this one so we don't
        // queue two pending navigations on the WebView2 renderer.
        try { core.Stop(); }
        catch (Exception ex) { LogService.Log($"{where}/Stop", ex); }
        navigation.Clock.Restart();
        core.NavigateToString(html);
        return navigation;
    }

    /// <summary>
    /// Waits for this navigation's completion, at most until <paramref name="grace"/> has passed
    /// since the document was handed over. True when it completed in that time.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan grace)
    {
        var remaining = grace - Clock.Elapsed;
        if (_completed.Task.IsCompleted) return true;
        if (remaining <= TimeSpan.Zero) return false;
        return await Task.WhenAny(_completed.Task, Task.Delay(remaining)) == _completed.Task;
    }

    private void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) =>
        _matcher.Started(e.NavigationId, e.Uri);

    private void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_matcher.IsOurs(e.NavigationId))
        {
            LogService.Debug($"[FOCUS] Ignored the completion of navigation {e.NavigationId}; this render's is {_matcher.NavigationId?.ToString() ?? "not started"}.");
            return;
        }
        LogService.Debug($"[FOCUS] NavigationCompleted {Clock.ElapsedMilliseconds} ms after the document was handed over (success {e.IsSuccess}, status {e.WebErrorStatus}).");
        _completed.TrySetResult(e.IsSuccess);
        Unsubscribe();
    }

    private void Unsubscribe()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _core.NavigationStarting -= OnStarting;
            _core.NavigationCompleted -= OnCompleted;
        }
        catch (Exception ex)
        {
            // The WebView2 may have closed meanwhile.
            LogService.Debug($"MessageBodyNavigation: {ex.Message}");
        }
    }

    public void Dispose() => Unsubscribe();
}
