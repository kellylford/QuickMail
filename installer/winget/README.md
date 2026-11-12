# winget manifest for `KellyLford.QuickMail`

> [!IMPORTANT]
> **Published.** microsoft/winget-pkgs#447170 merged on 2026-11-05 with 0.8.65, so
> `KellyLford.QuickMail` (moniker `quickmail`) is in the catalog. Every release since is
> submitted automatically by the release workflow (see
> [Every later release](#every-later-release)); a winget moderator still approves each one.

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
  the manifest. No `InstallerSwitches`: winget supplies the MSI's own, `/passive` by
  default (a progress window) and `/quiet` under `-h`.
- **`Scope: user`** — per-user, no elevation. The MSI has no `ALLUSERS`; it is per-user only.
- **`RequireExplicitUpgrade: true`** — QuickMail updates itself, and keeps its Add/Remove
  Programs version current while doing so, so winget seldom has an upgrade to offer. When it
  does, a newer MSI over an older one is a Windows Installer major upgrade that uninstalls the
  old copy first (#245, won't-fix upstream). That uninstall runs the *old* copy's uninstall
  hook. Copies from 0.8.53 on recognise the upgrade and do neither of the following; older
  copies do both, once, when they are the ones replaced:
  - **It offers to delete the user's data**, with a prompt saying QuickMail has been
    uninstalled. The prompt runs detached, so it can appear after the new version is already
    in place. The default answer keeps everything. Answering Yes, believing the message,
    deletes the profile and saved passwords of a working install.
  - **It removes the start-at-sign-in entry (#770)**, and the new install does not put it
    back.

  `RequireExplicitUpgrade` keeps all of that out of an unattended `winget upgrade --all`.
  `winget upgrade quickmail` still works when someone asks for it by name.
- **`UpgradeBehavior: install`** — winget runs the newer MSI, and Windows Installer's major
  upgrade is the upgrade. `uninstallPrevious` would add a second uninstall, and prompt, of
  its own.
- **`AppsAndFeaturesEntries` with `ProductCode: MSI:QuickMail` and `InstallerType: exe`** —
  that is Velopack's
  visible `HKCU\…\Uninstall\MSI:QuickMail` row, which winget lists as
  `ARP\User\<arch>\MSI:QuickMail`. Self-updates keep its `DisplayVersion` current. Measured
  on Kelly's machine, 2026-10-05: installed from the 0.8.44 MSI on 2026-09-03 and
  self-updated since, the running exe and this row both read 0.8.52, while the Windows
  Installer registration (`MsiGetProductInfo … VersionString`) still reads 0.8.44.0. **Do not add an
  `UpgradeCode` or an installer-level `ProductCode`**, even though `wingetcreate` adds both:
  they point winget at the Windows Installer registration, whose version is the one first
  installed and is never updated by self-update (#244). winget would then offer an upgrade
  that has already happened, forever.
  The entry's `InstallerType: exe` is needed because that row has no `WindowsInstaller=1`, so
  winget treats the installed copy as an `exe`. Without the entry saying so, winget refuses
  a `wix` installer over it as a different installer technology. That is read from
  winget-cli's source, not measured, and cannot be measured yet: `winget upgrade`, even with
  `--manifest`, finds the installed copy by looking the package Id up in the catalog
  sources, so before the package is published it reports "No installed package found"
  whatever the manifest says (install-matrix run 37274030522, verbose log). The line only
  widens what winget accepts, so it is safe either way. After the first version is
  published, run `winget upgrade quickmail` on a machine with an older release installed.
- **`Moniker: quickmail`** — makes `winget install quickmail` resolve without the full id.
- **Two `Installers` entries** — x64 and arm64. winget picks the native one.

## First submission (manual, once)

Prerequisite: Kelly's go. Any release from 0.8.48 on qualifies; use the newest.

1. Fill `<VERSION>`, `<X64-SHA256>`, `<ARM64-SHA256>` and `<RELEASE-DATE>` (YYYY-MM-DD)
   into copies of these three files in a folder named after the version. Hashes:
   `Get-FileHash <file> -Algorithm SHA256`. If you use `wingetcreate new` instead, delete the
   `UpgradeCode` and installer-level `ProductCode` it adds (see above).
2. `winget validate --manifest <folder>`. The template filled with 0.8.65's values passes. winget-pkgs rejects manifests whose `ManifestVersion` it has deprecated (1.10.0 was, on 2026-10-30), so move the schema lines and `ManifestVersion` to the version it names.
3. Install from the local manifest **in Windows Sandbox**, never on a working machine — an
   MSI upgrade over the copy you use is an uninstall and reinstall of it. winget-pkgs ships
   `Tools/SandboxTest.ps1` for this. Inside: `winget install --manifest <folder>`, launch,
   add an account, then check `winget list quickmail` shows one row at the manifest's
   version. Then `winget uninstall quickmail`. Expect a SmartScreen prompt before the install
   starts. On CI, SmartScreen engaged on QuickMail's MSI from a local manifest, and not on
   Node.js's installed the same way (plan, Phase 1d, scenario 7). Choose *More info* and then
   *Run anyway*.
4. Fork microsoft/winget-pkgs, add the folder as
   `manifests/k/KellyLford/QuickMail/<version>/`, open the PR — or
   `wingetcreate submit --token <classic PAT with public_repo> <folder>`.
5. Expect automated validation plus a moderator on a first-time package; days, not hours.
6. After it merges, on a machine that has never had QuickMail: `winget search quickmail`,
   `winget install quickmail`, launch, then let it self-update once and confirm
   `winget upgrade` still does not list it. **First, note whether a SmartScreen prompt
   appears** during `winget install`. From a local manifest it always did, and a control
   showed that was QuickMail's file reputation (plan, Phase 1d, scenario 7). From the catalog
   the download is in zone 2 instead of the Internet zone, and whether that avoids the
   prompt is unknown. **It may well appear.** If it does, record what it says and how a
   screen reader presents it. It would mean winget does not get around the SmartScreen
   problem #746 is about, and a `winget install` run unattended would wait on it.
7. **The upgrade, which cannot be tested before this point.** `winget upgrade` finds the
   installed copy through the catalog (see the `InstallerType: exe` note above), so it is
   only testable once a version is published. In a Sandbox: install the *previous* release's
   MSI, then launch it as `QuickMail.exe --updateFeed C:\empty` (an empty folder, so it
   finds no update to apply on exit), add an account, turn on start-at-sign-in, close it,
   and run `winget upgrade quickmail`. Listen for the "remove your data?" prompt: note
   when it appears and whether it takes focus, answer No, and check whether
   start-at-sign-in is still on afterwards. Not covered even then: a copy that
   **self-updated** before the MSI upgrade, so its files no longer match the old MSI's file
   table — the state winget will usually meet, and the hardest to set up on purpose.

## Every later release

Automatic. When a `v*` tag's release is published, the `winget` job in `quickmail.yml` calls
`.github/workflows/winget-submit.yml`, which:

1. skips the version if winget-pkgs already has it or a pull request for it is open;
2. downloads the release's two MSIs and hashes them;
3. writes the manifests with `scripts/winget-manifest.ps1`, which fills these templates,
   strips their comments, and refuses to write a manifest with an `UpgradeCode`, an
   installer-level `ProductCode`, `Setup.exe`, or `RequireExplicitUpgrade` off;
4. runs `scripts/winget-submit.ps1`, which builds one commit with the three files on top of
   winget-pkgs' current master, points a branch in Kelly's fork at it, and opens the pull
   request as `Update: KellyLford.QuickMail to <version>`. All through the GitHub API: the
   fork is never synced, because syncing it after upstream changes its workflows needs the
   `workflow` scope, which the token does not have. It never pushes over a branch that
   already has an open pull request, which may carry fixes a moderator asked for.

A moderator then approves it; 0.8.71's took about five hours. A failed submission leaves the
release untouched. Rerun the job, or run **Submit to winget** by hand with the version. Only
Kelly can run it for real, since the pull request is opened in his name; with **dry_run** on
(the default), anyone can, and it only shows the manifests it would submit.

`scripts/winget-manifest.tests.ps1` runs on every pull request (the `winget-manifest` job in
`quickmail.yml`), so a template or script change that breaks a rule fails there rather than
after a release.

Deliberately not winget-releaser, Komac or `wingetcreate update`: they analyse the MSI and
add the `UpgradeCode` and `ProductCode` this package must not have (see above). The script is
the only way manifests are made, by hand or in CI:

```
pwsh scripts/winget-manifest.ps1 -Version <v> -X64Sha256 <hash> -Arm64Sha256 <hash> -ReleaseDate <YYYY-MM-DD> -OutDir <folder>
winget validate --manifest <folder>
```

Filled with 0.8.71's values, it writes manifests byte-identical to the ones merged for 0.8.71.

**Setup (once).** The workflow needs `WINGET_TOKEN` in the `winget` environment (deployment
branches `main` and `v*` tags): a classic personal access token of Kelly's with only the
`public_repo` scope. A fine-grained token cannot open a pull request on microsoft/winget-pkgs.
Classic tokens expire; when it does, the job fails with GitHub's "Bad credentials" and needs a
new token in the same place.

`public_repo` is not narrow. It is write access to every public repository Kelly owns,
QuickMail included, and his admin role bypasses both the `main` ruleset and "Tags: admins
only". A leaked copy could push a `v*` tag or push to `main`, which is what the other
environments' protection rests on. What keeps it in: only `main` and `v*` tag runs get it,
both of which need Kelly's review or hands to change; the job runs nothing but checkout,
the runner's own `gh`, and two code-owned scripts; and no step prints it.
