using System.Runtime.CompilerServices;
using MimeKit;

namespace QuickMail.Services;

/// <summary>
/// Process-wide MimeKit parser settings, applied once when this assembly loads.
/// </summary>
internal static class MimeParsing
{
    /// <summary>
    /// The longest single header (folding included) the parser accepts. MimeKit 5 added
    /// <see cref="ParserOptions.MaxHeaderLength"/> with a 16 KB default, and a longer header makes
    /// the whole message fail to parse with a <see cref="System.FormatException"/>. A To or Cc list of
    /// a few hundred addresses, or a long References chain, passes that easily, and 4.x had no limit:
    /// with the default, such a POP3 message was never collected, a .eml would not open, and copying
    /// it to another account failed. 1 MB keeps a ceiling against a hostile message while accepting
    /// any real one.
    /// </summary>
    internal const int MaxHeaderLength = 1024 * 1024;

    /// <summary>
    /// Runs before any code in this assembly, so every parse (MailKit's POP3 client and IMAP engine
    /// use <see cref="ParserOptions.Default"/> too) sees the raised limit, the test host included.
    /// </summary>
#pragma warning disable CA2255 // A module initializer is the point: it must run before any parse, wherever the assembly is loaded from.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Configure()
    {
        if (ParserOptions.Default.MaxHeaderLength < MaxHeaderLength)
            ParserOptions.Default.MaxHeaderLength = MaxHeaderLength;
    }
}
