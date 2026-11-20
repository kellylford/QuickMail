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
delivered in two phases, **both implemented in this PR**:

- **Phase 1: Windows toast notifications for new mail** — plus clicking a toast opens the
  referenced message. Works whenever QuickMail is running (foreground, background, minimized).
- **Phase 2: Run in background / close to tray.** Lets QuickMail keep running (and keep
  delivering notifications) after the window is closed, restoring from the tray or a toast.

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

### Activation — bring forward + open the clicked message

The toast carries `accountId`, `folder` (the inbox FullName), and — for a single-message toast —
`messageId` in its arguments. `WindowsToastNotificationService.OnActivated` parses them (extracted
to the testable `ParseActivation`) into a `NotificationActivation(accountId, folder, messageId?)`
and raises `Activated`. `App` marshals to the UI thread and calls
`MainWindow.HandleNotificationActivation(act)`, which:

1. Restores + foregrounds the window (`RestoreAndActivate`: `Show()`, un-minimize, `Activate()`,
   Win32 `SetForegroundWindow`) and hides the tray icon.
2. Opens the referenced message via `OpenMessageByIdentityAsync(accountId, folder, messageId)` —
   which mirrors the existing `OpenMessageFromListAsync` mode routing (ReadingPane / Tab / Window)
   minus its drafts check. `SelectMessageAsync` fetches the detail by id (cache or IMAP), so the
   message need not be in the currently-loaded list.

The multi-message ("N new messages") toast has no `messageId`, so its click only brings the app
forward.

**Cold start.** Because Phase 2 keeps the app running, most activations are warm. But a toast can
outlive the app in the Action Center; clicking it then **relaunches** QuickMail via the
auto-registered COM activator, and `OnActivated` fires before accounts connect. `HandleNotificationActivation`
stashes the activation and re-tries it when `MainViewModel.StartupConnectCompleted` fires (after
the startup connect populates the account's folders — `IsAccountReady`), so the message opens once
the account is reachable. If the account never connects, the app is simply left foregrounded.

### Configuration

New `ConfigModel.NotifyOnNewMail` (bool, **default off — opt-in**). Notifications and
run-in-background are both off by default so nothing about the app's behaviour changes until the
user enables them in Settings. Parsed/written by `ConfigService` in the `[global]` section as
`NotifyOnNewMail = on|off`, read live via `_configService.Load()` on each detection so a Settings
change takes effect without restart.

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
5. The user presses **Enter** to activate it → QuickMail restores and comes to the foreground and
   **opens that message** in the user's configured mode (reading pane / tab / window). (Or
   **Delete**/dismiss to clear it, standard toast behaviour.)
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
  `INotificationService? _notifications`. New VM public surface: `event ExitRequested` (Exit now
  raises this instead of calling `Application.Current.Shutdown()` — the View shuts down, which also
  removes a pre-existing MVVM violation), `event StartupConnectCompleted`, `bool IsAccountReady(id)`.
- **Selector-bound item types:** none added (no new ComboBox/ListView item types), so no
  `SelectorItemAccessibilityTests` change.
- **DI / lifecycle:** `WindowsToastNotificationService` constructed in `App.OnStartup`, passed to
  both `MainViewModel` and `MainWindow` (new optional ctor params, so existing tests/construction
  are unaffected). `Activated` (now `Action<NotificationActivation>`) is wired to
  `MainWindow.HandleNotificationActivation`; the service is disposed in `App.OnExit`.
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
- `WindowsToastNotificationService.ParseActivation`: extracts accountId/folder/messageId from a
  toast argument string; the multi-message toast has no messageId; the info toast returns null.
- `WindowsToastNotificationService.SenderDisplayName`: strips the address, keeps the display name.
- `ConfigService`: round-trips `NotifyOnNewMail`, `CloseToTray`, `TrayHintShown` (all default off).
- `SettingsViewModel`: loads and saves `NotifyOnNewMail` and `CloseToTray`.

## Out of scope (Phase 1)

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

# Phase 2 — Run in background / close to tray (implemented)

When `CloseToTray` is on, closing the main window hides it to the notification area instead of
exiting, so the IDLE/delta watchers keep running and toasts keep arriving. The window is restored
from the tray icon or a notification; an explicit Exit truly quits.

## Design

- **Tray icon.** WPF has no native tray icon, so `<UseWindowsForms>true</UseWindowsForms>` is
  enabled *only* for a `System.Windows.Forms.NotifyIcon`, wrapped by a small View-layer
  `Views/TrayIconManager`. WinForms types are always fully qualified and its implicit global usings
  (`System.Windows.Forms`, `System.Drawing`) are removed in the csproj so they can't collide with
  the WPF `System.Windows(.Media)` type names used unqualified across the codebase. Icon = the
  executable's own icon (`Icon.ExtractAssociatedIcon`, falling back to `SystemIcons.Application` —
  the app ships no dedicated `.ico`); tooltip "QuickMail"; context menu **Open QuickMail** / **Exit
  QuickMail**; double-click restores. The icon is created lazily on first hide and is visible only
  while the window is hidden.
- **Config.** `ConfigModel.CloseToTray` (bool, **default off** — preserves today's exit-on-close
  behaviour for existing users) and `ConfigModel.TrayHintShown` (bool, internal one-time-hint
  state). Both in `[global]`; parsed/written by `ConfigService`. Settings → General → Notifications
  gains a checkbox "Keep running in the notification area when I close the window", bound to
  `SettingsViewModel.CloseToTray`.
- **Close handling.** `MainWindow.OnClosing`: if `CloseToTray` and not an explicit exit, cancel the
  close, show the tray icon, and `Hide()`. Because the window is *hidden* (never closed) the process
  stays alive under WPF's default `ShutdownMode=OnLastWindowClose` — no `ShutdownMode` change is
  needed. An explicit exit sets `_explicitExit` first so `OnClosing` performs a real close.
- **Exit paths.** `MainViewModel.Exit` now raises `ExitRequested` (instead of calling
  `Application.Current.Shutdown()` — removing an MVVM violation); `MainWindow` handles it and the
  tray **Exit** item via `RequestExit()`, which sets `_explicitExit` and calls
  `Application.Current.Shutdown()`. `MainWindow.OnClosed` disposes the tray icon so it never lingers.
- **One-time hint.** The first time the window hides to the tray, `INotificationService.ShowInfo`
  shows a "QuickMail is still running in the notification area…" toast (announced natively by
  screen readers, not baked into any control), then `TrayHintShown` is set so it never repeats.
  Its click is ignored (`action=info` toast has no target).

## Keyboard walkthrough

Close-to-tray on, screen reader running:

1. User presses **Alt+F4** (or File → Exit is *not* used). The window vanishes; a toast announces
   "QuickMail is still running in the notification area…" (first time only). Focus returns to the
   desktop/previous app — QuickMail is no longer in the Alt+Tab list.
2. New mail arrives → a toast appears exactly as in Phase 1. **Enter** on it restores QuickMail to
   the foreground and opens the message.
3. To restore manually: **Win+B** moves focus to the notification area; the user arrows to the
   QuickMail icon (announced "QuickMail"), presses **Enter** (or the **Menu** key → "Open
   QuickMail") → the window returns to the foreground.
4. To quit: from the tray icon, **Menu** key → arrow to "Exit QuickMail" → **Enter**; or, when the
   window is open, **Alt** → File → **Exit** (`ExitCommand`). Either performs a real shutdown.

With `CloseToTray` off (default): closing the window exits the app exactly as before; the tray icon
is never created.

## Infrastructure changes

- **F6 ring:** unchanged (the tray icon is an OS surface, not an in-app pane).
- **CommandRegistry:** no new command. Exit keeps its existing `ExitCommand`; close-to-tray is a
  Settings checkbox, not a hotkey.
- **`AutomationProperties.Name`:** one new checkbox, "Keep running in the notification area when I
  close the window" (short label). Tray menu item labels ("Open QuickMail", "Exit QuickMail") are
  WinForms `ToolStripMenuItem` text, exposed to UIA by WinForms.
- **`AccessibilityHelper.Announce`:** none. The one-time hint is a native toast (`ShowInfo`).
- **Build:** `UseWindowsForms=true`; implicit `System.Windows.Forms` / `System.Drawing` usings
  removed. `MainWindow` gains an `INotificationService?` ctor param (for the hint).
- **Lifecycle:** `TrayIconManager` (owns the `NotifyIcon`) is created lazily and disposed in
  `MainWindow.OnClosed`.

## Out of scope (Phase 2)

- **Minimize-to-tray** (only *close* hides to tray; the minimize button still minimizes to the
  taskbar).
- **Persistent tray icon** while the window is open (the icon appears only while hidden).
- **Launch-on-startup / start-hidden** and any autostart registration.
- **Unread-count badge / dynamic tray tooltip** (tooltip is the static "QuickMail").
