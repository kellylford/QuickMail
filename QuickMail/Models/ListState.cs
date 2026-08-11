namespace QuickMail.Models;

/// <summary>
/// Everything that decides how the message list is presented: grouping, filter, named-flag
/// sub-filter, sort order, and date range.
///
/// This is one record rather than five loose ViewModel properties so that restoring a
/// presentation is a single assignment. Before it existed, "apply a view" wrote six properties
/// and "clear the view" reset two of them, and each of the three navigation paths reset a
/// different subset — the defect behind issue #520. A whole-record assignment makes a partial
/// restore inexpressible, and a sixth presentation setting means one more field here rather than
/// four reset sites to remember.
///
/// A value type deliberately: a state handed out by the per-folder store and then mutated by the
/// ViewModel would corrupt the store silently.
/// </summary>
public readonly record struct ListState(
    ViewMode      Mode,
    MessageFilter Filter,
    string?       FlagFilterId,
    MessageSort   Sort,
    int?          DayLimit)
{
    /// <summary>The presentation a folder gets when nothing else has an opinion.</summary>
    public static ListState Default => new(
        ViewMode.Messages, MessageFilter.All, null, MessageSort.DateDescending, null);
}
