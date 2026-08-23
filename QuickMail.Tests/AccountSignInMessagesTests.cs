using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #606: the identity-mismatch guidance shown when a sign-in completes as a different account than the
/// one entered. For a Microsoft sign-in it must point to the working path (Help → Grant Admin Consent,
/// #607) rather than dead-ending; for any other provider it stays a plain wrong-account warning.
/// </summary>
public class AccountSignInMessagesTests
{
    [Fact]
    public void Microsoft_PointsToAdminConsentPath()
    {
        var msg = AccountSignInMessages.IdentityMismatchGuidance(
            "user@contoso.com", "admin@contoso.onmicrosoft.com", isMicrosoftSignIn: true);

        Assert.Contains("user@contoso.com", msg);                 // the entered account
        Assert.Contains("admin@contoso.onmicrosoft.com", msg);    // the account that actually signed in
        Assert.Contains("not changed", msg);                       // #202 protection is conveyed
        Assert.Contains("Grant Admin Consent", msg);               // the working path (#607)
        Assert.Contains("does not grant", msg);                    // signing in as admin here doesn't consent
    }

    [Fact]
    public void NonMicrosoft_IsPlainWrongAccountWarning_NoAdminConsentGuidance()
    {
        var msg = AccountSignInMessages.IdentityMismatchGuidance(
            "me@gmail.com", "other@gmail.com", isMicrosoftSignIn: false);

        Assert.Contains("me@gmail.com", msg);
        Assert.Contains("sign in again as me@gmail.com", msg);
        // Google has no admin-consent model — don't send the user chasing a Microsoft-only affordance.
        Assert.DoesNotContain("Grant Admin Consent", msg);
        Assert.DoesNotContain("organization", msg);
    }
}
