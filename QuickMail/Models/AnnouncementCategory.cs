namespace QuickMail.Models;

public enum AnnouncementCategory
{
    Hint,          // instructional tips the user can silence once familiar
    Status,        // background loading and sync progress
    Result,        // direct outcome of a user action
    MessageAction, // outcome of a common message command (delete, archive) — its own toggle (issue #317)

    /// <summary>
    /// Status-bar text that is never spoken, whatever the user's announcement settings.
    ///
    /// <para>Not a per-case mute of an announcement that otherwise exists — it is how a status
    /// string says it carries nothing the user does not already have. Deleting one message is the
    /// case it was added for (issue #667): the row is gone from the list and the next row has just
    /// been read, so "1 message deleted" tells the user only what they were told a moment ago, and
    /// costs an interruption to hear. The text still appears in the status bar, which is where a
    /// sighted user reads it and where <c>Ctrl+9</c> reads it back on demand.</para>
    ///
    /// <para>Use it only where the outcome is genuinely self-evident from the UI. A count the user
    /// cannot otherwise get ("3 messages deleted"), a state change they cannot see ("Folder is now
    /// empty") and every failure stay audible.</para>
    /// </summary>
    Silent
}
