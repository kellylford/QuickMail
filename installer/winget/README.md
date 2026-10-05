# winget manifest for `KellyLford.QuickMail`

> [!IMPORTANT]
> **Ready, not yet submitted.** The first submission to microsoft/winget-pkgs is a manual
> step that waits for Kelly's go: it publishes to a public catalog, and the identifier is
> permanent once merged. Nothing in this repository submits anything automatically.

Reference copy of the three manifests that describe QuickMail to the
[Windows Package Manager community repository](https://github.com/microsoft/winget-pkgs)
(issue #536, `docs/planning/winget-distribution-plan.md`). The **published** manifests live
in winget-pkgs under `manifests/k/KellyLford/QuickMail/<version>/`; the files here are the
template for the first, manual submission and the record of every deliberate choice in it.

## History, in one paragraph

The first version of this template pointed at Velopack's `Setup.exe`, because in vpk 1.2.0 a
silent MSI install landed in a drive root (#554). It was stopped on 2026-08-16 before
reaching a release: `Setup.exe` run over an MSI install leaves two Add/Remove Programs rows,
and removing the stale one deletes the working install. vpk 1.2.158 fixed the MSI's quiet
install location upstream (velopack#945, PR #970), and releases from 0.8.48 on were packed
with it. Measured on both architectures (install-matrix run 37268689330): a silent MSI now
installs to `%LocalAppData%\QuickMail` with one visible row, exactly like the wizard. So the
package now installs **the MSI**, which meets every existing user's machine as the same kind
of install it already has.

## What the choices mean

- **`InstallerType: wix`, the release MSI** — the same `QuickMail-<version>-win.msi` /
  `-win-arm64.msi` the download page offers. Never `Setup.exe` (above). Both MSIs carry the
  right `Template` architecture (`x64`, `Arm64`), so winget's architecture check agrees with
  the manifest. No `InstallerSwitches`: winget passes `/quiet /norestart` itself.
- **`Scope: user`** — per-user, no elevation. The MSI has no `ALLUSERS`; it is per-user only.
- **`RequireExplicitUpgrade: true`** — QuickMail updates itself, and keeps its Add/Remove
  Programs version current while doing so, so winget seldom has an upgrade to offer. When it
  does, a newer MSI over an older one is a Windows Installer major upgrade that uninstalls the
  old copy first, firing the "remove your data?" prompt mid-upgrade (#245, won't-fix; default
  keep). This keeps that out of an unattended `winget upgrade --all`.
  `winget upgrade quickmail` still works when someone asks for it by name.
- **`UpgradeBehavior: install`** — winget runs the newer MSI, and Windows Installer's major
  upgrade is the upgrade. `uninstallPrevious` would add a second uninstall, and prompt, of
  its own.
- **`AppsAndFeaturesEntries` with `ProductCode: MSI:QuickMail` only** — that is Velopack's
  visible `HKCU\…\Uninstall\MSI:QuickMail` row, which winget lists as
  `ARP\User\<arch>\MSI:QuickMail`. Self-updates keep its `DisplayVersion` current. Measured
  on Kelly's machine, 2026-10-05: installed from the 0.8.44 MSI on 2026-09-03 and
  self-updated since, the running exe and this row both read 0.8.52, while the Windows
  Installer registration (`MsiGetProductInfo … VersionString`) still reads 0.8.44.0. **Do not add an
  `UpgradeCode` or an installer-level `ProductCode`**, even though `wingetcreate` adds both:
  they point winget at the Windows Installer registration, whose version is the one first
  installed and is never updated by self-update (#244). winget would then offer an upgrade
  that has already happened, forever.
- **`Moniker: quickmail`** — makes `winget install quickmail` resolve without the full id.
- **Two `Installers` entries** — x64 and arm64. winget picks the native one.

## First submission (manual, once)

Prerequisite: Kelly's go. Any release from 0.8.48 on qualifies; use the newest.

1. Fill `<VERSION>`, `<X64-SHA256>`, `<ARM64-SHA256>` and `<RELEASE-DATE>` (YYYY-MM-DD)
   into copies of these three files in a folder named after the version. Hashes:
   `Get-FileHash <file> -Algorithm SHA256`. If you use `wingetcreate new` instead, delete the
   `UpgradeCode` and installer-level `ProductCode` it adds (see above).
2. `winget validate --manifest <folder>`. The template filled with 0.8.52's values passes.
3. Install from the local manifest **in Windows Sandbox**, never on a working machine — an
   MSI upgrade over the copy you use is an uninstall and reinstall of it. winget-pkgs ships
   `Tools/SandboxTest.ps1` for this. Inside: `winget install --manifest <folder>`, launch,
   add an account, then check `winget list quickmail` shows one row at the manifest's
   version and `winget upgrade` does not list it. Then `winget uninstall quickmail`.
4. Fork microsoft/winget-pkgs, add the folder as
   `manifests/k/KellyLford/QuickMail/<version>/`, open the PR — or
   `wingetcreate submit --token <classic PAT with public_repo> <folder>`.
5. Expect automated validation plus a moderator on a first-time package; days, not hours.
6. After it merges, on a machine that has never had QuickMail: `winget search quickmail`,
   `winget install quickmail`, launch, then let it self-update once and confirm
   `winget upgrade` still does not list it.

## Every later release

Nothing publishes automatically yet. Once the first version is in the catalog, plan Phase 4
describes the per-release workflow (winget-releaser on the `released` event, a classic PAT
as `WINGET_TOKEN`), now matching the two `.msi` assets instead of `Setup.exe`. Until then a
new version is the same steps 1, 2 and 4 by hand — or `wingetcreate update
KellyLford.QuickMail --version <v> --urls <x64-msi-url> <arm64-msi-url> --submit`, checking
the same two fields afterwards.
