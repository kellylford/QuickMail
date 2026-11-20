using System;
using System.IO;
using System.Text.Json;

namespace QuickMail.Services;

/// <summary>
/// The client-side rules file exists but couldn't be read or parsed (#700). Kept distinct from "no rules":
/// every writer loads the whole list, changes it and saves it back, so treating an unreadable file as empty
/// meant the next save replaced every rule in it with the one being saved.
/// </summary>
/// <remarks>An <see cref="IOException"/>, so a caller that already handles a failed file operation handles
/// this one too. The message is the reason alone — callers put it after their own "Couldn't …:" lead.</remarks>
public sealed class RulesFileUnreadableException : IOException
{
    // Windows' sharing and lock violations: another program has the file open.
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    public RulesFileUnreadableException() : base("rules.json can't be read.") { }
    public RulesFileUnreadableException(string message) : base(message) { }
    public RulesFileUnreadableException(string message, Exception innerException) : base(message, innerException) { }
    public RulesFileUnreadableException(string message, int hresult) : base(message, hresult) { }

    /// <summary>Wraps the failure from reading or parsing <paramref name="filePath"/>, saying which it was.</summary>
    public static RulesFileUnreadableException For(string filePath, Exception inner) => new(Describe(filePath, inner), inner);

    private static string Describe(string filePath, Exception inner) => inner switch
    {
        // Damaged: say where it is, because the user is the one who has to repair or remove it.
        JsonException => $"rules.json in {Path.GetDirectoryName(filePath)} is damaged and can't be read.",
        // Held open by another program: usually over in a moment, and the file itself is fine. The system's own
        // message repeats the full path, and there is nothing to find or fix.
        IOException io when (io.HResult & 0xFFFF) is SharingViolation or LockViolation
            => "rules.json is open in another program, so it can't be read right now.",
        _ => $"rules.json can't be opened. {inner.Message}",
    };
}
