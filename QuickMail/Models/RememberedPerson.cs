using System;
using System.Globalization;

namespace QuickMail.Models;

/// <summary>
/// One row of the address book's People I've Sent To tab (#1039): someone remembered from sent mail
/// who is not in the address book.
/// </summary>
public sealed class RememberedPerson
{
    public RememberedPerson(string displayName, string emailAddress, long lastSentUtcTicks, bool showFieldLabels = false)
    {
        DisplayName = displayName ?? string.Empty;
        EmailAddress = emailAddress ?? string.Empty;
        LastSentUtcTicks = lastSentUtcTicks;
        _showFieldLabels = showFieldLabels;
    }

    private readonly bool _showFieldLabels;

    public string DisplayName { get; }
    public string EmailAddress { get; }
    public long LastSentUtcTicks { get; }

    /// <summary>When they were last sent to, as a local date the way the rest of the app writes one.</summary>
    public string LastSentText => LastSentUtcTicks <= 0
        ? string.Empty
        : new DateTime(LastSentUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>The name as shown, or the address for someone remembered without one.</summary>
    public string ShownName => string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress : DisplayName;

    /// <summary>
    /// What a screen reader reads for the row, set on the row container (#644): name, address and when,
    /// with field labels when Settings asks for them in the contact list.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var hasName = !string.IsNullOrWhiteSpace(DisplayName);
            var when = LastSentText;
            if (_showFieldLabels)
            {
                var head = hasName ? $"Name {DisplayName}, email {EmailAddress}" : $"Email {EmailAddress}";
                return when.Length == 0 ? head : $"{head}, last sent {when}";
            }
            var plain = hasName ? $"{DisplayName}, {EmailAddress}" : EmailAddress;
            return when.Length == 0 ? plain : $"{plain}, last sent {when}";
        }
    }

    /// <summary>What type-ahead matches in the list: the name, or the address when there is none.</summary>
    public string TypeAheadText => ShownName;

    /// <summary>The last-resort accessible name of a list item; see <see cref="AccessibleName"/>.</summary>
    public override string ToString() => AccessibleName;
}
