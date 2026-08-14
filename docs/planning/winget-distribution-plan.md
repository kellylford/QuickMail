# Winget Distribution — Plan

**Issue:** [#536 — Distribute QuickMail through winget](https://github.com/kellylford/QuickMail/issues/536)
**Status:** Proposed
**Date:** 2026-08-14

## Summary

Make QuickMail installable with `winget install quickmail`, for both x64 and ARM64, by
publishing manifests to the community repository
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) that point at the MSI
installers already attached to every GitHub release. Package identifier:
**`KellyLford.QuickMail`** (a winget search on 2026-08-14 confirms no existing QuickMail
package, and `KellyBrazil.*` / `KellyElton.*` publishers show the naming convention this
follows).

No application code changes are expected. The work is verification of the existing
installer's unattended behavior, one investigation into Add/Remove Programs registration,
a decision about how winget upgrades coexist with Velopack self-updates, the first manual
manifest submission, and then CI automation so every future release updates the manifest
without manual work.

## Why it is worth doing

- **A fully keyboard-driven, screen-reader-friendly install path.** `winget install
  quickmail` in a terminal involves no browser, no download dialog, no wizard — a better
  first-run experience for QuickMail's core audience than any GUI installer can be.
- **Discoverability.** winget is where a growing share of Windows users (and virtually all
  developers and IT-adjacent users) look first. Being absent reads as "not maintained."
- **Scriptable setup.** Users rebuilding a machine restore their software with one winget
  import; QuickMail should be on that list.
- **It is nearly free.** Releases already publish permanent, versioned MSI URLs for both
  architectures on GitHub Releases — exactly the shape winget manifests require.

## Goals

1. `winget install quickmail` installs the current release on x64 and ARM64, per-user,
   without elevation, matching what the MSI does when run by hand.
2. `winget upgrade` behaves sanely alongside Velopack self-updates — no downgrade loops, no
   permanently stale "upgrade available" entries.
3. Every future release updates the winget manifest automatically from CI; the
   pre-release-then-promote cadence continues to work unchanged.
4. `winget uninstall` behaves identically to uninstalling from Settings → Apps, including
   the existing best-effort "remove user data?" prompt semantics (default: keep).

## Non-goals

- Publishing the portable executable as a winget *portable* package. The portable exe
  cannot apply updates (notify-only) and creates no Start Menu entry; the MSI is the
  install experience we support. Revisit only if users ask.
- Microsoft Store distribution (MSIX). Entirely different packaging and update model;
  separate decision, separate plan.
- Code signing. Winget does not require signed installers — manifests pin the installer's
  SHA256, and the validation pipeline runs its own malware scans. Unsigned binaries may
  still trigger SmartScreen for *direct* downloads, but winget installs bypass that UX.
  Signing remains tracked with the Azure Trusted Signing work in `docs/INSTALLER.md`, not
  here.

## How winget works (the moving parts)

A winget package is a set of small YAML manifests in the community repo, per version:

```
manifests/k/KellyLford/QuickMail/0.8.40/
  KellyLford.QuickMail.yaml                 # version manifest (identifier + version)
  KellyLford.QuickMail.locale.en-US.yaml    # name, publisher, description, license, URLs
  KellyLford.QuickMail.installer.yaml       # per-architecture installer URLs + SHA256
```

The installer manifest lists both architectures in one file:

- `Architecture: x64` → `QuickMail-<version>-win.msi`
- `Architecture: arm64` → `QuickMail-<version>-win-arm64.msi`

with `InstallerType: wix`, `Scope: user`, and each installer's SHA256. New versions are
new folders, submitted as PRs to microsoft/winget-pkgs. The first PR for a new package
goes through automated validation (schema, URL reachability, hash match, an actual install
in their pipeline, Defender scan) plus human moderation; subsequent version bumps are
mostly automated on their side.

Two facts shape the plan:

- **winget requires unattended install.** For MSIs it appends standard `msiexec` quiet
  switches; the installer must complete with no UI and no elevation prompt (our MSI is
  per-user by design, so elevation should not arise — but "should" is Phase 1's job).
- **`winget list` / `winget upgrade` correlate packages to Add/Remove Programs entries**
  by ProductCode, DisplayName, and DisplayVersion. Whatever our ARP entry looks like —
  and Velopack installs have both a Velopack-written uninstall entry and possibly the
  MSI's own registration — determines whether upgrades are detected correctly.

## Phase 1 — Verify unattended install and map the ARP registration

All investigation, no code. Run against a real release's MSI (not a local pack), on a
scratch profile.

1. **Silent install:** `msiexec /i QuickMail-0.8.x-win.msi /qn` from a non-elevated
   prompt. Confirm: exit code 0, app lands in `%LocalAppData%\QuickMail\current\`, Start
   Menu entry exists, no UAC prompt, no window ever appears. This is the exact contract
   winget's pipeline tests.
2. **Silent uninstall:** `msiexec /x <ProductCode> /qn`. Confirm it completes unattended.
   Note what happens to the Velopack uninstall hook (the detached "remove user data?"
   prompt): if it appears even under `/qn` that is acceptable for interactive
   `winget uninstall`, but record the behavior — the safe default (keep data) must hold
   when the prompt cannot appear.
3. **Map the ARP entries.** After an MSI install, enumerate
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall` (and HKLM, to prove nothing
   lands there): does the machine end up with one entry or two (MSI registration +
   Velopack's own)? Record DisplayName, DisplayVersion, ProductCode, Publisher for each.
   Two visible entries would also mean users see QuickMail twice in Settings → Apps —
   worth knowing regardless of winget.
4. **Does DisplayVersion track self-updates?** Install version N via MSI, let Velopack
   self-update to N+1, re-read the ARP entry. If DisplayVersion now reads N+1, winget's
   upgrade detection stays truthful for self-updated installs. If it still reads N,
   `winget upgrade` will forever offer an "upgrade" to a version already installed.
5. **Does ProductCode change per release?** Diff the ProductCode across two releases'
   MSIs (`msiexec` logs, or Orca/PowerShell against the MSI property table). Winget
   manifests carry the ProductCode per version; WiX-generated codes are normally
   per-build, which is fine — the manifest is per-version too — but we need to know.
6. **MSI-over-self-updated-install:** install N via MSI, self-update to N+1, then run the
   N+2 MSI (what `winget upgrade` will do). Confirm the result is a healthy N+2 install
   with one ARP entry and a working updater. This is the highest-risk interaction in the
   whole plan and must be proven before submission, not after.

Findings land in this document. If step 6 misbehaves, the fallback is
`UpgradeBehavior: uninstallPrevious` in the manifest (winget uninstalls old, installs
new — settings survive because the profile lives in `%APPDATA%`, per
`docs/INSTALLER.md`), which trades a slower upgrade for a clean one.

## Phase 2 — Decide the auto-update interplay

Recommendation: **keep Velopack self-update as the primary update channel; winget is an
acquisition channel.** A winget-installed QuickMail self-updates exactly like every other
install. This is the pattern most self-updating apps in winget use, and the alternative —
detecting a winget install and disabling the updater — makes winget users second-class
(they would wait on manifest PRs for every fix) for no benefit.

Consequences to accept and document:

- If Phase 1 finds DisplayVersion *does* track self-updates: `winget upgrade` simply shows
  nothing for QuickMail most of the time, because the app is already current. Ideal.
- If it does *not*: `winget upgrade` will list QuickMail with a stale installed-version.
  Running the upgrade is harmless (Phase 1 step 6 proves it) but noisy. If we land here,
  filing a Velopack issue or writing the DisplayVersion ourselves post-update is the fix —
  decide then, with data.
- winget never downgrades by default, so a manifest that lags the self-update channel by a
  release is safe, merely stale.

## Phase 3 — First manifest submission (manual, deliberately)

Do the first submission by hand to learn the pipeline before automating it:

1. Author the three manifests with `wingetcreate new` against the current release's asset
   URLs; fill the locale manifest from the repo (description from README, license MIT,
   `PackageUrl`/`PublisherSupportUrl` → repo, `Moniker: quickmail` — this is what makes
   the short `winget install quickmail` resolve).
2. Validate locally: `winget validate --manifest <dir>`, then an actual install with
   `winget install --manifest <dir>` (requires enabling the `LocalManifestFiles` setting
   once, from an elevated prompt). Run the winget-pkgs `SandboxTest.ps1` in Windows
   Sandbox for the clean-machine test — it catches missing-dependency assumptions
   (WebView2 install-on-demand via `--framework webview2` gets its first truly clean
   verification here).
3. Submit the PR to microsoft/winget-pkgs from Kelly's account (the fork the automation
   in Phase 4 will reuse). Expect automated validation plus a human moderation pass on
   this first one; turnaround is typically days.
4. After merge, verify end-to-end on a machine that has never seen QuickMail:
   `winget search quickmail`, `winget install quickmail`, launch, add account, then
   `winget uninstall` and confirm the data-removal prompt semantics.

## Phase 4 — Automate per-release manifest updates

Add a small workflow (or a job in `.github/workflows/quickmail.yml`) using the
**winget-releaser** action (vedantmgoyal9/winget-releaser), triggered on the release
**`released`** event. That event fires both when a full release is published *and when a
pre-release is promoted to a release* — which is exactly the promote-based cadence this
repo uses (see the release-cadence practice: pre-release first, promote after
verification). Pre-releases themselves never trigger it, so winget only ever sees
promoted builds.

Details:

- The action reads the release's assets, matches the two MSIs by pattern, computes hashes,
  writes the new version folder, and opens the PR to microsoft/winget-pkgs from a fork.
- It needs a **classic PAT with `public_repo` scope** stored as a repo secret (fine-grained
  tokens cannot fork/PR to microsoft/winget-pkgs today). Token creation is Kelly's step;
  everything else is workflow YAML.
- Fallback if the action ever bit-rots: `wingetcreate update KellyLford.QuickMail
  --version <v> --urls <x64.msi> <arm64.msi> --submit` in a plain workflow step does the
  same thing.
- The release checklist gains one line: after promoting a release, confirm the
  winget-pkgs PR appeared and (eventually) merged. Nothing blocks the release on it.

## Phase 5 — Documentation

- **User guide** (`docs/USER-GUIDE.md`): add winget as an install option alongside the MSI
  download — the command, both architectures being automatic, and that updates continue to
  arrive through the app itself.
- **`docs/INSTALLER.md`**: new section recording the manifest identifier, the ARP findings
  from Phase 1, the automation, and the PAT secret's name and scope.
- **README**: the `winget install quickmail` one-liner near the download links.
- **Release notes** for the first winget-available release mention the new install path.

## Sequencing and effort

| Phase | Depends on | Effort |
| --- | --- | --- |
| 1 — Silent install + ARP investigation | a published release's MSIs | Half a day of hands-on testing |
| 2 — Update-interplay decision | Phase 1 findings | Small; mostly writing the decision down |
| 3 — First manifest + submission | Phases 1–2 | A day, plus moderation wait (days) |
| 4 — CI automation | Phase 3 merged in winget-pkgs; PAT from Kelly | Small workflow change |
| 5 — Docs | Phase 3 live | Small |

## Open questions

1. **Q1:** If Phase 1 finds two ARP entries (MSI + Velopack), which one do we point
   winget's `AppsAndFeaturesEntries` at, and is the duplicate worth fixing in packaging
   regardless? (Needs Phase 1 data.)
2. **Q2:** If DisplayVersion does not track self-updates, do we patch it ourselves after
   each applied update, or accept the stale `winget upgrade` listing? (Needs Phase 1
   data; leaning "patch it" if Velopack exposes a clean hook.)
3. **Q3:** Publisher segment of the identifier: `KellyLford.QuickMail` is proposed to
   match the GitHub account. Confirm before the first submission — the identifier is
   permanent once merged.

## Out of scope

- Changing the installer technology, install location, or per-user decision — winget
  consumes the MSI as-is.
- Any in-app UI or announcement changes. Installing via winget is indistinguishable from
  installing via the MSI once the app launches; first-run behavior (desktop-shortcut
  offer, tutorial) is unchanged.
- Publishing older versions retroactively to winget. The catalog starts at the first
  submitted release.
- The Inno-era migration path (`docs/INSTALLER.md`) — users on v0.7.9.1-or-earlier
  installs who choose winget simply follow the same uninstall-then-install step with
  `winget install` as step 2.
