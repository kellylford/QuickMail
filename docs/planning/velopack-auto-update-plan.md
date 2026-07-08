# Plan: Automatic Updates via Velopack

## Status

**Implemented** (July 2026). Tracked in [#156](https://github.com/kellylford/QuickMail/issues/156). Supersedes [squirrel-auto-update-plan.md](squirrel-auto-update-plan.md).

The original spike branch (`worktree-velopack-auto-update`) was deleted before its code was
merged, so Phases 1–4 were re-implemented from this document. Everything the spike validated
reproduced cleanly: `vpk pack` (Velopack 1.2.0) succeeds against the real single-file publish
output and verifies the entry point via IL inspection, and the full test suite passes with the
in-app changes. See `docs/INSTALLER.md` for the as-built packaging and release flow. Items
still pending live verification are the install/update-cycle checks in the Verification
Checklist below (fresh install, migration from Inno Setup, update apply-on-relaunch,
`--profileDir` across an update, delta generation on the second release).

## TL;DR

Same goal as the original Squirrel plan: replace the Inno Setup installer with a package that
auto-updates installed users silently in the background, using the existing startup update check
(v0.7.7) as the trigger. Framework choice changes from Clowd.Squirrel to
[Velopack](https://velopack.io/) — the actively maintained successor, built by the same author,
with a documented migration path from Squirrel.Windows/Clowd.Squirrel.

---

## Why Velopack, and what the spike proved

Clowd.Squirrel was spiked first and hit two packaging blockers:
1. `squirrel pack` rejected a 4-part version string as non-SemVer.
2. `squirrel pack` refused to release an exe without a hand-added `SquirrelAwareVersion` comment
   embedded in a Win32 manifest resource — a separate, undocumented step from calling
   `SquirrelAwareApp.HandleEvents()` in code.

Research (see commit history on `docs/planning/squirrel-auto-update-plan.md`) found Velopack is
the same author's actual successor project, post-1.0 since May 2024, MIT-licensed, actively
maintained, with first-class GitHub Releases and Azure Trusted Signing support.

**The spike then validated this hands-on, not just from docs:**
- Added the `Velopack` NuGet package, flipped `App.xaml`'s Build Action from
  `ApplicationDefinition` to `Page`, added `<StartupObject>QuickMail.App</StartupObject>`, and
  added an explicit `[STAThread] static void Main(string[] args)` calling
  `VelopackApp.Build().Run()` before WPF initializes. Full build + all 797 tests pass unchanged.
- Rewrote `UpdateCheckService` against Velopack's real API (checked via reflection against the
  installed package, not assumed from docs): `UpdateManager.IsInstalled` / `IsPortable`
  properties exist exactly as needed to branch between the Velopack path and the existing
  portable-exe GitHub API fallback. `CheckForUpdatesAsync()` / `DownloadUpdatesAsync()` map
  cleanly onto the same background-download pattern the Squirrel version used.
- Ran `dotnet publish` (unchanged self-contained single-file settings) then
  `vpk pack --packId QuickMail --packVersion 0.7.9 --packDir publish --mainExe QuickMail.exe`
  against the real build output. **It succeeded** — no version-format error, and no manifest hack
  needed: `vpk pack` auto-verifies the entry point via IL inspection of the built exe
  (`Verified VelopackApp.Run() in 'System.Void QuickMail.App::Main(System.String)'`).
- Output: `QuickMail-0.7.9-full.nupkg`, `QuickMail-win-Setup.exe`, `QuickMail-win-Portable.zip`,
  `RELEASES`, `releases.win.json`, `assets.win.json`.

**New finding from the spike, not obvious from docs:** unzipping `QuickMail-win-Portable.zip`
shows it is not a bare single exe — it contains a small `QuickMail.exe` shim (~1.7MB),
`Update.exe`, and the real 225MB self-contained exe under `current\QuickMail.exe`. Velopack's
"portable" package is a portable *install layout*, not a rename of today's one-file
`publish\QuickMail.exe`. See **Decisions & Scope Boundaries** below for how this plan handles it.

## Versioning decision

QuickMail's `0.7.8.1` four-part scheme for the v0.7.8.1 patch release was a one-off, not a
convention to preserve. Going forward, release versions passed to `vpk pack --packVersion` must
be plain 3-part SemVer (`0.7.9`, `0.8.0`, etc.) or a SemVer2 prerelease/build tag if a point
release is ever needed again (`0.7.9-1`) — Velopack has its own strict `SemanticVersion` parser,
same constraint class as Squirrel had, just resolved by not fighting it. This does not require
changing `AssemblyVersion`/`FileVersion` in the csproj (those can stay whatever Windows
file-property convention is wanted), only the string passed to `--packVersion`; confirm this
decoupling explicitly during implementation rather than assuming it.

---

## What does NOT change

- **User data (`%APPDATA%\QuickMail`)** — entirely separate from the exe install path. Velopack
  installs to `%LocalAppData%\QuickMail\current\`. Accounts, settings, contacts, rules,
  templates, saved views, and the mail cache are untouched. (Same guarantee as the Squirrel plan;
  not yet verified end-to-end with a real install/uninstall cycle — see Verification Checklist.)
- **Windows Credential Manager** — passwords are keyed by credential name, not exe location.
  Untouched.
- **`--profileDir` users** — `ProfileContext` resolves the data directory from the command-line
  argument, independent of where the exe lives. `UpdateManager.ApplyUpdatesAndRestart` /
  `WaitExitThenApplyUpdates` both take an explicit `restartArgs` parameter, so passing through
  `Environment.GetCommandLineArgs()[1..]` at apply time is achievable in code — but whether a
  Start Menu shortcut carrying `--profileDir` survives Velopack's own shortcut refresh on update
  is **unverified** and needs a real update-cycle test.
- **Help menu UX** — the "No updates available" / "Update available: version X.Y.Z" entry and
  `OpenUpdatePageCommand` stay. The underlying mechanism changes from a raw GitHub API check to
  Velopack's `UpdateManager`, same as the Squirrel plan intended.

## What changes from the original Squirrel plan

- **Portable distribution.** Because Velopack's portable package is a multi-file layout (shim +
  `Update.exe` + `current\`), not a rename of today's single `publish\QuickMail.exe`, this plan
  recommends **keeping today's raw self-contained single-file exe as the portable download**,
  built exactly as it is now, and using `vpk pack`'s output purely for the installed/auto-update
  track. This preserves the current one-file-download portable experience and avoids shipping
  users a zip where they expect an exe. Velopack's own `-Portable.zip` output is not used.
- **No manifest/assembly marker step.** Squirrel's Phase 1 needed a documented but easy-to-miss
  manifest comment; Velopack's `vpk pack` verifies the entry point itself via IL inspection, so
  there is one fewer manual step and one fewer way to silently ship a broken package.

---

## Migration story

Unchanged in shape from the Squirrel plan — existing Inno Setup users need a one-time manual
step to land on the auto-update track:

1. Uninstall the current version via Programs & Features / Settings → Apps, **declining** the
   offer to delete user data.
2. Download and run the new Velopack-based `QuickMail-Setup.exe` (same GitHub Releases page).
3. Launch QuickMail — everything is exactly as they left it.

New users get the same experience as today (download, run, one SmartScreen prompt until
signing lands); future updates for both groups are then silent.

---

## Implementation Phases

### Phase 0 — Spike (complete)

Everything under "What the spike proved" above. Lives on `worktree-velopack-auto-update`
(unmerged). Implementation resumes from this branch.

### Phase 1 — NuGet + in-app code (spike-complete, needs hardening)

Done in the spike:
- `Velopack` NuGet package added.
- `App.xaml` Build Action → `Page`, `<StartupObject>QuickMail.App</StartupObject>` added to
  `QuickMail.csproj`.
- Custom `Main` in `App.xaml.cs` calling `VelopackApp.Build().Run()` before WPF init.
- `UpdateCheckService` rewritten: `UpdateManager.IsInstalled` branches between the Velopack path
  (`CheckForUpdatesAsync` → fire-and-forget `DownloadUpdatesAsync` in the background, same
  pattern the Squirrel version used) and the existing portable-exe GitHub API fallback.

Remaining before this phase is done:
- Decide whether to use `VelopackApp.Build()`'s `OnFirstRun` / `OnRestarted` hooks for any
  first-launch or post-update messaging (out of scope for the spike; not yet designed).
- Confirm `MainViewModel.CheckForUpdateInBackgroundAsync` needs no changes — the
  `IUpdateCheckService` contract didn't change, so this should already be transparent, but
  verify no behavioral assumptions baked into that method break.
- Wire `ApplyUpdatesAndRestart`'s `restartArgs` to preserve `--profileDir` and `/debug` across an
  update-triggered restart, if the app ever calls that method directly rather than relying on
  `SetAutoApplyOnStartup(true)` (which applies pending updates before the next normal launch,
  where args come from the OS/shortcut, not from Velopack).
- `IUpdateCheckService` / `StubServices.cs`: no interface change expected, same as the Squirrel
  plan assumed — confirm existing tests still pass unmodified (spike confirms: yes, 797/797).

### Phase 2 — Packaging (spike-validated, needs CI wiring)

1. Install the `vpk` CLI as a .NET global tool: `dotnet tool install -g vpk`.
2. Replace the `installer` target in `build.bat`:
   ```bat
   :: Old: ISCC.exe installer\quickmail.iss
   :: New:
   vpk pack --packId QuickMail --packVersion %VERSION% --packDir publish\ ^
     --mainExe QuickMail.exe --packTitle "QuickMail" --packAuthors "Kelly Ford" ^
     --outputDir installer\Output\Releases
   ```
   `%VERSION%` must be resolved to a 3-part SemVer string per the versioning decision above —
   reading it straight from the exe's 4-part `FileVersion` (as the old `build.bat` did) will
   break again if `FileVersion` keeps a 4th segment; decide the exact source string during
   implementation.
3. Output confirmed in the spike: `installer\Output\Releases\` containing
   `QuickMail-X.Y.Z-full.nupkg`, `QuickMail-X.Y.Z-delta.nupkg` (from the second release onward),
   `QuickMail-win-Setup.exe`, `RELEASES`, `releases.win.json`, `assets.win.json`.
4. Per the portable-distribution decision above, `build.bat publish` continues to produce the
   raw `publish\QuickMail.exe` as today — that file, not `vpk pack`'s `-Portable.zip`, remains
   the portable download.

The Inno Setup script (`installer/quickmail.iss`) and `CodeDependencies.iss` can be retained for
now in case a fallback is needed, but are no longer part of the build process.

### Phase 3 — GitHub Actions release workflow

Current flow: `dotnet publish` → `ISCC.exe` → upload `quickmail-vX.Y.Z-setup.exe` +
`QuickMail.exe`.

New flow: `dotnet publish` → `vpk download github` (fetch the prior release's assets so
`vpk pack` can generate a delta) → `vpk pack` → `vpk upload github --publish` → also upload the
raw portable `QuickMail.exe` as today.

```yaml
- name: Pack Velopack release
  run: |
    dotnet tool install -g vpk
    vpk download github --repoUrl https://github.com/kellylford/QuickMail
    vpk pack --packId QuickMail --packVersion ${{ env.VERSION }} --packDir publish `
      --mainExe QuickMail.exe --outputDir installer\Output\Releases
    vpk upload github --repoUrl https://github.com/kellylford/QuickMail --publish `
      --releaseName "QuickMail v${{ env.VERSION }}" --tag v${{ env.VERSION }}
```

Needs `permissions: contents: write` on the job, same as today's release step. `vpk upload
github --publish` creates/publishes the GitHub Release directly — confirm this doesn't conflict
with or duplicate the existing `softprops/action-gh-release` step; the two may need to be
merged into one release action rather than run side by side.

### Phase 4 — Release notes + user guide

Unchanged in shape from the Squirrel plan:
- Release notes for the first Velopack-based version: explain the one-time reinstall, confirm
  data is preserved, mention passwords are safe.
- `docs/USER-GUIDE.md`: update the installation section.
- `docs/INSTALLER.md`: update to describe `vpk`-based packaging and output structure.

---

## Files to Modify

| File | Change | Status |
|------|--------|--------|
| `QuickMail/QuickMail.csproj` | Add `Velopack` package; flip `App.xaml` to `Page`; add `StartupObject` | Done in spike |
| `QuickMail/App.xaml.cs` | Add custom `Main` calling `VelopackApp.Build().Run()` | Done in spike |
| `QuickMail/Services/UpdateCheckService.cs` | Replace GitHub API implementation with `UpdateManager` | Done in spike |
| `QuickMail/Services/IUpdateCheckService.cs` | No interface change expected | Confirmed in spike |
| `build.bat` | Replace `installer` target with `vpk pack`; resolve 3-part version string | Not started |
| `.github/workflows/quickmail.yml` | Replace ISCC step with `vpk download`/`pack`/`upload`; reconcile with existing release-creation step | Not started |
| `docs/INSTALLER.md` | Update to describe `vpk`-based packaging | Not started |
| `docs/USER-GUIDE.md` | Update installation and update sections | Not started |
| `docs/release-notes-v0.x.x.md` | Migration note for the first Velopack release | Not started |

## Files Retired

| File | Fate |
|------|------|
| `installer/quickmail.iss` | Retired (keep in repo for reference until first Velopack release ships successfully) |
| `installer/CodeDependencies.iss` | Retired with the above |
| `installer/Languages/Custom.en.isl` | Retired with the above |

---

## Decisions & Scope Boundaries

- **Portable distribution stays the raw self-contained exe**, not Velopack's `-Portable.zip` —
  see "What changes from the original Squirrel plan" above.
- **Update is applied on next launch, not immediately.** Velopack downloads silently via
  `DownloadUpdatesAsync`; `SetAutoApplyOnStartup(true)` (Velopack's default) applies a pending
  update automatically the next time the app starts, before `App.OnStartup` runs. No in-session
  restart prompt needed unless added later.
- **Delta updates are automatic** from the second Velopack release onward, generated by
  `vpk pack` when a prior release is available locally via `vpk download github`. CI must run
  `vpk download` before `vpk pack` in every release or delta generation silently stops.
- **The `--profileDir` command-line flag** needs an explicit decision on whether shortcut-based
  launches carrying it survive an update cycle — flagged as unverified in Phase 1 and the
  Verification Checklist; do not assume it "just works" the way Squirrel's plan optimistically
  assumed for its own equivalent concern.
- **All-users install is not supported.** Velopack always installs per-user to `%LocalAppData%`,
  same limitation the Squirrel plan already accepted.
- **Out of scope:** WinGet package submission. Code signing (Velopack has native
  `--azureTrustedSignFile` support for Azure Trusted Signing — wire in once that work completes,
  but it is not required to ship this plan).

---

## Verification Checklist

1. **Local packaging dry run** — done in the spike: `vpk pack` against the real self-contained
   single-file publish output succeeds with a 3-part version, no manifest workaround needed.
2. Fresh install: run the Velopack `Setup.exe`, verify QuickMail launches, Start Menu/Desktop
   shortcuts present per the configured `--shortcuts` list, no elevation prompt.
3. Existing user migration: uninstall Inno Setup version (decline data deletion), install
   Velopack version, verify all accounts, mail, contacts, rules, and settings are intact and
   passwords work.
4. `--profileDir` user: verify custom profile path is respected after a Velopack install **and**
   after an update-triggered relaunch — this is the open question from the spike, not yet
   observed.
5. Update cycle: pack v+1, verify `CheckForUpdatesAsync` finds it, `DownloadUpdatesAsync`
   downloads it, and it applies automatically on next launch via `SetAutoApplyOnStartup`.
6. Delta update: verify a second release (after `vpk download github`) generates a delta nupkg
   and that update downloads use it (smaller than the full package).
7. Portable exe: verify the raw `publish\QuickMail.exe` (not Velopack's portable zip) continues
   to work unchanged, still shows the Help menu update notification via the GitHub API fallback.
8. Uninstall: verify Start Menu/Desktop shortcuts removed, `%LocalAppData%\QuickMail` removed,
   `%APPDATA%\QuickMail` untouched.
9. Uninstall + reinstall: verify user data survives the full cycle.
