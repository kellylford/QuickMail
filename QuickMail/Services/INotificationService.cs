using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Identifies what a clicked toast should open. <see cref="MessageId"/> is null for the
/// "N new messages" toast (no single target — the handler just surfaces the account's inbox).
/// </summary>
public sealed record NotificationActivation(Guid AccountId, string? Folder, string? MessageId);

/// <summary>
/// Shows Windows toast (app) notifications. The single implementation
/// (<see cref="WindowsToastNotificationService"/>) wraps the Windows Community Toolkit
/// notification platform; a null service (tests, unsupported OS) means notifications are
/// simply not shown. See docs/planning/notifications-pm-dev-spec.md.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// False on a down-level OS or if the notification platform rejected registration.
    /// Callers must no-op when false.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Show a new-mail toast for one account. <paramref name="newMessages"/> is the
    /// genuinely-new set — already de-duplicated and gated by the caller. Best-effort:
    /// never throws.
    /// </summary>
    void ShowNewMail(string accountLabel, Guid accountId, IReadOnlyList<MailMessageSummary> newMessages);

    /// <summary>
    /// Show a one-off informational toast (e.g. the "still running in the notification area"
    /// hint). Best-effort: never throws.
    /// </summary>
    void ShowInfo(string title, string message);

    /// <summary>
    /// Raised when the user activates (clicks) a toast, carrying the message to open. May fire
    /// off the UI thread — the handler must marshal to the UI thread before touching any window.
    /// </summary>
    event Action<NotificationActivation>? Activated;
}
