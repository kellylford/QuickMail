using System;

namespace QuickMail.Models;

/// <summary>
/// One calendar belonging to an account: a node under it in the folder tree, and — when it can be
/// written to — an entry in the appointment editor's Calendar picker.
///
/// <para>
/// These come from the provider's own calendar list, recorded by the sync, rather than from the
/// calendars that happen to have events in them. That distinction is the point: a calendar you have
/// not put anything in yet is still one you can file the first appointment into, and deriving the
/// list from existing rows made such a calendar invisible.
/// </para>
///
/// <para>
/// <see cref="CanWrite"/> is what keeps a calendar you merely subscribe to — a holidays feed, a
/// colleague's shared calendar you can only read — out of the save-target picker, where choosing it
/// could do nothing but fail. It still appears in the tree, because reading it is exactly what it is
/// for.
/// </para>
/// </summary>
public sealed record CalendarSourceInfo(Guid AccountId, string CalendarId, string CalendarName, bool CanWrite)
{
    /// <summary>How this calendar reads in a list: "Apple: Family".</summary>
    public string LabelUnder(string accountLabel) => $"{accountLabel}: {CalendarName}";
}
