using System;

namespace QuickMail.Services;

/// <summary>
/// Thrown when a server-rule operation is refused for lack of the <c>MailboxSettings.ReadWrite</c>
/// permission (Graph returns <c>403</c>).
/// <para>
/// The View must surface this as an <b>admin-directed</b> message — never an in-app "Reauthorize"
/// button. QuickMail requests the per-resource <c>.default</c> scope, so re-authorizing cannot grant
/// a permission the tenant hasn't approved, and in admin-consent tenants the signed-in user is
/// usually not the person who can approve it. See
/// <c>docs/planning/server-rules-pm-dev-spec.md</c> §4 and §5.
/// </para>
/// </summary>
public sealed class ServerRuleConsentRequiredException : Exception
{
    public ServerRuleConsentRequiredException()
        : base("QuickMail doesn't have permission to manage server rules for this account.") { }

    public ServerRuleConsentRequiredException(string message) : base(message) { }

    public ServerRuleConsentRequiredException(string message, Exception? innerException)
        : base(message, innerException) { }
}
