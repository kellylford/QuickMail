namespace QuickMail.Helpers;

/// <summary>User-facing messages for the account sign-in flow. Centralized so the Add-Account and
/// Account-Manager dialogs show identical, in-sync text.</summary>
public static class AccountSignInMessages
{
    /// <summary>
    /// Shown when a Microsoft/Google sign-in completes as a DIFFERENT identity than the one entered — the
    /// #202 guard has kept the account bound to <paramref name="entered"/> and discarded the other sign-in.
    ///
    /// For a Microsoft sign-in (<paramref name="isMicrosoftSignIn"/>) this is usually an administrator who
    /// signed in at the "needs admin approval" screen to approve QuickMail for the organization — but signing
    /// in there does NOT grant organization consent, and the guard discards the admin's sign-in, leaving the
    /// user stuck (#606). So the message points to the path that actually works: the in-app admin-consent
    /// action (#607). For any other provider it is just a wrong-account warning (no admin-consent model).
    /// </summary>
    public static string IdentityMismatchGuidance(string entered, string actual, bool isMicrosoftSignIn)
    {
        var lead = $"You entered {entered}, but sign-in completed as {actual}. The account was not changed.";

        if (!isMicrosoftSignIn)
            return $"{lead}\n\nPlease sign in again as {entered}.";

        return $"{lead}\n\n" +
               "If an administrator signed in here to approve QuickMail for your organization: signing in " +
               "on this screen does not grant that approval. To approve QuickMail for the whole " +
               "organization, an administrator should choose Help → Grant Admin Consent for Your " +
               "Organization (or grant consent in the Microsoft Entra admin center). Once that is done, " +
               $"everyone signs in as themselves with no further prompts.\n\n" +
               $"Otherwise, if you simply used the wrong account, sign in again as {entered}.";
    }
}
