using System;

namespace QuickMail.Services;

/// <summary>
/// The original message (the exact bytes the server holds) does not exist anywhere QuickMail can
/// reach, as opposed to being temporarily out of reach — a connection failure is reported as the
/// connection failure it is. Thrown by <see cref="IMailService.GetOriginalMessageAsync"/> (#728).
/// <para>The message text is written for the user: it is what Save explains when it refuses.</para>
/// </summary>
public sealed class MessageOriginalUnavailableException(string message) : Exception(message);
