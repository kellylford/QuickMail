# New-Mail Notifications — PM/Dev Spec

Issue: [#240 — Add notification support](https://github.com/kellylford/QuickMail/issues/240)

## Summary

Surface a **Windows toast (app) notification** when new mail arrives, using the
platform's own notification system so it is announced by screen readers, appears in the
Windows notification center, and honours the OS's Focus Assist / Do Not Disturb and
per-app notification settings.

The important realisation from reading the code: **QuickMail already detects new mail**.
The change-notifier infrastructure (`IChangeNotifier`) runs an IMAP IDLE connection per
account (`ImapMailService`) and a Microsoft Graph `/messages/delta` poll
(`GraphChangeNotifier`), aggregated by `ChangeNotifierRouter`. When new mail lands in an
inbox it raises `InboxNewMailDetected(accountId)`, which `MainViewModel.OnInboxNewMailDetected`
already handles by running a targeted inbox sync. So this feature does **not** add polling —
it hangs a toast off the signal that already exists.

Because the notification uses the Windows notification platform, the "accessibility
infrastructure comes from Microsoft" (the issue author's phrasing): screen readers announce
toasts natively, and the user can review/replay them from the notification center
(Win+A) or navigate to the newest with Win+Shift+V. We add **no** custom
`AccessibilityHelper.Announce` for new mail — the toast is the mechanism, and layering an
in-app announcement on top would double-speak.

## Phasing

Issue #240 asks for three things: (1) toast on new mail, (2) minimize-to-tray on close, and
(3) periodic background sync. Item (3) already exists (IDLE / delta poll). This spec is
delivered in two phases:

- **Phase 1 (this PR): Windows toast notifications for new mail.** Works whenever QuickMail
  is running (foreground, background, or minimized to the taskbar) — already the common case
  for an email client.
- **Phase 2 (deferred, spec'd below): Run in background / close to tray.** Lets QuickMail keep
  running (and keep delivering notifications) after the window is closed. Larger surface —
  adds a tray icon, a new interaction model, and its own accessibility requirements — so it
  is intentionally split out.

---

# Phase 1 — Toast notifications (implemented)

## Doing it "properly": the Microsoft rules for an unpackaged Win32 app

QuickMail is an **unpackaged** (non-MSIX) self-contained WPF app installed by Velopack. The
Microsoft-sanctioned path for such apps is the **Windows Community Toolkit** notifications
library (`Microsoft.Toolkit.Uwp.Notifications`), which provides `ToastNotificationManagerCompat`
and `ToastContentBuilder`. For unpackaged apps this library:

- **Auto-registers the AppUserModelID (AUMID) and a COM activator** in the registry on first
  use. No `Package.appxmanifest`, and (on Windows 10 1709+) **no Start Menu shortcut is
  required** — the manifest/shortcut steps in Microsoft's docs are the MSIX-only path.
- **Handles activation** (the user clicking the toast) via the
  `ToastNotificationManagerCompat.OnActivated` static event. Activation may arrive on a
  background thread and must be marshalled to the UI thread before touching any window.
- Requires the project to target a **Windows-10 TFM** (`net8.0-windowsX.0.17763.0` or higher)
  so the underlying `Windows.UI.Notifications` WinRT `.Show()` API is available. QuickMail
  currently targets `net8.0-windows` (= `net8.0-windows7.0`), so the TFM is bumped (below).

### Build changes required

- `QuickMail.csproj`: `TargetFramework` `net8.0-windows` → `net8.0-windows10.0.17763.0`; add
  `PackageReference Microsoft.Toolkit.Uwp.Notifications 7.1.3`.
- `QuickMail.Tests.csproj`: `TargetFramework` bumped to match (a project can only reference a
  project whose platform version is ≤ its own).

`SupportedOSPlatformVersion` becomes 10.0.17763 (Windows 10 1809, Oct 2018). This is the
effective floor for the toast APIs and is a reasonable minimum for a 2026 desktop app.

## Design

### New service: `INotificationService` / `WindowsToastNotificationService`

`Services/INotificationService.cs`:

```csharp
public interface INotificationService
{
    /// <summary>False on down-level OS or if the platform rejected registration; callers no-op.</summary>
    bool IsSupported { get; }

    /// <summary>Show a new-mail toast for one account. `newMessages` is the genuinely-new set
    /// (already de-duplicated and gated by the caller). Best-effort; never throws.</summary>
    void ShowNewMail(string accountLabel, Guid accountId, IReadOnlyList<MailMessageSummary> newMessages);

    /// <summary>Raised (possibly off the UI thread) when the user activates a toast.</summary>
    event Action? Activated;
}
```

`Services/WindowsToastNotificationService.cs` (View/OS-layer service; a service may make OS
calls — the MVVM "no System.Windows in a VM" rule applies to ViewModels, not services):

- `IsSupported` = `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)` AND registration did
  not throw. The whole thing is wrapped so any failure degrades to a silent no-op (logged),
  never a crash.
- Constructor subscribes `ToastNotificationManagerCompat.OnActivated`; the handler raises
  `Activated`. Toast arguments carry `accountId`/`messageId` for future use (Phase 1 only needs
  "bring the app forward").
- `ShowNewMail`:
  - **1 new message:** title = sender display name, body = subject (first non-empty of subject /
    "(no subject)"), optional preview attribution line.
  - **N new messages:** title = "{N} new messages", body = account label.
  - Toasts are tagged/grouped per account (`tag = accountId`, `group = "newmail"`) so repeated
    arrivals for one account collapse rather than stack endlessly.
- App identity (name "QuickMail" + icon) comes from Velopack's Start Menu shortcut, which the
  compat layer latches onto during auto-registration. In a dev run (F5, no shortcut) toasts
  still work but may show a generic app label — expected, not a bug.

### Deciding what is "genuinely new" (no flooding, no duplicates)

The trigger is `OnInboxNewMailDetected(accountId)`, which fires **only** from the change
notifier on a real new-mail event — never during the initial bulk sync. But it carries no
message details, so the targeted inbox sync it already runs is extended to **return** the
fetched summaries:

- `ISyncService.SyncOneFolderAsync` / `SyncOneFolderOnlineAsync` change from `Task` to
  `Task<IReadOnlyList<MailMessageSummary>>`, returning the `incoming` list they already
  compute (empty when nothing was fetched). `FolderSynced` still fires exactly as before.

`MainViewModel` then filters `incoming` down to the genuinely-new set on the **UI thread**
(so the dedupe set is single-thread-owned):

- **Unread only** (`!IsRead`) — a message already read on another device is not new to the user.
- **Arrived this session** (`Date >= _notifyThresholdUtc`, captured at VM construction) — the
  targeted sync fetches the last 50 messages, so the pre-existing backlog is excluded.
- **Not already notified** — a per-key `HashSet<string>` (`accountId + \0 + MessageId`) prevents
  re-notifying the same message when IDLE fires repeatedly within a session.

If any survive, `INotificationService.ShowNewMail` is called. The threshold + unread + dedupe
combination means: no toast for startup backlog, no toast for mail read elsewhere, and no
duplicate toast on repeated IDLE fires.

### Activation

`WindowsToastNotificationService.Activated` → `App` marshals to the UI thread and calls a new
`MainWindow.RestoreAndActivate()` (restore if minimized, `Show()`, `Activate()`, foreground via
`SetForegroundWindow`). Phase 1 click behaviour = **bring QuickMail to the foreground**.
Opening the specific clicked message is deferred (see Out of scope) — the accountId/messageId
are already in the toast args for when we add it.

### Configuration

New `ConfigModel.NotifyOnNewMail` (bool, **default on**). Consistent with the app's other
opt-out defaults (AutoUpdate on, announcements on) and with the issue's intent. Parsed/written
by `ConfigService` in the `[global]` section as `NotifyOnNewMail = on|off`, read live via
`_configService.Load()` on each detection so a Settings change takes effect without restart.

Settings UI: a new **Notifications** GroupBox on the **General** tab with one checkbox,
"Show a _notification when new mail arrives", bound to `SettingsViewModel.NotifyOnNewMail`.

## Keyboard walkthrough

New mail arrives (screen reader running), `NotifyOnNewMail` on:

1. QuickMail is running (foreground, background, or minimized). New mail lands in an inbox.
2. The change notifier raises `InboxNewMailDetected`; the targeted sync fetches it; the message
   is genuinely new (unread, after launch, not seen before).
3. A Windows toast appears. The screen reader announces it natively, e.g.
   "QuickMail. Jane Smith. Subject: Lunch Thursday?" (content order is the platform's).
4. The user presses **Win+Shift+V** to move focus to the most recent notification (standard
   Windows shortcut; not an app shortcut). The screen reader reads the toast.
5. The user presses **Enter** to activate it → QuickMail restores and comes to the foreground;
   focus lands in the main window. (Or **Delete**/dismiss to clear it, standard toast behaviour.)
6. If the user does nothing, the toast auto-dismisses to the notification center (Win+A), where
   it can be reviewed later — all standard OS behaviour QuickMail does not implement.

With `NotifyOnNewMail` off (Settings → General → Notifications, unchecked): step 3 never
happens; nothing else changes.

## Infrastructure changes

- **F6 ring:** unchanged. No new pane; the toast is an OS surface, not an in-app control.
- **CommandRegistry:** no new command. (Toggling notifications is a Settings checkbox, not a
  hotkey action — consistent with the other announcement/appearance settings.)
- **`AutomationProperties.Name` added/changed:** one — the new checkbox "Show a notification
  when new mail arrives" (short label; no role words, no instructions).
- **`AccessibilityHelper.Announce` calls added:** **none.** The toast is delivered by the
  Windows notification platform, which screen readers announce natively. Deliberately not
  layering an in-app announcement (would double-speak).
- **VM state properties:** none of the existing state flags (e.g. `IsMessageOpen`) change. New
  private VM state only: `_notifyThresholdUtc`, `_notifiedMessageKeys`, and the injected
  `INotificationService? _notifications`.
- **Selector-bound item types:** none added (no new ComboBox/ListView item types), so no
  `SelectorItemAccessibilityTests` change.
- **DI / lifecycle:** `WindowsToastNotificationService` constructed in `App.OnStartup`, passed to
  `MainViewModel` (new optional ctor param `INotificationService? notificationService = null`,
  so existing tests/construction are unaffected), `Activated` wired to `MainWindow.RestoreAndActivate`.
- **Interface change:** `ISyncService.SyncOneFolderAsync` / `SyncOneFolderOnlineAsync` return
  `Task<IReadOnlyList<MailMessageSummary>>`. The `SyncService` implementation and the test stub
  are updated; all existing callers ignore the return value, so behaviour is unchanged.

## Tests

- `WindowsToastNotificationService`: `IsSupported` reflects OS version; `ShowNewMail` is a no-op
  (no throw) when unsupported. (Cannot assert a real toast renders in a headless test.)
- New-mail filter logic (extracted to a testable pure helper or exercised via a stub
  `INotificationService`): asserts startup backlog (Date < threshold) does not notify; already-
  read messages do not notify; a repeated IDLE fire for the same message notifies once; a fresh
  unread message notifies with the expected count.
- `ConfigService`: round-trips `NotifyOnNewMail` (on/off, default on when absent).
- `SettingsViewModel`: loads and saves `NotifyOnNewMail`.

## Out of scope (Phase 1)

- **Opening the clicked message.** Click brings the app to the foreground; it does not navigate
  to the specific message (that requires locating the message across folders/accounts and is
  deferred; the args are already carried for a follow-up).
- **Backdated delivery.** A message whose `Date` header predates app launch but is delivered
  after launch will not toast (excluded by the `Date >= threshold` gate). `MailMessageSummary`
  exposes only `Date` (sent), not the IMAP INTERNALDATE (received); using received date is a
  possible future refinement. Real-time arrivals — the overwhelming majority — are unaffected.
- **Per-account notification toggles**, custom sounds, notification actions (Archive/Mark read
  from the toast), and per-message-count batching windows.
- **Notifications for non-inbox folders.** Only inbox new mail notifies, matching the existing
  `InboxNewMailDetected` scope.
- **Mail rules are not pre-applied before notifying.** The targeted IDLE/delta sync
  (`SyncOneFolderAsync`/`SyncOneFolderOnlineAsync`) does not run the rule engine (only the full
  `SyncFolderAsync` does), so a message that a rule will subsequently move or delete can still
  raise a new-mail toast. Pre-existing behaviour of the targeted-sync path; acceptable for Phase 1.
  A future refinement could suppress toasts for rule-matched senders.
- **Everything in Phase 2** (below).

---

# Phase 2 — Run in background / close to tray (deferred)

Not implemented in this PR. Captured so the design is on record.

Goal: when enabled, closing the main window hides it to the system tray instead of exiting, so
IDLE/delta watchers keep running and toasts keep arriving; the app is restored from the tray or
a toast; an explicit Exit truly quits.

Sketch:

- Add `<UseWindowsForms>true</UseWindowsForms>` and a `System.Windows.Forms.NotifyIcon` managed
  by a small View-layer `TrayIconManager` owned by `MainWindow` (or `App`). Icon = app icon,
  tooltip "QuickMail", context menu **Open QuickMail** / **Exit**.
- New `ConfigModel.CloseToTray` (bool, **default off** — preserves today's exit-on-close
  behaviour for existing users). Settings → General → Notifications: "When I close the window,
  keep QuickMail running in the notification area".
- `MainWindow.OnClosing`: if `CloseToTray` and this is not an explicit exit, cancel the close and
  `Hide()`. An explicit-exit flag (set by the tray **Exit** item and a File → Exit menu item)
  bypasses the cancel and calls `Application.Current.Shutdown()`.
- Tray restore reuses `RestoreAndActivate()` (added in Phase 1).

Phase 2 accessibility requirements (must be in its own spec before coding):

- The tray icon and its context menu must be keyboard reachable (Win+B → arrows → Menu/Enter is
  the OS path) and each menu item needs a clear accessible name.
- A keyboard-only way to exit must always exist independent of the mouse (File → Exit).
- First time the window hides to tray, a one-time toast/hint ("QuickMail is still running in the
  notification area") so the app doesn't silently vanish — delivered as a toast (native SR
  announcement), not baked into any control name.
- Decide `ShutdownMode` implications (WPF `OnLastWindowClose` vs `OnExplicitShutdown`) so hiding
  the last window doesn't terminate the process.
