# Installer & Automatic Updates

QuickMail is packaged with [Velopack](https://velopack.io/). `build.bat installer` runs
`dotnet publish` then `vpk pack`, emitting `installer/Output/Releases/` (gitignored)
containing:

- `QuickMail-win.msi` — **the user-facing installer**: a standard Windows Installer wizard
  (WiX 5) with welcome, license acceptance, and conclusion pages fed from
  `installer/velopack/*.txt` and the repo `LICENSE`
- `QuickMail-win-Setup.exe` — Velopack's one-click installer (no wizard, no license page);
  produced by `vpk pack` but **not shipped** — the MSI is the installer users download
- `QuickMail-<version>-full.nupkg` — the full update package consumed by the in-app updater
- `QuickMail-<version>-delta.nupkg` — binary delta from the previous release (generated only
  when the previous release's packages are present; CI fetches them with `vpk download github`
  before packing, local builds produce full packages only)
- `RELEASES`, `releases.win.json`, `assets.win.json` — release feed metadata read by the updater
- `QuickMail-win-Portable.zip` — **not shipped**; the raw single-file `publish/QuickMail.exe`
  remains the portable download

The `vpk` CLI is a .NET global tool: `dotnet tool install -g vpk`.

## Key facts

- **Per-user install only (deliberate).** The MSI is packed with `--instLocation PerUser`:
  installs to `%LocalAppData%\QuickMail\current\`, no elevation prompt. Velopack supports
  `--instLocation Either` (user chooses Program Files), but background updates from a
  non-elevated process into Program Files are unverified — offering that choice could break
  the silent-update guarantee, so it stays off until proven.
- **Automatic updates.** The startup update check (`UpdateCheckService`) uses Velopack's
  `UpdateManager` with a GitHub Releases source when running from an installed copy. A found
  update is downloaded silently in the background and applied by `Update.exe` after the app
  exits, so the next launch runs the new version. Updates work identically whether the MSI
  or Setup.exe performed the original install. The portable exe falls back to the original
  GitHub API check, which can only notify.
- **Desktop shortcut is the app's job, not the installer's.** `--shortcuts StartMenuRoot`
  creates only the Start Menu entry. On first launch of an installed copy, QuickMail offers
  to add a desktop shortcut (accessible in-app dialog, default No, asked once — recorded as
  `DesktopShortcutPrompted` in config.ini); the choice stays editable in Settings → General.
  The shortcut targets `%LocalAppData%\QuickMail\current\QuickMail.exe`, which is stable
  across updates. See `Helpers/DesktopShortcut.cs`.
- **Uninstall offers to remove user data.** Velopack's `OnBeforeUninstallFastCallback` (wired
  in `App.xaml.cs Main`) launches a detached PowerShell prompt — detached because Update.exe
  kills hook processes after 30 seconds, far too short to leave a question pending. Default
  answer keeps everything; an explicit Yes deletes `%APPDATA%\QuickMail` **and** QuickMail
  entries in Windows Credential Manager (passwords `QuickMail:<id>`, tokens `QuickMail.*`).
  Custom `--profileDir` locations are never touched. Data survival on uninstall-without-Yes
  matches the old Inno behavior. The prompt is **best-effort**: on script-restricted machines
  (AppLocker, Constrained Language Mode) it may never appear, in which case data is simply
  kept — the safe default. The hook and the prompt script log to
  `%TEMP%\quickmail-uninstall.log` for diagnosis.
- **WebView2 install-on-demand** is preserved via `--framework webview2` — setup installs the
  WebView2 Runtime when missing, same as the old Inno `Dependency_AddWebView2`.
- **Version string must be SemVer.** `vpk pack --packVersion` rejects 4-part versions. The
  release tag is the source of truth in CI (stripped of the leading `v`) and must match the
  csproj `<Version>` — the workflow fails the release if they differ. A hotfix should use a
  SemVer2 prerelease tag (e.g. `0.8.1-1`), not a 4th segment.
- **Entry point requirement.** `vpk pack` verifies via IL inspection that `Main` calls
  `VelopackApp.Build().Run()`. That is why `App.xaml` compiles as `Page` and `App.xaml.cs`
  declares an explicit `Main` (see the csproj `StartupObject`). Removing or reordering that
  call breaks packaging.
- **Code signing** is not wired up yet. `vpk pack` supports Azure Trusted Signing via
  `--azureTrustedSignFile` when that work completes.

## Release flow (CI)

On a `v*` tag, `.github/workflows/quickmail.yml`:

1. Verifies the tag version matches the csproj `<Version>`.
2. `dotnet publish` (unchanged single-file self-contained build).
3. `vpk download github` — fetches the previous release's packages so a delta can be built.
   Allowed to fail (the first Velopack release has no prior packages).
4. `vpk pack` — builds setup exe, full/delta packages, and feed metadata; then deletes the
   downloaded previous-version `.nupkg` so only current-version assets upload.
5. `softprops/action-gh-release` uploads the portable `QuickMail.exe`, the MSI installer, the
   `.nupkg` packages, and the feed metadata files. The in-app updater reads these from the
   latest GitHub release.

## Testing updates locally (no GitHub release needed)

The `--updateFeed <path>` startup flag points the update check at a local folder of
`vpk pack` output instead of GitHub Releases, so the complete cycle — check, background
download, apply on relaunch, delta packaging — can be verified offline:

1. `build.bat installer` — packs the current csproj version (say `0.8.1`) into
   `installer\Output\Releases\`.
2. Run `installer\Output\Releases\QuickMail-win.msi`. Walk the wizard (welcome, license,
   conclusion); the app installs to `%LocalAppData%\QuickMail`. First launch offers the
   desktop shortcut.
3. Bump `<Version>` in `QuickMail/QuickMail.csproj` (say `0.8.2`) and run
   `build.bat installer` again **without deleting the Releases folder** — because the
   previous full package is still there, this also exercises delta generation
   (`QuickMail-0.8.2-delta.nupkg` appears).
4. Launch the **installed** copy with the feed override:
   `%LocalAppData%\QuickMail\current\QuickMail.exe --updateFeed <repo>\installer\Output\Releases`
   The startup check finds 0.8.2, announces it, and downloads it in the background
   (`quickmail.log` records "Update 0.8.2 downloaded; it will be applied when QuickMail
   exits").
5. Exit QuickMail. `Update.exe` applies the staged update.
6. Relaunch (Start Menu or the same command). Help menu now reads "running version 0.8.2".

Add `--profileDir <scratch>` to any of these launches to keep test runs away from real
data. Uninstalling via Settings → Apps removes `%LocalAppData%\QuickMail` and shortcuts;
revert the csproj version bump afterwards.

The flag only overrides *where packages come from*; everything else (Velopack's installed
detection, staging, `Update.exe` apply) runs exactly the production code path. What it
cannot test: the GitHub Releases fetch itself (`GithubSource`) and the CI packing steps —
those are exercised by the first real tagged release.

## Testing the live GitHub update path (one-time, two real releases)

The local `--updateFeed` cycle above proves everything except the one link that only runs in
production: Velopack's `GithubSource` resolving the latest release and downloading its assets
from GitHub. That link cannot be exercised until **two** published releases exist, because
there is nothing for the first release to update *to*. Do this once — it proves the update
channel for every future release. A broken updater is self-perpetuating (stranded clients
cannot be auto-fixed), so verify it before you ever rely on it to ship an urgent fix.

Run it on your own machine before trusting it broadly:

1. **Publish the baseline (0.8.0).** Confirm the tag equals all three csproj version
   properties, then push the tag: `git tag v0.8.0 && git push origin v0.8.0`. CI builds and
   creates the release.
2. **Verify the release assets by eye.** On the GitHub release page, confirm all four updater
   files are attached: `QuickMail-0.8.0-full.nupkg`, `RELEASES`, `releases.win.json`,
   `assets.win.json` (plus the `QuickMail-win.msi` and portable `QuickMail.exe`). A missing or
   malformed feed is the most likely failure and the one that strands clients *silently*.
3. **Install the baseline from the real release.** Download and run `QuickMail-win.msi` from
   the release page (not a local build) so the installed copy is exactly what users get.
4. **Publish an update (0.8.1).** Bump `<Version>`, `<AssemblyVersion>`, and `<FileVersion>`
   in `QuickMail/QuickMail.csproj`, add `docs/release-notes-v0.8.1.md`, commit, then
   `git tag v0.8.1 && git push origin v0.8.1`. The 0.8.1 release **must be a normal published
   release, not a draft or prerelease** — the client runs `GithubSource(..., prerelease: false)`
   and will not see a prerelease tag.
5. **Watch the installed 0.8.0 update itself.** Launch the installed copy **with no
   `--updateFeed` flag** (add `/debug` to get log detail). The startup check should find 0.8.1
   and download it in the background — `quickmail.log` records
   `Update 0.8.1 downloaded; it will be applied when QuickMail exits`. Exit, relaunch, and
   confirm Help → About reads 0.8.1.
6. **If nothing happens:** re-launch with `/debug` and read `quickmail.log`; failure reasons
   are logged via `LogService.Debug`. Then recheck step 2 — a bad feed file is the usual cause.

**Do not delete/reuse the test 0.8.1.** Deleting a published release your test machine already
updated to leaves that machine pointing at gone assets, and reusing the `0.8.1` number later
muddles the feed metadata and delta chain. Just keep 0.8.1 as a legitimate (even trivial)
release and let your next work be 0.8.2. The first *delta* package is only exercised on the
0.8.1 → 0.8.2 hop; the 0.8.0 → 0.8.1 update ships a full package, which is expected.

## Migrating from the Inno Setup installer

Users on an Inno Setup install (v0.7.9.1 and earlier) need a one-time manual reinstall to get
onto the auto-update track:

1. Uninstall QuickMail via Settings → Apps, **declining** the offer to delete user data.
2. Download and run `QuickMail-win.msi` from the releases page.
3. Launch QuickMail — accounts, settings, and mail are exactly as they were.

The retired Inno Setup script (`installer/quickmail.iss`, `installer/CodeDependencies.iss`,
`installer/Languages/Custom.en.isl`) is kept in the repo for reference until the first
Velopack release has shipped successfully.
