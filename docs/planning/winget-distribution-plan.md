# Winget Distribution — Plan

**Issue:** [#536 — Distribute QuickMail through winget](https://github.com/kellylford/QuickMail/issues/536)
**Status:** Phase 1 complete (2026-08-15), re-verified on CI across both architectures
(2026-08-16, Phase 1b). Approach revised: the winget package will install Velopack's
**Setup.exe**, not the MSI. Phase 2 (ship Setup.exe) is PR #555; the Phase 4 workflow and
the Phase 3 manifest template are PR #557; the CI harness is PR #560. Waiting on: merge of
those, then the 0.8.41 release, then the manual first submission.
**Date:** 2026-08-14, revised 2026-08-15 and 2026-08-16

## Summary

Make QuickMail installable with `winget install quickmail`, for both x64 and ARM64, by
publishing manifests to the community repository
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) that point at installers
attached to every GitHub release. Package identifier: **`KellyLford.QuickMail`** (a winget
search on 2026-08-14 confirms no existing QuickMail package, and `KellyBrazil.*` /
`KellyElton.*` publishers show the naming convention this follows).

The original draft assumed the MSI would be the winget installer. **Phase 1 testing
disproved that** (details below): a silent MSI install lands in a drive root — `C:\QuickMail`
on the ARM64 machine, `D:\QuickMail` on the x64 CI runner, since Windows Installer picks the
drive — and a silent MSI upgrade over an existing install uninstalls the old copy
— data-removal prompt and all — and relocates the app. Velopack's one-click `Setup.exe` does everything winget
needs correctly, so the plan now ships Setup.exe as a release asset and points the manifest
at it. The only code change is a few lines in the release workflow.

## Why it is worth doing

- **A fully keyboard-driven, screen-reader-friendly install path.** `winget install
  quickmail` in a terminal involves no browser, no download dialog, no wizard — a better
  first-run experience for QuickMail's core audience than any GUI installer can be.
- **Discoverability.** winget is where a growing share of Windows users (and virtually all
  developers and IT-adjacent users) look first. Being absent reads as "not maintained."
- **Scriptable setup.** Users rebuilding a machine restore their software with one winget
  import; QuickMail should be on that list.
- **It is nearly free.** Releases already publish permanent, versioned installer URLs on
  GitHub Releases; adding Setup.exe to that list is one workflow edit.

## Goals

1. `winget install quickmail` installs the current release on x64 and ARM64, per-user,
   without elevation, into `%LocalAppData%\QuickMail` — the same place the wizard installs.
2. `winget upgrade` behaves sanely alongside Velopack self-updates — no downgrade loops, no
   mid-upgrade data prompt, no relocation.
3. Every future release updates the winget manifest automatically from CI; the
   pre-release-then-promote cadence continues to work unchanged.
4. `winget uninstall` behaves identically to uninstalling from Settings → Apps, including
   the existing best-effort "remove user data?" prompt semantics (default: keep).

## Non-goals

- Publishing the portable executable as a winget *portable* package. The portable exe
  cannot apply updates (notify-only) and creates no Start Menu entry.
- Microsoft Store distribution (MSIX). Entirely different packaging and update model.
- Fixing the MSI's silent-install behavior in this plan. It is a real defect (tracked
  separately, see Phase 1 findings) but the winget path no longer depends on it.

## Phase 1 findings (2026-08-15, ARM64 machine, vpk 1.2.0 output)

Tested against the real v0.8.40 release MSIs plus two signed ARM64 `Setup.exe` builds from
the on-demand installer workflow (0.8.40-test.42.1 and 0.8.40-test.44.1). All installs
were silent and non-elevated. Registry state was verified from outside any app container.

### The MSI is not suitable as the winget installer

| Test | Result |
| --- | --- |
| `msiexec /i QuickMail-0.8.39-win-arm64.msi /qn` | Exit 0 in 4 s, no UI, no elevation — **but installed to `C:\QuickMail\`**, not `%LocalAppData%\QuickMail`. |
| Why | The wizard's Welcome→Next control event is what sets `INSTALLFOLDER = [LocalAppDataFolder]QuickMail`. `/qn` runs no dialogs, so `INSTALLFOLDER` falls back to the Directory table default `TARGETDIR\QuickMail`. The `SetINSTALLFOLDER` action only fires when `VELOPACK_INSTALLDIR` is passed. Velopack's docs say `/qn` should honor `--instLocation PerUser`; vpk 1.2.0 does not. Upstream Velopack defect. |
| `VELOPACK_INSTALLDIR="%LocalAppData%\QuickMail"` on the command line | Works — but the value is a literal path, so a per-user location cannot be expressed in a public winget manifest. |
| Silent 0.8.40 MSI over a wizard-style `%LocalAppData%` 0.8.39 install (what `winget upgrade` would do) | `RemoveExistingProducts` uninstalled the LocalAppData copy, **fired the "remove your data?" prompt mid-upgrade**, then installed 0.8.40 to `C:\QuickMail`. Confirms and extends the #245 finding that MSI-over-MSI is a two-phase uninstall/reinstall. |
| Add/Remove Programs rows | Two: the MSI's own hidden row (`ARPSYSTEMCOMPONENT=1`, HKLM, DisplayVersion `0.8.40.0`) and Velopack's visible `HKCU\...\Uninstall\MSI:QuickMail` (DisplayVersion `0.8.40`, `QuietUninstallString: msiexec /x {ProductCode} /qn`). winget correlates to the Velopack row (`ARP\User\Arm64\MSI:QuickMail`). |
| ProductCode / UpgradeCode | ProductCode changes every version and differs per architecture; UpgradeCode `{4F6E83C5-E7FB-5BBD-A3C3-6D78A4720D5E}` is stable across both. Upgrade table replaces older, blocks newer. Authoring is correct; the problem is location and the uninstall hook, not the upgrade logic. One consequence worth stating rather than leaving to be inferred: because the UpgradeCode is shared, the ARM64 MSI treats an x64 install as an older version of itself and replaces it, and vice versa. That is almost certainly the behaviour you want — it is the only way an MSI user changes architecture — but it is the opposite of the Velopack channels, where `INSTALLER.md` records that **no cross-architecture migration exists**. |
| Silent uninstall `msiexec /x {ProductCode} /qn` | Exit 0; ARP rows and Start Menu shortcut removed; data prompt appears (correct for a real uninstall). Leaves empty `current`/`packages` folders behind. |
| Post-patching the MSI in CI (e.g. WiX transform to fix the default directory) | Not viable: every MSI and exe in the release is Authenticode-signed via Azure Trusted Signing (`INSTALLER.md` still says signing is "not wired up" — it is), and a post-pack edit invalidates the signature. |

### Setup.exe does everything winget needs

| Test | Result |
| --- | --- |
| `Setup.exe --silent` fresh install | 4 s, no UI, no elevation, installs to `%LocalAppData%\QuickMail`, runs the `--veloapp-install` hook, does **not** launch the app (silent skips launch), creates the Start Menu shortcut, writes one ARP row. |
| ARP row | `HKCU\...\Uninstall\QuickMail`: DisplayName `QuickMail`, Publisher `Kelly Ford`, DisplayVersion `0.8.40` (3-part SemVer; prerelease suffix stripped), `UninstallString "…\Update.exe" --uninstall`, `QuietUninstallString "…\Update.exe" --uninstall --silent`, InstallLocation, NoModify/NoRepair. `winget list` shows `QuickMail  ARP\User\Arm64\QuickMail  0.8.40`. |
| `Setup.exe --silent` newer build over an existing install (the `winget upgrade` path) | 4 s. Velopack treats it as an overwrite (silent = yes), force-stops a running instance, renames the old folder for rollback, installs fresh, deletes the rollback folder. **Does not invoke the old app's uninstall hook — no data prompt.** Version advanced; ARP row rewritten. |
| `winget uninstall --name QuickMail --silent` | "Successfully uninstalled." winget ran the QuietUninstallString; ARP row and shortcuts gone; the data prompt appeared, as it should on a genuine uninstall. |
| Signing | The on-demand builds' Setup.exe files were Authenticode-signed by the same certificate as the release MSIs — vpk signs Setup.exe as part of `pack`, so the release workflow needs no extra signing step. |

Verified from Velopack source (`src/bins/src/commands/install.rs`, `setup.rs`,
`windows/registry.rs`): default directory is `LocalAppData\{packId}`; `--silent` answers
yes to the overwrite prompt and suppresses launch; the install hook runs in silent mode;
no version comparison is done (Setup.exe will happily overwrite with an older build).

That last point is not quite moot. `winget upgrade` never downgrades, but
`winget install --version <older>` installs exactly what it is asked for, and Phase 4 keeps
five older versions in the catalog (`max-versions-to-keep: 5`). A user who pins an older
version therefore gets a silent downgrade with no warning from either winget or Setup.exe,
and then Velopack self-updates them back up on the next launch. Nothing breaks; it is just
a confusing thing to watch happen, and worth knowing before someone reports it.

### What Phase 1 could not settle by hand

- **x64.** Every test above ran on ARM64.
- **Setup.exe over an MSI install.** Phase 1 covered Setup-over-Setup and MSI-over-MSI. It
  never covered the combination every existing user hits, because every existing user
  installed from the MSI and the winget package installs Setup.exe.
- **What winget itself sees.** The correlation claims above were read out of the registry
  and reasoned about, not observed from `winget list`.
- **Does DisplayVersion track Velopack self-updates?** Velopack has a dedicated
  `update_msi_uninstall_entry` and rewrites the ordinary entry via
  `write_uninstall_entry`; the code paths exist but were not exercised (that needs the app
  to run and self-update, which the test machine was not set up for). Check on the first
  real self-update after a winget install: if DisplayVersion lags, `winget upgrade` shows a
  stale "upgrade available" that is harmless but noisy.

The first three are now measured -- see below. The fourth still needs a running app.

## Phase 1b findings (CI, 2026-08-16) -- both architectures, measured

Hand-testing one machine does not scale to a matrix and cannot be re-run against a future
vpk, so the open questions above were turned into a workflow:
`.github/workflows/winget-install-matrix.yml` (PR #560). It packs two synthetic versions
with the release workflow's own `vpk pack` arguments and walks the install and upgrade
permutations on `windows-latest` (x64) and `windows-11-arm` (ARM64), snapshotting
Add/Remove Programs rows across all three hives, Windows Installer product registrations,
install directories, shortcuts, and `winget list` output after every step. Re-run it by
pushing a `test/winget-**` branch.

Numbers below are from run
[31958458925](https://github.com/kellylford/QuickMail/actions/runs/31958458925), vpk 1.2.0,
winget 1.11.510 (x64) and 1.29.280 (ARM64). Packages were unsigned, which does not affect
install location, ARP registration, or upgrade semantics.

### The migration path is not clean, and this is the one thing to fix before submitting

Starting state: MSI installed into `%LocalAppData%\QuickMail`, i.e. what the wizard
produces and what every existing user is running. Then `Setup.exe --silent` at a newer
version, which is exactly what `winget install` or `winget upgrade` runs.

| | Before (MSI only) | After `Setup.exe --silent` |
| --- | --- | --- |
| Visible ARP rows | 1 -- `MSI:QuickMail`, 1.0.0 | **2** -- `MSI:QuickMail` still 1.0.0, plus `QuickMail` 1.0.1 |
| Hidden HKLM MSI row | 1 | 1, still 1.0.0 |
| Windows Installer product registration | 1 | **1 -- survives** |
| `winget list` | one row, `ARP\User\X64\MSI:QuickMail  1.0.0` | **two rows**, `…\MSI:QuickMail 1.0.0` and `…\QuickMail 1.0.1` |

Identical on ARM64 (`ARP\User\Arm64\…`). Three consequences:

1. **Users see QuickMail twice in Settings > Apps**, at two different versions.
2. **The stale row is actively dangerous.** `MSI:QuickMail` keeps
   `QuietUninstallString: msiexec /x {ProductCode} /qn`, pointed at the same directory the
   new Velopack install now owns. Uninstalling the wrong one of the two rows tears files
   out from under a working install.
3. **The manifest's `AppsAndFeaturesEntries` matches both rows**, since both have
   `DisplayName: QuickMail`. Whichever winget picks, the other stays.

`Publisher` is `Kelly Ford` on both rows too, measured — so neither DisplayName nor Publisher
discriminates, and `ProductCode` is the only field that does.

The fix in the manifest is to correlate on the ARP key name rather than the display name:
winget's identifier for a non-MSI entry ends in that key (`ARP\User\X64\QuickMail`), so
`ProductCode: QuickMail` in `AppsAndFeaturesEntries` selects the Velopack row and excludes
`MSI:QuickMail`. That is in #557. It does not remove the second row -- nothing short of
Velopack cleaning up the MSI registration will -- but it stops winget acting on the wrong
one.

The reverse (MSI over a Setup.exe install, which a winget user who later downloads the MSI
would hit) is equally messy: 2 visible rows, same shape.

### Everything else measured clean

| Test | x64 | ARM64 |
| --- | --- | --- |
| `Setup.exe --silent` fresh | 1 ARP row, `%LocalAppData%\QuickMail`, Start Menu shortcut, exit 0 in ~5 s | same |
| `Setup.exe --silent` over Setup.exe | still exactly 1 row; DisplayVersion advances 1.0.0 -> 1.0.1; `winget list` shows one row at the new version | same |
| Quiet uninstall (`Update.exe --uninstall --silent`, what `winget uninstall` runs) | exit 0; 0 ARP rows, 0 install directories, 0 shortcuts, 0 product registrations | same |
| `vpk pack` output filenames | `QuickMail-win-Setup.exe`, `QuickMail-win.msi`, `QuickMail-win-Portable.zip`, `RELEASES`, `releases.win.json`, `assets.win.json`, `QuickMail-<v>-full.nupkg` | the same set, each suffixed `win-arm64` |

One caveat on the uninstall row, from the independent review of #560: a CI machine never has
a `%APPDATA%\QuickMail` profile, and `LaunchUninstallDataPrompt` returns early when there is
none. So the measurement proves the uninstall leaves nothing behind; it does **not** exercise
the detached "remove your data?" prompt, which is the part with interesting failure modes
(`INSTALLER.md` records it as best-effort and silently skipped on script-restricted
machines). Phase 1's ARM64 run did see that prompt appear. Deliberately not automated —
creating the profile directory on a runner would strand a hidden modal with nothing to
answer it. This is one of the cases issue #561 (a persistent test machine) exists for.

### Two corrections to the Phase 1 table above

- **The silent-MSI defect is not architecture-specific, and it is not literally `C:\`.**
  It reproduces on x64. The x64 runner installed to `D:\QuickMail` -- Windows Installer
  resolves the Directory table's `TARGETDIR` default to the drive it prefers, which was the
  ARM64 machine's `C:` and the x64 runner's `D:`. #554's title should read "a drive root",
  not "C:\QuickMail".
- **Both `Setup.exe` binaries are x86 PE images**, on both channels. Architecture therefore
  cannot be inferred from the binary at all -- only from the filename, and
  `QuickMail-<v>-win-Setup.exe` carries no architecture token while
  `QuickMail-<v>-win-arm64-Setup.exe` does. This matters only for Phase 4, where komac
  infers `Architecture:` for each asset; see the note in Phase 4.

### Still not settled

- **`winget upgrade` correlation.** It cannot be tested until `KellyLford.QuickMail` exists
  in the catalog -- with no package to compare an installed version against, `winget
  upgrade` has nothing to say. Re-run the matrix after the first submission merges.
- **DisplayVersion after a Velopack self-update**, unchanged from above: it needs the app
  to run and update itself, which the probe does not do.

## Phase 2 — Ship Setup.exe with every release (the one code change)

`vpk pack` already emits `QuickMail-win-Setup.exe` (x64) and
`QuickMail-win-arm64-Setup.exe` (ARM64) into `installer/Output/Releases/`; the release
workflow currently uploads the MSIs and discards them (`INSTALLER.md`: "produced by
`vpk pack` but not shipped"). Change `.github/workflows/quickmail.yml` to:

1. Rename each Setup.exe to include the version — `QuickMail-<version>-win-Setup.exe` /
   `QuickMail-<version>-win-arm64-Setup.exe` — the same way the MSIs are renamed today.
   Name the file rather than globbing for it. `build-installer.yml` renames "whichever
   single `*Setup.exe` landed" behind a count guard, but that guard cannot be reused here:
   the release workflow packs both channels into one output folder, so by the time the
   ARM64 pack runs the folder already holds the renamed x64 Setup.exe and the glob would
   match two files.
2. Add both to the `softprops/action-gh-release` file list.
3. Nothing else: Setup.exe is signed by vpk during pack, and the MSI, portable exe, and
   feed metadata continue to ship unchanged.

The MSI stays the download-page installer (wizard, license page). Setup.exe is what winget
consumes; it is also a perfectly good direct download for anyone who wants one-click.
`INSTALLER.md`'s asset table and its "Code signing is not wired up yet" line need updating
in the same PR.

**This change is not testable until a tag is pushed** — the release workflow only runs on
`v*` tags. The next release (0.8.41) is therefore the first that can be submitted to
winget. Alternatively, a workflow-dispatch dry run of the pack + upload steps against a
draft release would prove it earlier; judge whether that is worth building.

## Phase 3 — First manifest submission (manual, deliberately)

Do the first submission by hand to learn the pipeline before automating it:

1. Author the manifests with `wingetcreate new` against the release's Setup.exe URLs.
   Installer manifest shape:
   - `InstallerType: exe`
   - `Scope: user`
   - `InstallerSwitches: { Silent: --silent, SilentWithProgress: --silent }`
   - two `Installers` entries, `Architecture: x64` and `arm64`, each with its URL and SHA256
   - `AppsAndFeaturesEntries: [{ DisplayName: QuickMail, Publisher: Kelly Ford,
     ProductCode: QuickMail }]` so `winget upgrade` correlates the Velopack ARP row to the
     package. `ProductCode` is not optional decoration: on a machine upgraded from an MSI
     install there are two rows named QuickMail (Phase 1b), and for a non-MSI entry
     winget's ProductCode is the Add/Remove Programs key name — `QuickMail` for Velopack's
     row, `MSI:QuickMail` for the MSI's. Without it the manifest matches both.
   - `UpgradeBehavior: install` (default) — Setup.exe's overwrite is the upgrade; never
     `uninstallPrevious`, which would fire the data prompt on every upgrade.
   - Locale manifest: name, `Publisher: Kelly Ford`, description from README, `License:
     MIT`, `PackageUrl`/`PublisherSupportUrl` → repo, `Moniker: quickmail`, `Tags`.
2. Validate locally: `winget validate --manifest <dir>`, then a real install with
   `winget install --manifest <dir>` (requires enabling the `LocalManifestFiles` setting
   once, elevated). Run the winget-pkgs `SandboxTest.ps1` in Windows Sandbox for the
   clean-machine test — this is also the first truly clean verification of the WebView2
   install-on-demand path, and the x64 verification Phase 1 lacked.
3. Submit the PR to microsoft/winget-pkgs from Kelly's account (Phase 4's automation
   reuses that fork). Expect automated validation plus a human moderation pass on this
   first one; turnaround is typically days.
4. After merge, verify end-to-end on a machine that has never seen QuickMail:
   `winget search quickmail`, `winget install quickmail`, launch, add account, then
   `winget uninstall quickmail`.

## Phase 4 — Automate per-release manifest updates

Add a small workflow using the **winget-releaser** action (vedantmgoyal9/winget-releaser),
triggered on the release **`released`** event. That event fires both when a full release is
published *and when a pre-release is promoted to a release* — exactly the promote-based
cadence this repo uses. Pre-releases themselves never trigger it, so winget only ever sees
promoted builds.

- The action reads the release's assets, matches the two Setup.exe files by pattern,
  computes hashes, writes the new version folder, and opens the PR to
  microsoft/winget-pkgs from a fork.
- **Check the architecture on the first automated PR before letting it merge.** The action
  hands the assets to komac, which infers `Architecture:` per installer. Phase 1b measured
  both Setup.exe binaries as **x86** PE images, so the binary cannot disambiguate them;
  only the filename can, and `QuickMail-<v>-win-Setup.exe` carries no architecture token
  while `QuickMail-<v>-win-arm64-Setup.exe` does. If the x64 asset comes out as `x86` or
  disappears from the manifest, the fix is to put `x64` in the release asset's name (one
  line in `quickmail.yml`, plus the `installers-regex` in `winget-publish.yml`). The manual
  first submission is unaffected — it states both architectures outright.
- It needs a **classic PAT with `public_repo` scope** stored as a repo secret
  (fine-grained tokens cannot fork/PR to microsoft/winget-pkgs today). Token creation is
  Kelly's step; everything else is workflow YAML.
- Fallback if the action ever bit-rots: `wingetcreate update KellyLford.QuickMail
  --version <v> --urls <x64-Setup.exe> <arm64-Setup.exe> --submit` in a plain workflow
  step does the same thing.
- The release checklist gains one line: after promoting a release, confirm the
  winget-pkgs PR appeared and (eventually) merged. Nothing blocks the release on it.

## Phase 5 — Documentation

- **User guide** (`docs/USER-GUIDE.md`): add winget as an install option alongside the MSI
  download — the command, both architectures being automatic, and that updates continue to
  arrive through the app itself.
- **`docs/INSTALLER.md`**: Setup.exe now ships (asset table), signing *is* wired up, the
  manifest identifier, the ARP findings above, the automation, and the PAT secret's name
  and scope. Also correct the "installs to `%LocalAppData%`" claim to note that this holds
  for the wizard and for Setup.exe, but **not** for a silent MSI install (see the separate
  MSI issue).
- **README**: the `winget install quickmail` one-liner near the download links.
- **Release notes** for the first winget-available release mention the new install path.

## Update-interplay decision (was Phase 2)

Keep Velopack self-update as the primary update channel; winget is an acquisition channel.
A winget-installed QuickMail self-updates exactly like every other install. winget never
downgrades, so a manifest that lags the self-update channel by a release is safe, merely
stale. If DisplayVersion turns out not to track self-updates (see open item above),
decide then whether to patch it ourselves post-update.

## Sequencing and effort

| Phase | Depends on | Effort |
| --- | --- | --- |
| 1 — Verification | — | **Done 2026-08-15** |
| 2 — Ship Setup.exe in releases | a PR + the next tagged release | Small workflow change; proven only by the next release |
| 3 — First manifest + submission | first release carrying Setup.exe | A day, plus moderation wait (days) |
| 4 — CI automation | Phase 3 merged in winget-pkgs; PAT from Kelly | Small workflow change |
| 5 — Docs | Phase 3 live | Small |

## Open questions

1. **Q1 (resolved):** winget correlates to Velopack's own visible ARP row, not the MSI's
   hidden one. With Setup.exe there is only the one row.
2. **Q2:** If DisplayVersion does not track self-updates, patch it ourselves or accept the
   stale `winget upgrade` listing? Decide with data after the first real self-update of a
   winget-installed copy.
3. **Q3:** Publisher segment of the identifier: `KellyLford.QuickMail` is proposed to
   match the GitHub account. Confirm before the first submission — the identifier is
   permanent once merged.
4. **Q4 (resolved as to winget; not as to the MSI):** No upstream report needed. The MSI
   silent-install defect is
   [velopack/velopack#945](https://github.com/velopack/velopack/issues/945), closed
   2026-07-01 by a fix on master (`SetQuietDefaultInstallFolder` for `UILevel<5` in
   `MsiTemplate.hbs`), shipped so far only in the 1.2.110 prerelease. Tracked in #554,
   which also keeps the still-open MSI-over-MSI uninstall/prompt behavior (#245) in view.
   Winget is unaffected either way, since it installs Setup.exe.

   Do not read this as "the MSI fixes itself." NuGet still shows **1.2.0** as the newest
   stable `vpk` (checked 2026-08-16); every version above it, including 1.2.110, carries a
   prerelease suffix. There is no stable release with the fix to upgrade *to*, and no
   published date for one.
5. **Q5 (new):** Should `vpk` be pinned? The release workflow installs it with
   `dotnet tool install -g vpk`, unpinned, so a future release run silently packs with
   whatever version is newest that day. Two things in the tree currently pull in opposite
   directions on this: the workflow comment above the ARM64 pack step says "re-check this
   listing if vpk is ever upgraded — a collision here would corrupt the x64 feed and strand
   every existing install", which assumes upgrades are events somebody notices, while Q4
   above treats the silent upgrade as the delivery mechanism for a fix. Both cannot hold.

   Pinning and bumping deliberately looks like the right answer: it makes the feed-collision
   re-check possible, and it turns "wait for a stable Velopack" into a scheduled action
   rather than a hope. `.github/workflows/winget-install-matrix.yml` re-measures the packed
   output and the install paths on demand, so a bump can be verified before it ships rather
   than after.

## Out of scope

- Changing the installer technology, install location, or per-user decision.
- Any in-app UI or announcement changes. Installing via winget is indistinguishable from
  installing via Setup.exe once the app launches; first-run behavior (desktop-shortcut
  offer, tutorial) is unchanged.
- Publishing older versions retroactively to winget. The catalog starts at the first
  release that ships Setup.exe.
- The Inno-era migration path (`docs/INSTALLER.md`) — users on v0.7.9.1-or-earlier
  installs who choose winget simply follow the same uninstall-then-install step with
  `winget install` as step 2.
