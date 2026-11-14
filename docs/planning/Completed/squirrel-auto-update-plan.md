# Plan: Automatic Updates via Clowd.Squirrel

## Status

**Superseded.** Tracked in [#156](https://github.com/kellylford/QuickMail/issues/156). See
[velopack-auto-update-plan.md](velopack-auto-update-plan.md) for the active plan.

A spike branch (`worktree-squirrel-auto-update`) implemented Phase 1 of this plan and hit two
packaging blockers: `squirrel pack` rejects the project's 4-part version scheme (`X.Y.Z.W`,
e.g. `0.7.8.1`) as non-SemVer, and the exe must carry a `SquirrelAwareVersion` manifest marker
that `SquirrelAwareApp.HandleEvents()` alone does not provide. Before fixing those, research
surfaced that Clowd.Squirrel is effectively superseded by [Velopack](https://velopack.io/) —
built by the same author as the actual successor to Squirrel.Windows/Clowd.Squirrel, with an
official migration path from Squirrel. The spike branch was discarded rather than fixed forward.
A follow-up spike on `worktree-velopack-auto-update` confirmed Velopack resolves both blockers,
and the [new plan](velopack-auto-update-plan.md) carries that work forward. This document is
kept for historical context only.

## TL;DR

Replace the Inno Setup installer with a Clowd.Squirrel package so that installed users receive future updates silently and automatically. The existing startup update check (added in v0.7.7) becomes the trigger: when an update is found, Squirrel downloads it in the background and applies it on the next launch — no browser, no SmartScreen, no installer dialog. For existing users, this requires one reinstall to get onto the Squirrel track; after that, updates are invisible.

---

## Why Squirrel (not the alternatives)

- **Option 1 (self-download + prompt):** The app downloads the new installer and prompts the user to run it. That downloaded exe is unsigned, so SmartScreen blocks it at launch — same friction as today, except one step later.
- **Option 2 (Squirrel) — this plan:** Updates are downloaded by the running app via `HttpClient`, not the browser. Files landed in `%LocalAppData%` without a Zone.Identifier mark are never flagged by SmartScreen. The update is already extracted and ready before the user knows it exists. Battle-tested on GitHub Desktop, Slack, Teams.
- **Option 3 (WinGet):** Good for discoverability and the command-line path, but doesn't deliver the in-app seamless update experience. Can be done alongside this, not instead of it.

## Code signing interaction

Squirrel works unsigned. The initial setup.exe download has the same SmartScreen friction it has today — no better, no worse. Once installed, all future updates completely bypass SmartScreen. When code signing is eventually resolved (Azure Trusted Signing address verification is in progress), the initial download friction disappears for new users too. Squirrel does not need to wait for signing.

---

## What does NOT change

- **User data (`%APPDATA%\QuickMail`)** — entirely separate from the exe install path. Squirrel installs to `%LocalAppData%\QuickMail\app-x.y.z\`. The two paths never overlap. Accounts, settings, contacts, rules, templates, saved views, and the mail cache are untouched.
- **Windows Credential Manager** — passwords are keyed by credential name, not exe location. Untouched.
- **`--profileDir` users** — `ProfileContext` resolves the data directory from the command-line argument, independent of where the exe lives. Unaffected.
- **Portable exe** — stays as-is. Still shows the Help menu update notification; still requires the user to manually download the new portable exe. Squirrel only benefits the installed path.
- **Help menu UX** — the "No updates available" / "Update available: version X.Y.Z" entry and `OpenUpdatePageCommand` stay. The underlying mechanism changes from a raw GitHub API check to Squirrel's `UpdateManager`, but the visible behavior is the same or better (update is already downloaded when the entry appears).

---

## Migration story

### Existing users (currently on Inno Setup install)

The Squirrel install goes to a different location than the Inno Setup install (`%LocalAppData%` vs. Program Files or the per-user programs folder). To get onto the auto-update track:

1. Uninstall the current version via Programs & Features / Settings → Apps.
   - The uninstaller will offer to delete user data — **select No**. User data in `%APPDATA%\QuickMail` should be kept.
   - Credential Manager entries are not touched by the uninstaller.
2. Download and run the new Squirrel-based setup.exe (same GitHub Releases page as always).
3. Launch QuickMail — everything is exactly as they left it.

This is a one-time ask. Release notes for the version that ships Squirrel must explain this clearly.

### New users

Same experience as today: download setup.exe from GitHub, fight through SmartScreen once, done. Future updates are silent.

### Users who skip the migration

A user who stays on their Inno Setup install indefinitely will continue to receive Help-menu update notifications and can manually download new releases. They just don't get the silent-update benefit until they reinstall.

---

## Implementation Phases

### Phase 1 — NuGet + in-app code

**Goal:** App participates in Squirrel's install/uninstall lifecycle and uses `UpdateManager` for checking and downloading updates.

1. Add `Clowd.Squirrel` NuGet package to `QuickMail.csproj`.
2. In `App.xaml.cs` `OnStartup`, add `SquirrelAwareApp.HandleEvents()` call **before** anything else. This handles:
   - `onInitialInstall` — create Start menu shortcut (replaces Inno Setup's `[Icons]` section)
   - `onAppUpdate` — update the shortcut target to the new versioned folder
   - `onAppUninstall` — remove Start menu shortcut
3. Replace `UpdateCheckService.CheckForUpdateAsync` with a Squirrel-backed implementation:
   ```csharp
   using var mgr = new UpdateManager("https://github.com/kellylford/QuickMail");
   var info = await mgr.CheckForUpdate();
   if (info.ReleasesToApply.Any())
   {
       // Download in background — already done by the time the user sees the notification
       await mgr.DownloadReleases(info.ReleasesToApply, progress => { /* optional */ });
       return new UpdateInfo(info.FutureReleaseEntry.Version.ToString(), releaseUrl);
   }
   return null;
   ```
4. The existing `CheckForUpdateInBackgroundAsync` in `MainViewModel` already fires on startup and wires the result to `UpdateAvailableText` / `OpenUpdatePageCommand`. Keep that wiring; only the service implementation changes.
5. `ApplyReleases` does not need to be called in-process. Squirrel applies the downloaded update on the next launch automatically via `Update.exe`. The "restart to update" step is transparent to the user.

**Tests:** `IUpdateCheckService` is already stubbed in `StubServices.cs`. No new test infrastructure needed; existing tests are unaffected.

### Phase 2 — Packaging

**Goal:** `build.bat installer` produces a Squirrel `setup.exe` and `Releases/` folder instead of an Inno Setup installer.

1. Install the Squirrel CLI tool (available as a .NET tool: `dotnet tool install -g clowd.squirrel`).
2. Replace the `installer` target in `build.bat`:
   ```bat
   :: Old: ISCC.exe installer\quickmail.iss
   :: New:
   squirrel pack --framework net8 --packId QuickMail --packVersion %VERSION% ^
     --packAuthors "Kelly Ford" --packDir publish\ ^
     --releaseDir installer\Output\Releases
   ```
3. Output: `installer\Output\Releases\` containing:
   - `QuickMail-x.y.z-full.nupkg` — full package
   - `QuickMail-x.y.z-delta.nupkg` — binary delta from previous release (generated automatically after the second release)
   - `RELEASES` — manifest file consumed by `UpdateManager`
   - `QuickMail-x.y.z-Setup.exe` — the user-facing installer (replaces `quickmail-v0.7.x-setup.exe`)

The Inno Setup script (`installer/quickmail.iss`) and `CodeDependencies.iss` can be retained for now in case a fallback is needed, but they are no longer part of the build process.

**WebView2 note:** The Inno Setup script currently detects and installs WebView2 on demand. Squirrel has no equivalent hook. WebView2 is bundled with Windows 11 and most current Windows 10 installs; in practice the prereq check is rarely needed. If this becomes a support issue, a startup check in `App.xaml.cs` can detect a missing WebView2 runtime and show a one-time dialog with a download link.

### Phase 3 — GitHub Actions release workflow

**Goal:** The release workflow produces and uploads Squirrel assets instead of the Inno Setup installer.

Current flow: `dotnet publish` → `ISCC.exe` → upload `quickmail-vX.Y.Z-setup.exe` + `QuickMail.exe`.

New flow: `dotnet publish` → `squirrel pack` → upload contents of `Releases/` + `QuickMail.exe` (portable, unchanged).

The `UpdateManager` GitHub backend discovers releases by querying the GitHub Releases API and matching asset names. Assets must include the `RELEASES` file and the `.nupkg` files. The setup.exe can also be an asset (it's the initial installer for new users).

**Important:** The `Releases/` folder must be cumulative — each release adds new nupkg assets to the same release listing, or assets from previous releases must remain available for delta generation. The GitHub Releases approach works because `UpdateManager` checks the latest release's assets. Delta packages reference the previous full package by hash, so the previous release's assets must remain publicly accessible.

### Phase 4 — Release notes + user guide

**Goal:** Communicate the one-time migration clearly.

- Release notes for the first Squirrel-based version: explain the one-time reinstall, confirm data is preserved, mention passwords are safe.
- `docs/USER-GUIDE.md`: update the installation section to reflect the new installer and the auto-update behavior.
- `docs/INSTALLER.md`: update to reflect the new packaging tool and output structure.

---

## Files to Modify

| File | Change |
|------|--------|
| `QuickMail/QuickMail.csproj` | Add `Clowd.Squirrel` NuGet reference |
| `QuickMail/App.xaml.cs` | Add `SquirrelAwareApp.HandleEvents()` before `OnStartup` logic |
| `QuickMail/Services/UpdateCheckService.cs` | Replace GitHub API implementation with `UpdateManager` |
| `QuickMail/Services/IUpdateCheckService.cs` | No interface change expected |
| `build.bat` | Replace `installer` target |
| `.github/workflows/release.yml` (or equivalent) | Replace ISCC step with squirrel pack; update uploaded assets |
| `docs/INSTALLER.md` | Update to describe new packaging |
| `docs/USER-GUIDE.md` | Update installation and update sections |
| `docs/release-notes-v0.x.x.md` | Migration note for the first Squirrel release |

## Files Retired

| File | Fate |
|------|------|
| `installer/quickmail.iss` | Retired (keep in repo for reference until first Squirrel release ships successfully) |
| `installer/CodeDependencies.iss` | Retired with the above |
| `installer/Languages/Custom.en.isl` | Retired with the above |

---

## Decisions & Scope Boundaries

- **Update is applied on next launch, not immediately.** Squirrel downloads silently; on next app start `Update.exe` applies the pending update before handing off to the new version. No in-session restart prompt is needed unless we add one later.
- **Delta updates are automatic** after the second Squirrel release. No extra work required; `squirrel pack` generates them from the previous full package.
- **Portable exe is not in scope.** It continues to use the existing Help menu notification and requires manual download to update.
- **WebView2 prereq check is removed.** Acceptable given current Windows install base. Revisit if support issues arise.
- **Shortcut creation moves from Inno Setup to Squirrel.** The `SquirrelAwareApp.HandleEvents()` `onInitialInstall` handler is responsible. Desktop shortcut should remain opt-in (not created by default), consistent with current Inno Setup behavior (`Flags: unchecked`).
- **The `--profileDir` command-line flag** needs to survive across Squirrel launches. If a user's shortcut passes `--profileDir`, the Squirrel `onAppUpdate` handler must preserve that argument when updating the shortcut target. Verify this during implementation.
- **All-users install is no longer supported.** Squirrel always installs per-user to `%LocalAppData%`. The current Inno Setup installer offered an opt-in all-users (elevated) path. This is a minor regression; the per-user default was always the recommended path.
- **Out of scope:** WinGet package submission (can be done in parallel or later). Code signing (separate ongoing work with Azure Trusted Signing).

---

## Verification Checklist

1. Fresh install: run Squirrel setup.exe, verify QuickMail launches, Start menu shortcut present, no desktop shortcut by default.
2. Existing user migration: uninstall Inno Setup version (decline data deletion), install Squirrel version, verify all accounts, mail, contacts, rules, and settings are intact and passwords work.
3. `--profileDir` user: verify custom profile path still respected after Squirrel install and after an update cycle.
4. Update cycle: simulate update by packing v+1, placing assets, verify in-app check finds it, downloads it, and applies on next launch without user action beyond restarting.
5. Delta update: verify second release generates a delta nupkg and that `UpdateManager` uses it (smaller download than full).
6. Portable exe: verify it continues to work unchanged, still shows Help menu update notification.
7. Uninstall: verify Start menu shortcut removed, `%LocalAppData%\QuickMail` removed, `%APPDATA%\QuickMail` untouched.
8. Uninstall + reinstall: verify user data survives the full cycle.
