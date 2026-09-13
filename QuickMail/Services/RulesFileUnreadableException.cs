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
    public RulesFileUnreadableException() : base("rules.json can't be read.") { }
    public RulesFileUnreadableException(string message) : base(message) { }
    public RulesFileUnreadableException(string message, Exception innerException) : base(message, innerException) { }
    public RulesFileUnreadableException(string message, int hresult) : base(message, hresult) { }

    /// <summary>Wraps the failure from reading or parsing the file, naming which of the two it was.</summary>
    public RulesFileUnreadableException(Exception innerException) : base(Describe(innerException), innerException) { }

    private static string Describe(Exception inner) => inner is JsonException
        ? "rules.json is damaged and can't be read."
        : $"rules.json can't be opened. {inner.Message}";
}
