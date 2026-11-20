namespace QuickMail.Services;

/// <summary>Outcome of the organization admin-consent flow (#607).</summary>
public enum AdminConsentStatus
{
    /// <summary>The admin granted consent for the organization.</summary>
    Granted,

    /// <summary>The admin declined, cancelled, or closed the window before granting.</summary>
    Declined,

    /// <summary>Azure AD returned an error (e.g. the account is not an administrator).</summary>
    Error,
}

/// <summary>
/// Result of parsing the <c>http://localhost</c> redirect from the <c>/adminconsent</c> flow (#607).
/// <see cref="Error"/> carries Azure AD's error description when <see cref="Status"/> is
/// <see cref="AdminConsentStatus.Error"/>; it is null otherwise.
/// </summary>
public readonly record struct AdminConsentResult(AdminConsentStatus Status, string? Error);
