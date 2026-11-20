namespace QuickMail.Helpers;

/// <summary>User-facing messages for the account sign-in flow. Centralized so the Add-Account and
/// Account-Manager dialogs show identical, in-sync text.</summary>
public static class AccountSignInMessages
{
    /// <summary>
    /// Body of the prompt shown when a Microsoft/Google sign-in completes as a DIFFERENT identity than the
    /// one entered — the #202 guard has kept the account bound to <paramref name="entered"/> and discarded
    /// the other sign-in. Phrased as a yes/no question because the dialog offers to sign in again as
    /// <paramref name="entered"/> (Yes) or return to the account screen (No).
    ///
    /// The common Microsoft case (#606) is an administrator signing in at the "needs admin approval" screen
    /// to approve QuickMail for the organization. That approval IS granted when they do it — so the message
    /// reassures that it's saved and simply invites the user to finish by signing in as themselves. (It does
    /// NOT tell them to go grant consent again — they just did.) For any other provider it's a plain
    /// wrong-account retry prompt (no admin-consent model).
    /// </summary>
    public static string IdentityMismatchPrompt(string entered, string actual, bool isMicrosoftSignIn)
    {
        // "this account is {entered}" (not "you're adding") so the wording fits both dialogs — Add Account
        // and the Account Manager re-authenticating an existing account.
        var lead = $"You signed in as {actual}, but this account is {entered} — so it was left as {entered}.";

        var middle = isMicrosoftSignIn
            ? "\n\nIf an administrator just approved QuickMail for your organization here, that approval is saved.\n\n"
            : "\n\n";

        return $"{lead}{middle}Sign in again as {entered} to finish?";
    }
}
