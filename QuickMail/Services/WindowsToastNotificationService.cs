using System;
using System.Collections.Generic;
using Microsoft.Toolkit.Uwp.Notifications;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Windows Community Toolkit implementation of <see cref="INotificationService"/> for an
/// unpackaged (non-MSIX) Win32 app. <see cref="ToastNotificationManagerCompat"/> auto-registers
/// the AppUserModelID and a COM activator in the registry on first use — no appxmanifest and (on
/// Windows 10 1709+) no Start Menu shortcut required. The app name and icon shown on the toast
/// come from Velopack's Start Menu shortcut, which the compat layer latches onto; a dev run with
/// no shortcut still works but may show a generic label.
///
/// Every platform call is guarded: any failure degrades to a logged no-op, never a crash.
/// See docs/planning/notifications-pm-dev-spec.md.
/// </summary>
public sealed class WindowsToastNotificationService : INotificationService, IDisposable
{
    private const string NewMailGroup = "newmail";

    private readonly bool _osSupported;
    private bool _activationHooked;

    public event Action? Activated;

    public WindowsToastNotificationService()
    {
        _osSupported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
        if (!_osSupported) return;

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _activationHooked = true;
        }
        catch (Exception ex)
        {
            // Registration/hook failed — treat notifications as unsupported for this session.
            _osSupported = false;
            LogService.Log("WindowsToastNotificationService: activation hook failed", ex);
        }
    }

    public bool IsSupported => _osSupported;

    public void ShowNewMail(string accountLabel, Guid accountId, IReadOnlyList<MailMessageSummary> newMessages)
    {
        if (!_osSupported || newMessages is null || newMessages.Count == 0) return;

        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument("action", "openMail")
                .AddArgument("accountId", accountId.ToString());

            if (newMessages.Count == 1)
            {
                var m = newMessages[0];
                var display = SenderDisplayName(m.From);
                var sender  = string.IsNullOrWhiteSpace(display) ? accountLabel : display;
                var subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject;
                builder.AddArgument("messageId", m.MessageId)
                       .AddText(sender)
                       .AddText(subject);
                if (!string.IsNullOrWhiteSpace(m.Preview))
                    builder.AddText(m.Preview);
            }
            else
            {
                builder.AddText($"{newMessages.Count} new messages")
                       .AddText(accountLabel);
            }

            // Tag/group per account so repeated arrivals for one account collapse rather than
            // stack endlessly in the notification center.
            builder.Show(toast =>
            {
                toast.Tag   = Sanitize(accountId.ToString());
                toast.Group = NewMailGroup;
            });
        }
        catch (Exception ex)
        {
            LogService.Log("WindowsToastNotificationService: ShowNewMail failed", ex);
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        // Fires on a background thread. Just surface the event; the subscriber marshals to the UI
        // thread. Phase 1 ignores the arguments (click = bring the app forward); accountId/messageId
        // are carried for a future "open the clicked message" enhancement.
        try { Activated?.Invoke(); }
        catch (Exception ex) { LogService.Log("WindowsToastNotificationService: activation handler threw", ex); }
    }

    // Toast tags/groups are limited to 64 chars and a restricted character set; a Guid string is
    // safe, but sanitize defensively so an unexpected value can never throw inside Show().
    private static string Sanitize(string s) => s.Length <= 64 ? s : s[..64];

    // Reduce the From header to a display name so the toast (and the screen reader reading it)
    // says "Jane Smith", not "Jane Smith jane@example.com". Falls back to the bare address, then
    // to the raw header, so a toast never ends up with an empty sender.
    internal static string SenderDisplayName(string from)
    {
        if (string.IsNullOrWhiteSpace(from)) return string.Empty;
        try
        {
            if (MimeKit.MailboxAddress.TryParse(from, out var mb))
                return string.IsNullOrWhiteSpace(mb.Name) ? mb.Address : mb.Name;
        }
        catch { /* malformed header — fall back to the raw string */ }
        return from;
    }

    public void Dispose()
    {
        if (_activationHooked)
        {
            try { ToastNotificationManagerCompat.OnActivated -= OnToastActivated; }
            catch { /* best effort */ }
            _activationHooked = false;
        }
    }
}
