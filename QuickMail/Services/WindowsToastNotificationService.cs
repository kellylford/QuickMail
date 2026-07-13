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

    // Activation can arrive on a cold start (a toast clicked while the app was closed relaunches it)
    // before App has subscribed. Buffer the most recent one and flush it to the first subscriber so
    // the click is never dropped in the startup window. Guarded because OnActivated fires on a
    // background thread while the subscribe happens on the UI thread.
    private readonly object _activationLock = new();
    private Action<NotificationActivation>? _activated;
    private NotificationActivation? _bufferedActivation;

    public event Action<NotificationActivation>? Activated
    {
        add
        {
            NotificationActivation? replay = null;
            lock (_activationLock)
            {
                _activated += value;
                if (_bufferedActivation != null) { replay = _bufferedActivation; _bufferedActivation = null; }
            }
            if (replay != null) value?.Invoke(replay);
        }
        remove
        {
            lock (_activationLock) { _activated -= value; }
        }
    }

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
            // Carry the inbox folder so a click can open the exact message without re-resolving it.
            var folder = newMessages[0].FolderName ?? string.Empty;
            var builder = new ToastContentBuilder()
                .AddArgument("action", "openMail")
                .AddArgument("accountId", accountId.ToString())
                .AddArgument("folder", folder);

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

    public void ShowInfo(string title, string message)
    {
        if (!_osSupported) return;
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "info")
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch (Exception ex)
        {
            LogService.Log("WindowsToastNotificationService: ShowInfo failed", ex);
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        // Fires on a background thread. Parse the toast arguments into a NotificationActivation and
        // surface it; the subscriber marshals to the UI thread. A missing/unparseable accountId
        // yields Guid.Empty, which the handler treats as "just bring the app forward".
        try
        {
            var act = ParseActivation(e.Argument);
            if (act != null) DispatchActivation(act);
        }
        catch (Exception ex) { LogService.Log("WindowsToastNotificationService: activation handler threw", ex); }
    }

    // Raises Activated, or buffers when no subscriber has attached yet (cold start still wiring up)
    // so the click is delivered to the first subscriber instead of being dropped.
    internal void DispatchActivation(NotificationActivation act)
    {
        Action<NotificationActivation>? handler;
        lock (_activationLock)
        {
            handler = _activated;
            if (handler == null) { _bufferedActivation = act; return; }
        }
        handler.Invoke(act);
    }

    /// <summary>
    /// Parses a toast's argument string into a <see cref="NotificationActivation"/>, or null when it
    /// is the "still running" info toast (which has no target and must not open a message). A missing
    /// or unparseable accountId yields <see cref="Guid.Empty"/>, which the handler treats as
    /// "just bring the app forward".
    /// </summary>
    internal static NotificationActivation? ParseActivation(string argument)
    {
        var args = ToastArguments.Parse(argument);

        if (args.TryGetValue("action", out var action) && action == "info") return null;

        // accountId is Guid.Empty when absent or unparseable, which the activation handler treats
        // as "just bring the app forward". Discard the bool result explicitly (satisfies CA1806).
        _ = Guid.TryParse(args.Get("accountId"), out var accountId);
        var folder    = args.Contains("folder") ? args.Get("folder") : null;
        var messageId = args.Contains("messageId") ? args.Get("messageId") : null;
        return new NotificationActivation(accountId, folder, messageId);
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
