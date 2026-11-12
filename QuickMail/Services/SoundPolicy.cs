using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Decides whether a requested sound plays (issue #912). Pure apart from what it remembers, with the
/// clock passed in, so the flood rules can be tested without audio. A backlog of mail or a burst of
/// rules must not turn into a burst of noise over someone's speech output.
/// </summary>
public sealed class SoundPolicy(Func<DateTime> utcNow)
{
    /// <summary>Most sounds waiting behind the one playing. Further requests are dropped, not delayed.</summary>
    public const int MaxWaiting = 3;

    /// <summary>The same sound again within this long adds nothing.</summary>
    public static readonly TimeSpan SameSoundGap = TimeSpan.FromSeconds(1);

    /// <summary>A new-mail sound this soon after a rule sound is about the same mail.</summary>
    public static readonly TimeSpan RuleHoldsArrivalFor = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastPlayed = new(StringComparer.Ordinal);
    private DateTime _lastRuleSound = DateTime.MinValue;

    /// <summary>Whether this sound should play. Remembers it if so, so call once per request.</summary>
    /// <param name="soundKey">Identifies the sound, so a repeat of the same one can be dropped.</param>
    /// <param name="origin">Why it is playing.</param>
    /// <param name="muted">All sounds are muted.</param>
    /// <param name="quiet">Windows reports a presentation, full-screen use or quiet time.</param>
    /// <param name="waiting">How many sounds are already waiting.</param>
    public bool ShouldPlay(string soundKey, SoundOrigin origin, bool muted, bool quiet, int waiting)
    {
        if (muted) return false;
        if (quiet && origin is not (SoundOrigin.Direct or SoundOrigin.Navigation)) return false;

        // A key press's answer (#923): every press gets one, so no same-sound gap, but never late.
        if (origin == SoundOrigin.Navigation) return waiting == 0;

        lock (_gate)
        {
            var now = utcNow();

            // Recorded before the checks below: a rule whose sound is dropped as a repeat still filed
            // this mail, so the arrival sound behind it is still about the same mail.
            if (origin == SoundOrigin.Rule) _lastRuleSound = now;

            if (origin == SoundOrigin.Arrival && IsWithin(now, _lastRuleSound, RuleHoldsArrivalFor)) return false;
            if (waiting >= MaxWaiting) return false;
            if (_lastPlayed.TryGetValue(soundKey, out var last) && IsWithin(now, last, SameSoundGap)) return false;

            _lastPlayed[soundKey] = now;
            return true;
        }
    }

    // A clock set back gives a negative age. That counts as long ago, or the sound stays silent until the clock catches up.
    private static bool IsWithin(DateTime now, DateTime then, TimeSpan window)
    {
        var age = now - then;
        return age >= TimeSpan.Zero && age < window;
    }
}
