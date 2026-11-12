using System;

namespace QuickMail.Helpers;

/// <summary>
/// Holds an announcement until the user has paused (#901). Background news, such as auto-save
/// keeping a draft on this computer, said while the user is typing or moving the caret cuts into
/// what the screen reader is reading. The window offers the text with <see cref="Queue"/>, reports
/// each key and caret move with <see cref="NoteActivity"/>, and asks <see cref="TakeIfQuiet"/> on
/// a timer; the text comes out once nothing has happened for the quiet period.
///
/// <para>A pure function of the times it is given, so it can be tested without waiting.</para>
/// </summary>
public sealed class QuietTimeAnnouncement
{
    private string? _pending;
    private DateTime _lastActivity;

    public QuietTimeAnnouncement(TimeSpan quietPeriod) => QuietPeriod = quietPeriod;

    /// <summary>How long nothing must happen before the held text is said.</summary>
    public TimeSpan QuietPeriod { get; }

    /// <summary>Whether text is waiting to be said.</summary>
    public bool IsPending => _pending != null;

    /// <summary>
    /// Holds <paramref name="text"/> to be said after the next pause, replacing anything still
    /// held. The pause is counted from now at the earliest.
    /// </summary>
    public void Queue(string text, DateTime now)
    {
        if (string.IsNullOrEmpty(text)) return;
        _pending = text;
        if (now > _lastActivity) _lastActivity = now;
    }

    /// <summary>A key or caret move: the pause starts again.</summary>
    public void NoteActivity(DateTime now)
    {
        if (now > _lastActivity) _lastActivity = now;
    }

    /// <summary>The held text if the quiet period has passed since the last activity, and forgets it; otherwise null.</summary>
    public string? TakeIfQuiet(DateTime now)
    {
        if (_pending == null || now - _lastActivity < QuietPeriod) return null;
        var text = _pending;
        _pending = null;
        return text;
    }

    /// <summary>Forgets anything held, as when the window closes.</summary>
    public void Clear() => _pending = null;
}
