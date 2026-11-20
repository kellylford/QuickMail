using MimeKit;
using QuickMail.Helpers;

namespace QuickMail.Services;

/// <summary>
/// Shared address-parsing logic used by both SMTP and IMAP (drafts).
/// </summary>
public static class AddressParser
{
    /// <summary>
    /// Adds the valid addresses in <paramref name="addressString"/> to <paramref name="list"/>. Entries may be
    /// separated by commas, semicolons or line breaks; a quoted or unquoted name with a comma in it, such as
    /// "Smith, John" &lt;john@example.com&gt;, stays one recipient rather than becoming one called "Smith" (#979).
    /// </summary>
    public static void AddAddresses(InternetAddressList list, string addressString) =>
        AddressInput.AddTo(list, addressString);
}
