using System;

namespace QuickMail.Helpers;

/// <summary>
/// Picks out the NavigationCompleted that belongs to one NavigateToString call (issue #959).
///
/// NavigateToString returns no NavigationId, and opening a message while another is still loading
/// stops that earlier navigation, whose own NavigationCompleted (unsuccessful) can arrive after the
/// new call. Taking the first completion that comes along released the wait for the new message
/// early. So: subscribe before calling NavigateToString, take the id of the first document
/// navigation that starts after it (NavigateToString starts as <c>about:</c> or <c>data:</c>; a link
/// the user follows is an http address, and is cancelled anyway), and accept only that id's completion.
/// </summary>
public sealed class NavigationMatcher
{
    /// <summary>The id of this render's navigation, once it has started.</summary>
    public ulong? NavigationId { get; private set; }

    /// <summary>True for the address NavigateToString's own navigation starts with.</summary>
    public static bool IsDocumentNavigation(string? uri) =>
        string.IsNullOrEmpty(uri)
        || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Notes a NavigationStarting. The first document navigation after the call is ours.</summary>
    public void Started(ulong navigationId, string? uri)
    {
        if (NavigationId is null && IsDocumentNavigation(uri))
            NavigationId = navigationId;
    }

    /// <summary>True when a NavigationCompleted with <paramref name="navigationId"/> is this render's.</summary>
    public bool IsOurs(ulong navigationId) => NavigationId == navigationId;
}
