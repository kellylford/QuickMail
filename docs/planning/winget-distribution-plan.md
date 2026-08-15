# Winget Distribution — Plan

**Issue:** [#536 — Distribute QuickMail through winget](https://github.com/kellylford/QuickMail/issues/536)
**Status:** Phase 1 complete (2026-08-15). Approach revised: the winget package will
install Velopack's **Setup.exe**, not the MSI. Phase 2 (ship Setup.exe) is PR #555; the
Phase 4 workflow and the Phase 3 manifest template are PR #557. Waiting on: merge of
both, then the 0.8.41 release, then the manual first submission.
**Date:** 2026-08-14, revised 2026-08-15

## Summary

Make QuickMail installable with `winget install quickmail`, for both x64 and ARM64, by
publishing manifests to the community repository
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) that point at installers
attached to every GitHub release. Package identifier: **`KellyLford.QuickMail`** (a winget
search on 2026-08-14 confirms no existing QuickMail package, and `KellyBrazil.*` /
`KellyElton.*` publishers show the naming convention this follows).

The original draft assumed the MSI would be the winget installer. **Phase 1 testing
disproved that** (details below): a silent MSI install lands in `C:\QuickMail`, and a
silent MSI upgrade over an existing install uninstalls the old copy — data-removal prompt
and all — and relocates the app. Velopack's one-click `Setup.exe` does everything winget
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
| ProductCode / UpgradeCode | ProductCode changes every version and differs per architecture; UpgradeCode `{4F6E83C5-E7FB-5BBD-A3C3-6D78A4720D5E}` is stable across both. Upgrade table replaces older, blocks newer. Authoring is correct; the problem is location and the uninstall hook, not the upgrade logic. |
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
no version comparison is done (Setup.exe will happily overwrite with an older build —
winget never offers downgrades, so this is moot in practice).

### Two things Phase 1 could not settle

- **Does DisplayVersion track Velopack self-updates?** Velopack has a dedicated
  `update_msi_uninstall_entry` and rewrites the ordinary entry via
  `write_uninstall_entry`; the code paths exist but were not exercised (that needs the app
  to run and self-update, which the test machine was not set up for). Check on the first
  real self-update after a winget install: if DisplayVersion lags, `winget upgrade` shows a
  stale "upgrade available" that is harmless but noisy.
- **x64.** Every test above ran on ARM64. Nothing in the findings is architecture-specific
  (the MSI tables and Velopack code are identical), but the first x64 verification should
  happen during Phase 3's Windows Sandbox run.

## Phase 2 — Ship Setup.exe with every release (the one code change)

`vpk pack` already emits `QuickMail-win-Setup.exe` (x64) and
`QuickMail-win-arm64-Setup.exe` (ARM64) into `installer/Output/Releases/`; the release
workflow currently uploads the MSIs and discards them (`INSTALLER.md`: "produced by
`vpk pack` but not shipped"). Change `.github/workflows/quickmail.yml` to:

1. Rename each Setup.exe to include the version — `QuickMail-<version>-win-Setup.exe` /
   `QuickMail-<version>-win-arm64-Setup.exe` — the same way the MSIs are renamed today
   (and the same way `build-installer.yml` already renames its Setup.exe; copy that
   snippet, including its "exactly one Setup.exe" guard).
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
   - `AppsAndFeaturesEntries: [{ DisplayName: QuickMail, Publisher: Kelly Ford }]` so
     `winget upgrade` correlates the Velopack ARP row to the package
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
4. **Q4 (resolved):** No upstream report needed. The MSI silent-install defect is
   [velopack/velopack#945](https://github.com/velopack/velopack/issues/945), closed
   2026-07-01 by a fix on master (`SetQuietDefaultInstallFolder` for `UILevel<5` in
   `MsiTemplate.hbs`), shipped so far only in the 1.2.110 prerelease. The release
   workflow installs the latest *stable* vpk, so the MSI is fixed automatically by the
   next stable Velopack. Tracked in #554, which also keeps the still-open MSI-over-MSI
   uninstall/prompt behavior (#245) in view. Winget is unaffected either way.

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
