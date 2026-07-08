# Installer & Automatic Updates

QuickMail is packaged with [Velopack](https://velopack.io/). `build.bat installer` runs
`dotnet publish` then `vpk pack`, emitting `installer/Output/Releases/` (gitignored)
containing:

- `QuickMail-win-Setup.exe` — the user-facing installer
- `QuickMail-<version>-full.nupkg` — the full update package consumed by the in-app updater
- `QuickMail-<version>-delta.nupkg` — binary delta from the previous release (generated only
  when the previous release's packages are present; CI fetches them with `vpk download github`
  before packing, local builds produce full packages only)
- `RELEASES`, `releases.win.json`, `assets.win.json` — release feed metadata read by the updater
- `QuickMail-win-Portable.zip` — **not shipped**; the raw single-file `publish/QuickMail.exe`
  remains the portable download

The `vpk` CLI is a .NET global tool: `dotnet tool install -g vpk`.

## Key facts

- **Per-user install only.** Velopack installs to `%LocalAppData%\QuickMail\current\`, no
  elevation prompt. There is no all-users install path.
- **Automatic updates.** The startup update check (`UpdateCheckService`) uses Velopack's
  `UpdateManager` with a GitHub Releases source when running from an installed copy. A found
  update is downloaded silently in the background and applied by `Update.exe` after the app
  exits, so the next launch runs the new version. The portable exe falls back to the original
  GitHub API check, which can only notify.
- **Version string must be SemVer.** `vpk pack --packVersion` rejects 4-part versions. The
  release tag is the source of truth in CI (stripped of the leading `v`) and must match the
  csproj `<Version>` — the workflow fails the release if they differ. A hotfix should use a
  SemVer2 prerelease tag (e.g. `0.8.1-1`), not a 4th segment.
- **Entry point requirement.** `vpk pack` verifies via IL inspection that `Main` calls
  `VelopackApp.Build().Run()`. That is why `App.xaml` compiles as `Page` and `App.xaml.cs`
  declares an explicit `Main` (see the csproj `StartupObject`). Removing or reordering that
  call breaks packaging.
- **Shortcuts:** Start Menu only (`--shortcuts StartMenuRoot`), matching the previous
  installer's default of no desktop shortcut.
- **No WebView2 prerequisite check.** The old Inno Setup installer installed the WebView2
  Runtime on demand; Velopack has no equivalent hook. WebView2 ships with Windows 11 and
  current Windows 10. If this becomes a support issue, add a startup check with a download
  link.
- **User data is never touched.** The app installs to `%LocalAppData%\QuickMail`; user data
  lives in `%APPDATA%\QuickMail` and Windows Credential Manager. Install, update, and
  uninstall never touch either.
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
5. `softprops/action-gh-release` uploads the portable `QuickMail.exe`, the setup exe, the
   `.nupkg` packages, and the feed metadata files. The in-app updater reads these from the
   latest GitHub release.

## Testing updates locally (no GitHub release needed)

The `--updateFeed <path>` startup flag points the update check at a local folder of
`vpk pack` output instead of GitHub Releases, so the complete cycle — check, background
download, apply on relaunch, delta packaging — can be verified offline:

1. `build.bat installer` — packs the current csproj version (say `0.8.1`) into
   `installer\Output\Releases\`.
2. Run `installer\Output\Releases\QuickMail-win-Setup.exe`. The app installs to
   `%LocalAppData%\QuickMail` and launches.
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

## Migrating from the Inno Setup installer

Users on an Inno Setup install (v0.8.0 and earlier) need a one-time manual reinstall to get
onto the auto-update track:

1. Uninstall QuickMail via Settings → Apps, **declining** the offer to delete user data.
2. Download and run `QuickMail-win-Setup.exe` from the releases page.
3. Launch QuickMail — accounts, settings, and mail are exactly as they were.

The retired Inno Setup script (`installer/quickmail.iss`, `installer/CodeDependencies.iss`,
`installer/Languages/Custom.en.isl`) is kept in the repo for reference until the first
Velopack release has shipped successfully.
