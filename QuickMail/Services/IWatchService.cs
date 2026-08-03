using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Stores the conversations the user has chosen to watch, and answers whether a given message
/// belongs to one. Membership of the Watched Conversations virtual folder is computed from this
/// service at fetch time; no watch state is stored per message.
/// </summary>
public interface IWatchService
{
    /// <summary>Every stored watch, newest first.</summary>
    IReadOnlyList<WatchedConversation> GetAll();

    /// <summary>
    /// True when a message with this subject belongs to a watched conversation. Called once per
    /// message per fetch, so it must stay O(1).
    /// </summary>
    bool IsWatched(string subject);

    /// <summary>
    /// Starts watching the conversation this subject belongs to. Returns false — storing nothing —
    /// when the subject normalizes to empty, or when the conversation is already watched.
    /// </summary>
    bool Watch(string subject);

    /// <summary>
    /// Stops watching the conversation this subject belongs to, whichever of its messages the
    /// subject came from. Returns false when it was not watched.
    /// </summary>
    bool Unwatch(string subject);
}
