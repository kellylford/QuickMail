namespace QuickMail.Models;

/// <summary>
/// How many of a group's messages are unread, out of how many in all. One value rather than two
/// so the "2 of 5 unread" row field binds a single property and cannot read one half fresh and
/// the other stale (#800).
/// </summary>
public readonly record struct UnreadTally(int Unread, int Total);
