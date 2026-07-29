using System;
using System.Globalization;

namespace QuickMail.Helpers;

/// <summary>
/// How far one stepping gesture moves a date or time field. Named for the keystroke rather than
/// for an amount, because the amount differs between the two field kinds — Up is a day in a date
/// field and a quarter hour in a time field.
/// </summary>
public enum FieldStep
{
    /// <summary>Ctrl+Up / Ctrl+Down. One minute in a time field; a day in a date field, which has
    /// no finer unit to offer.</summary>
    Small,

    /// <summary>Up / Down. One day, or a quarter hour snapped to the :00 :15 :30 :45 grid.</summary>
    Normal,

    /// <summary>Shift+Up / Shift+Down. One week, or one hour.</summary>
    Medium,

    /// <summary>PageUp / PageDown. One month, or one hour — a time field has nothing useful
    /// between an hour and a day, so Medium and Large deliberately coincide there.</summary>
    Large,

    /// <summary>Ctrl+PageUp / Ctrl+PageDown. One year, or one day.</summary>
    Huge,
}

/// <summary>
/// Stepping and free-text parsing for the appointment editor's date and time fields. Pure
/// arithmetic and string work so the whole gesture and typing surface is unit-testable without a
/// window, a dispatcher, or a screen reader.
///
/// Every method takes the field's <c>current</c> value and returns a full <see cref="DateTime"/>
/// rather than a date part or a <see cref="TimeSpan"/>. That is deliberate: a time field that
/// carries into the date is the only way to make "step 23:50 up by fifteen minutes" mean 00:05
/// tomorrow instead of wrapping back to the same morning.
/// </summary>
public static class DateTimeFieldParser
{
    /// <summary>Minutes the Normal gesture moves a time field, and the grid it snaps to.</summary>
    private const int TimeGridMinutes = 15;

    // ---------------------------------------------------------------- stepping

    /// <summary>
    /// Applies one gesture to a date field. Time of day is preserved, so stepping the date of a
    /// 9:15 appointment leaves it at 9:15.
    ///
    /// Month and year steps use <see cref="DateTime.AddMonths"/> semantics, which clamp rather
    /// than overflow: January 31 stepped up a month is the last day of February, not March 3.
    /// </summary>
    /// <param name="direction">+1 for up, -1 for down.</param>
    public static DateTime StepDate(DateTime current, FieldStep step, int direction) => step switch
    {
        FieldStep.Small => current.AddDays(direction),
        FieldStep.Normal => current.AddDays(direction),
        FieldStep.Medium => current.AddDays(7 * direction),
        FieldStep.Large => current.AddMonths(direction),
        FieldStep.Huge => current.AddYears(direction),
        _ => current,
    };

    /// <summary>
    /// Applies one gesture to a time field, carrying into the date when it crosses midnight.
    ///
    /// The Normal step snaps to the quarter-hour grid instead of adding fifteen minutes blindly:
    /// from 9:07 Up gives 9:15 and Down gives 9:00, so repeated stepping converges on round times
    /// rather than preserving whatever odd minute an imported appointment happened to start on.
    /// Seconds are discarded for the same reason. The finer Small step does not snap — it is the
    /// escape hatch for setting 9:05 exactly.
    /// </summary>
    /// <param name="direction">+1 for up, -1 for down.</param>
    public static DateTime StepTime(DateTime current, FieldStep step, int direction)
    {
        switch (step)
        {
            case FieldStep.Small:
                return Truncate(current).AddMinutes(direction);

            case FieldStep.Normal:
                // Whole minutes elapsed today, so a 9:07:30 value snaps from 9:07 and not from 9:08.
                var minutes = (int)(Truncate(current) - current.Date).TotalMinutes;
                var snapped = direction > 0
                    ? (minutes / TimeGridMinutes + 1) * TimeGridMinutes
                    : ((minutes + TimeGridMinutes - 1) / TimeGridMinutes - 1) * TimeGridMinutes;
                return current.Date.AddMinutes(snapped);

            case FieldStep.Medium:
            case FieldStep.Large:
                return Truncate(current).AddHours(direction);

            case FieldStep.Huge:
                return Truncate(current).AddDays(direction);

            default:
                return current;
        }
    }

    private static DateTime Truncate(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMinute));

    // ------------------------------------------------------------ date parsing

    /// <summary>
    /// Parses free-typed text for a date field.
    ///
    /// Accepts absolute dates in the given culture ("8/3", "8/3/2026", "Aug 3", "2026-08-03"),
    /// the words "today" / "tomorrow" / "yesterday", weekday names ("fri", "next tuesday"), and
    /// signed offsets ("+7", "-3", "+2w", "+1m", "+1y") where a bare number counts days.
    ///
    /// <paramref name="current"/>'s time of day is carried onto the result, so typing "8/3" into
    /// the date box of a 9:15 appointment yields August 3 at 9:15 — the date field must never
    /// silently reset the time. Offsets are measured from <paramref name="current"/>, so "+7"
    /// after already moving the field a month forward means a week beyond that, not a week from
    /// today. Weekday and word forms resolve against <paramref name="today"/>.
    ///
    /// <paramref name="today"/> is a parameter rather than a read of <see cref="DateTime.Now"/> so
    /// that "tomorrow" is deterministic in tests and cannot change under a suite that straddles
    /// local midnight.
    /// </summary>
    public static bool TryParseDate(string? text, DateTime current, DateTime today,
                                    out DateTime result, CultureInfo? culture = null)
    {
        result = current;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        culture ??= CultureInfo.CurrentCulture;
        var time = current.TimeOfDay;

        // Time-shaped text in the date box is a mis-entry, not a date. Rejecting it here matters
        // because DateTime.TryParse("9:30") succeeds and would silently move the appointment to
        // today — the mirror image of the bug TryParseTime exists to prevent.
        if (LooksLikeTimeOnly(trimmed, culture)) return false;

        if (TryParseDateWord(trimmed, today, out var word))
        {
            result = word.Date + time;
            return true;
        }

        if (TryParseDateOffset(trimmed, current, out var offset))
        {
            result = offset.Date + time;
            return true;
        }

        // A bare day number means that day of the month already shown: "3" on a July date is
        // July 3. Handled before TryParse, which reads a lone number inconsistently across
        // cultures and would otherwise make "3" mean March on some machines.
        if (trimmed.Length <= 2 && TryDigits(trimmed, out var day))
        {
            if (day < 1 || day > DateTime.DaysInMonth(current.Year, current.Month)) return false;
            result = new DateTime(current.Year, current.Month, day) + time;
            return true;
        }

        if (DateTime.TryParse(trimmed, culture, DateTimeStyles.None, out var parsed))
        {
            result = parsed.Date + time;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the text is unambiguously a time rather than a date, so the date field can refuse
    /// it. Deliberately narrower than "TryParseTimeOfDay succeeds": a bare "3" parses as 03:00 but
    /// in a date field it means the third, and a field that rejected it would be maddening.
    /// </summary>
    private static bool LooksLikeTimeOnly(string text, CultureInfo culture)
    {
        var lower = text.Trim().ToLowerInvariant();
        if (lower is "noon" or "midday" or "midnight") return true;

        var separator = culture.DateTimeFormat.TimeSeparator;
        if (lower.Contains(':') || (separator.Length > 0 && separator != ":" && lower.Contains(separator)))
            return TryParseTimeOfDay(lower, culture, out _);

        // "9am", "9 pm", "930p" — digits and nothing but a meridiem after them. The check is
        // anchored on the digits because the bare "p" shorthand would otherwise flag "sep".
        foreach (var (suffix, _) in Meridiems(culture))
        {
            if (suffix.Length == 0 || !lower.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var head = lower[..^suffix.Length].TrimEnd();
            if (head.Length > 0 && TryDigits(head, out _)) return true;
        }
        return false;
    }

    private static bool TryParseDateWord(string text, DateTime today, out DateTime result)
    {
        result = today.Date;
        var lower = text.ToLowerInvariant();

        switch (lower)
        {
            case "today": return true;
            case "tomorrow" or "tom": result = today.Date.AddDays(1); return true;
            case "yesterday" or "yest": result = today.Date.AddDays(-1); return true;
        }

        // "next tuesday" skips today even when today is a Tuesday; a bare "tuesday" does not. That
        // split is the common reading: "tuesday" said on a Tuesday means today, "next tuesday"
        // never does.
        var skipToday = false;
        if (lower.StartsWith("next ", StringComparison.Ordinal))
        {
            skipToday = true;
            lower = lower[5..].Trim();
        }

        if (!TryParseWeekday(lower, out var target)) return false;

        var days = ((int)target - (int)today.DayOfWeek + 7) % 7;
        if (days == 0 && skipToday) days = 7;
        result = today.Date.AddDays(days);
        return true;
    }

    private static bool TryParseWeekday(string lower, out DayOfWeek day)
    {
        // Matched against the invariant English names only. The abbreviations users actually type
        // ("tue", "thurs") are not the culture's abbreviated names in every locale, and a field
        // that accepts "tue" on one machine and not another is worse than one that never does.
        foreach (DayOfWeek candidate in Enum.GetValues<DayOfWeek>())
        {
            var name = candidate.ToString().ToLowerInvariant();
            if (lower.Length >= 3 && name.StartsWith(lower, StringComparison.Ordinal))
            {
                day = candidate;
                return true;
            }
        }
        day = default;
        return false;
    }

    private static bool TryParseDateOffset(string text, DateTime current, out DateTime result)
    {
        result = current;
        if (!TrySplitOffset(text, out var amount, out var unit)) return false;

        result = unit switch
        {
            'd' or '\0' => current.AddDays(amount),
            'w' => current.AddDays(7 * amount),
            'm' => current.AddMonths(amount),
            'y' => current.AddYears(amount),
            _ => current,
        };
        return unit is 'd' or 'w' or 'm' or 'y' or '\0';
    }

    // ------------------------------------------------------------ time parsing

    /// <summary>
    /// Parses free-typed text for a time field, returning the instant on
    /// <paramref name="current"/>'s date that carries the new time of day.
    ///
    /// Accepts "9", "930", "0930", "9:30", "9:30 am", "9a", "9p", "14:30", "noon", "midnight",
    /// and signed offsets ("+30", "-15", "+2h") which may roll the date.
    ///
    /// Deliberately does NOT fall back to <see cref="DateTime.TryParse"/>. That is the whole point
    /// of this method: TryParse("8/3") succeeds, and its TimeOfDay is zero, so the old editor
    /// turned a date typed into the time box into midnight with no error and no announcement. An
    /// explicit format list refuses date-shaped text instead, and the field can revert.
    ///
    /// A bare one- or two-digit number is read as a literal hour on the 24-hour clock: "9" is
    /// 09:00 and "21" is 21:00. Nine in the evening is "9p", "9 pm", or "21" — never a guess.
    /// </summary>
    public static bool TryParseTime(string? text, DateTime current,
                                    out DateTime result, CultureInfo? culture = null)
    {
        result = current;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        culture ??= CultureInfo.CurrentCulture;

        if (TrySplitOffset(trimmed, out var amount, out var unit))
        {
            result = unit switch
            {
                'm' or '\0' => Truncate(current).AddMinutes(amount),
                'h' => Truncate(current).AddHours(amount),
                _ => current,
            };
            return unit is 'm' or 'h' or '\0';
        }

        if (!TryParseTimeOfDay(trimmed, culture, out var timeOfDay)) return false;

        result = current.Date + timeOfDay;
        return true;
    }

    /// <summary>
    /// The time-of-day half of <see cref="TryParseTime"/>, shared with
    /// <see cref="TryParseDate"/> so the date field can recognise and refuse time-shaped input.
    /// </summary>
    private static bool TryParseTimeOfDay(string text, CultureInfo culture, out TimeSpan result)
    {
        result = default;
        var lower = text.Trim().ToLowerInvariant();

        switch (lower)
        {
            case "noon" or "midday": result = TimeSpan.FromHours(12); return true;
            case "midnight": result = TimeSpan.Zero; return true;
        }

        // Peel off the meridiem first — as the culture's designators, as plain AM/PM, and as the
        // bare "a"/"p" shorthand that makes "9p" two keystrokes instead of five.
        int? meridiem = null;   // 0 = morning, 1 = afternoon
        foreach (var (suffix, value) in Meridiems(culture))
        {
            if (suffix.Length == 0 || !lower.EndsWith(suffix, StringComparison.Ordinal)) continue;
            meridiem = value;
            lower = lower[..^suffix.Length].TrimEnd();
            break;
        }

        // Always accept the colon, plus the culture's own separator when it is something else, so
        // a machine set to a locale that writes 9.30 still takes the colon everyone's keyboard has.
        var separator = culture.DateTimeFormat.TimeSeparator;
        var parts = separator.Length > 0 && separator != ":"
            ? lower.Split(ColonAnd(separator[0]), StringSplitOptions.None)
            : lower.Split(':');

        int hour, minute;
        if (parts.Length == 2)
        {
            if (!TryDigits(parts[0], out hour) || !TryDigits(parts[1], out minute)) return false;
            if (parts[1].Length != 2) return false;
        }
        else if (parts.Length == 1)
        {
            var digits = parts[0];
            if (!TryDigits(digits, out var value)) return false;
            switch (digits.Length)
            {
                case 1 or 2: hour = value; minute = 0; break;          // "9", "14"
                case 3: hour = value / 100; minute = value % 100; break; // "930"
                case 4: hour = value / 100; minute = value % 100; break; // "0930", "1430"
                default: return false;
            }
        }
        else return false;

        if (minute is < 0 or > 59) return false;

        if (meridiem is { } half)
        {
            if (hour is < 1 or > 12) return false;
            hour = half == 0 ? hour % 12 : hour % 12 + 12;
        }
        else if (hour is < 0 or > 23) return false;

        result = new TimeSpan(hour, minute, 0);
        return true;
    }

    private static char[] ColonAnd(char separator) => [':', separator];

    private static (string Suffix, int Value)[] Meridiems(CultureInfo culture) =>
    [
        (culture.DateTimeFormat.AMDesignator.ToLowerInvariant(), 0),
        (culture.DateTimeFormat.PMDesignator.ToLowerInvariant(), 1),
        ("am", 0), ("pm", 1), ("a", 0), ("p", 1),
    ];

    private static bool TryDigits(string text, out int value)
    {
        value = 0;
        if (text.Length == 0) return false;
        foreach (var c in text)
            if (!char.IsAsciiDigit(c)) return false;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// Splits "+7", "-3", "+2w", "+90m" into a signed amount and a lower-case unit letter
    /// ('\0' when the unit was omitted). The sign is mandatory: an unsigned "7" is far likelier to
    /// be a day of the month or an hour than an offset, and guessing wrong silently moves an
    /// appointment.
    /// </summary>
    private static bool TrySplitOffset(string text, out int amount, out char unit)
    {
        amount = 0;
        unit = '\0';
        if (text.Length < 2 || (text[0] != '+' && text[0] != '-')) return false;

        var body = text[1..].Trim();
        if (body.Length == 0) return false;

        if (!char.IsAsciiDigit(body[^1]))
        {
            unit = char.ToLowerInvariant(body[^1]);
            body = body[..^1].TrimEnd();
        }

        if (!TryDigits(body, out var magnitude)) return false;

        amount = text[0] == '-' ? -magnitude : magnitude;
        return true;
    }
}
