namespace QuickMail.Controls;

public class AddressChipModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public bool IsInvalid { get; set; }

    // Text shown on the chip face
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress : DisplayName;

    // Full RFC-style address used for accessibility name, tooltip, and clipboard copy
    public string FullAddress => string.IsNullOrWhiteSpace(DisplayName)
        ? EmailAddress
        : $"{DisplayName} <{EmailAddress}>";

    // Serialized form written back to the To/Cc/Bcc string binding
    public string Serialize() => FullAddress;
}
