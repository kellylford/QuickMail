using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #606: the prompt shown when a sign-in completes as a different account than the one entered. It offers
/// to sign in again as the entered account (Yes/No). For a Microsoft sign-in it reassures that an admin's
/// approval — if one just happened here — is already saved (it does NOT tell the user to go grant consent,
/// which they just did). For any other provider it's a plain wrong-account retry prompt.
/// </summary>
public class AccountSignInMessagesTests
{
    [Fact]
    public void Microsoft_ReassuresApprovalSaved_AndOffersRetry_NotReGrant()
    {
        var msg = AccountSignInMessages.IdentityMismatchPrompt(
            "user@contoso.com", "admin@contoso.onmicrosoft.com", isMicrosoftSignIn: true);

        Assert.Contains("user@contoso.com", msg);                 // the entered account
        Assert.Contains("admin@contoso.onmicrosoft.com", msg);    // who actually signed in
        Assert.Contains("left as user@contoso.com", msg);          // #202: account not rebound
        Assert.Contains("approval is saved", msg);                 // admin's consent already happened
        Assert.Contains("Sign in again as user@contoso.com", msg); // the offered action

        // Must NOT tell them to go do admin consent — they just did it by signing in as the admin.
        Assert.DoesNotContain("Grant Admin Consent", msg);
        Assert.DoesNotContain("does not grant", msg);
    }

    [Fact]
    public void NonMicrosoft_IsPlainRetryPrompt_NoAdminConsentLanguage()
    {
        var msg = AccountSignInMessages.IdentityMismatchPrompt(
            "me@gmail.com", "other@gmail.com", isMicrosoftSignIn: false);

        Assert.Contains("me@gmail.com", msg);
        Assert.Contains("Sign in again as me@gmail.com", msg);
        // Google has no admin-consent/organization model — don't surface Microsoft-only language.
        Assert.DoesNotContain("organization", msg);
        Assert.DoesNotContain("approval", msg);
        Assert.DoesNotContain("Grant Admin Consent", msg);
    }
}
