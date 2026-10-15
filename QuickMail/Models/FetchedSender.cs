namespace QuickMail.Models;

/// <summary>
/// One message's sender address as the server holds it now, for the one-time backfill (#803), with that message's
/// Message-ID beside it so the write can check the stored id still names the cached message (review of #806).
/// <see cref="InternetMessageId"/> is empty when the message has none.
/// </summary>
public readonly record struct FetchedSender(string FromEmail, string InternetMessageId);
