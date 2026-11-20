using System;
using System.Collections.Generic;

namespace QuickMail.Helpers;

/// <summary>
/// Holds back what would be announced while a dialog is about to close, so it can be said once focus
/// has settled where the dialog put it (#955). Said as the dialog closes, an announcement is cut off
/// by the screen reader reading the newly focused control. Only what is offered between
/// <see cref="Hold"/> and <see cref="Stop"/> is held; anything else is said at once.
/// </summary>
public sealed class HeldAnnouncements
{
    private readonly List<string> _held = [];
    private int _depth;

    /// <summary>Whether announcements are being held now.</summary>
    public bool IsHolding => _depth > 0;

    /// <summary>Starts holding. Paired with <see cref="Stop"/>; nested pairs hold until the outermost stops.</summary>
    public void Hold() => _depth++;

    /// <summary>Stops holding. What was held stays until <see cref="TakeAll"/>.</summary>
    public void Stop() => _depth = Math.Max(0, _depth - 1);

    /// <summary>Holds the text if holding, and says whether it was held; if not, the caller says it now.</summary>
    public bool TryHold(string text)
    {
        if (!IsHolding) return false;
        if (text.Length > 0) _held.Add(text);
        return true;
    }

    /// <summary>Everything held, joined into one announcement, or null if nothing was; and forgets it.</summary>
    public string? TakeAll()
    {
        if (_held.Count == 0) return null;
        var text = string.Join(" ", _held);
        _held.Clear();
        return text;
    }
}
