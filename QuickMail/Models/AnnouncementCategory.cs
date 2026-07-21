namespace QuickMail.Models;

public enum AnnouncementCategory
{
    Hint,          // instructional tips the user can silence once familiar
    Status,        // background loading and sync progress
    Result,        // direct outcome of a user action
    MessageAction  // outcome of a common message command (delete, archive) — its own toggle (issue #317)
}
