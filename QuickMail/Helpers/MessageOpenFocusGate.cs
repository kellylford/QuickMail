namespace QuickMail.Helpers;

/// <summary>
/// Which render a window's message-open focus loop is running for (issue #959). Used on the UI thread
/// only.
///
/// Two things must not happen while that loop is waiting for the page:
/// - Its own first step, moving keyboard focus to the WebView2 host, raises the host's
///   GotKeyboardFocus, whose handler focuses the document without waiting for it to finish loading.
///   That handler is for the user's own Tab or F6 into the body; during the loop it must stand aside,
///   or focus lands in a page that is still loading, which is how #959 left the reader on the title.
/// - F6 back into the body starts the same loop for the same render, and both would announce.
/// </summary>
public sealed class MessageOpenFocusGate
{
    private int? _running;

    /// <summary>
    /// Starts the loop for <paramref name="renderVersion"/>. False when one is already running for it:
    /// the caller then only moves focus to the host and leaves the running loop to finish.
    /// </summary>
    public bool TryBegin(int renderVersion)
    {
        if (_running == renderVersion) return false;
        _running = renderVersion;
        return true;
    }

    /// <summary>Ends the loop for <paramref name="renderVersion"/>; a later render's loop is left running.</summary>
    public void End(int renderVersion)
    {
        if (_running == renderVersion) _running = null;
    }

    /// <summary>True while the loop for <paramref name="renderVersion"/> is running.</summary>
    public bool IsRunning(int renderVersion) => _running == renderVersion;
}
