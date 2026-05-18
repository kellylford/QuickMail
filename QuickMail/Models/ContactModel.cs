namespace QuickMail.Models;

public class ContactModel
{
    public int    Id            { get; set; }
    public string DisplayName   { get; set; } = string.Empty;
    public string EmailAddress  { get; set; } = string.Empty;
    public long   LastUsedTicks { get; set; }

    public string Display => string.IsNullOrWhiteSpace(DisplayName)
        ? EmailAddress
        : $"{DisplayName} <{EmailAddress}>";
}
