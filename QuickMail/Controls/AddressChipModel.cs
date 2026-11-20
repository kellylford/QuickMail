namespace QuickMail.Controls;

public class AddressChipModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public bool IsInvalid { get; set; }

    // Text shown on the chip face
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress : DisplayName;

    // Readable address used for the accessible name and tooltip. Names are never quoted here:
    // a screen reader would otherwise read the quotation marks.
    public string FullAddress => string.IsNullOrWhiteSpace(DisplayName)
        ? EmailAddress
        : $"{DisplayName} <{EmailAddress}>";

    // Serialized form written back to the To/Cc/Bcc string binding and put on the clipboard.
    // A name holding a comma, semicolon or other RFC 5322 special is quoted, so "Smith, John"
    // is read back as one person rather than as "Smith" and "John" (#979).
    public string Serialize() => Helpers.AddressInput.Format(DisplayName, EmailAddress);
}
