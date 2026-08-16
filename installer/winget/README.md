# winget manifest for `KellyLford.QuickMail`

> [!IMPORTANT]
> **On hold — do not submit these manifests.** Winget distribution was stopped on
> 2026-08-16 before any release shipped the installer it points at. `Setup.exe` run over an
> existing MSI install leaves two Add/Remove Programs entries, and removing the stale one
> deletes the working install (measured; see the plan document and `docs/INSTALLER.md`).
> Every QuickMail user today installed from the MSI, so that is the path a winget install
> would take. These files stay as the finished starting point for whenever the underlying
> Velopack behaviour is fixed — issue #536 records what has to be true first.


Reference copy of the three manifests that describe QuickMail to the
[Windows Package Manager community repository](https://github.com/microsoft/winget-pkgs)
(issue #536, `docs/planning/winget-distribution-plan.md`). The **published** manifests live
in winget-pkgs under `manifests/k/KellyLford/QuickMail/<version>/`; the files here are the
template for the first, manual submission and the record of every deliberate choice in it.
After that first version merges, `.github/workflows/winget-publish.yml` opens the PR for
each promoted release automatically and these files are reference only.

## What the choices mean

- **`InstallerType: exe`, `--silent`** — the installer is Velopack's one-click
  `QuickMail-<version>-win-Setup.exe` / `-win-arm64-Setup.exe`, never the MSI. A silent MSI
  install lands in a drive root rather than `%LocalAppData%` — `C:\QuickMail` on one machine,
  `D:\QuickMail` on a CI runner, since Windows Installer picks the drive — and a silent MSI
  upgrade uninstalls the old copy (data prompt and all) before relocating the app — issue
  #554. `Setup.exe --silent` installs to
  `%LocalAppData%\QuickMail`, and run over an existing install it overwrites in place
  without invoking the uninstall hook.
- **`Scope: user`** — per-user, no elevation. Matches the wizard.
- **`UpgradeBehavior: install`** — winget just runs the newer Setup.exe; that *is* the
  upgrade. `uninstallPrevious` would run `Update.exe --uninstall` first and fire the
  data-removal prompt on every upgrade.
  Accept one consequence knowingly: Setup.exe force-stops a running instance before it
  overwrites. So `winget upgrade --all` — a routine, unattended thing to run — closes
  QuickMail if it is open, without asking. There is no better option (`uninstallPrevious`
  does the same *and* prompts about the user's data), but it belongs in the user guide's
  winget section rather than being discovered.
- **`AppsAndFeaturesEntries`** — Velopack writes `HKCU\…\Uninstall\QuickMail` with
  `DisplayName: QuickMail`, `Publisher: Kelly Ford` and a 3-part `DisplayVersion`; this is
  how `winget list`/`upgrade` correlate an installed copy to the package. The
  `ProductCode: QuickMail` line matters on any machine that came from the MSI: Setup.exe
  does not remove the MSI's `MSI:QuickMail` row, so two rows named QuickMail coexist and
  `winget list` shows the app twice (measured on both architectures — Phase 1b in
  `docs/planning/winget-distribution-plan.md`). winget's ProductCode for a non-MSI entry is
  the registry key name, so naming it picks Velopack's row and leaves the stale one alone.
- **`Moniker: quickmail`** — makes `winget install quickmail` resolve without the full id.
- **Two `Installers` entries** — x64 and arm64. Winget picks the native one.

## First submission (manual, once)

Prerequisite: a promoted release that carries both Setup.exe assets (0.8.41 onward — 0.8.40
predates PR #555).

1. Fill `<VERSION>`, `<X64-SHA256>`, `<ARM64-SHA256>` and `<RELEASE-DATE>` (YYYY-MM-DD)
   into copies of these three files in a folder named after the version. Hashes:
   `Get-FileHash <file> -Algorithm SHA256`, or `certutil -hashfile <file> SHA256`.
   `wingetcreate new <x64-url> <arm64-url>` produces the same files interactively if you
   would rather start from its prompts — but check its output against these choices.
2. `winget validate --manifest <folder>`
3. Install from the local manifest **in Windows Sandbox**, not on your own machine.
   winget-pkgs ships `Tools/SandboxTest.ps1` for exactly this. The reason to keep it out of
   a working machine is the package's own upgrade semantics: `Setup.exe --silent` overwrites
   an existing install in place, so testing it against the copy you use replaces that copy.
   Inside the Sandbox: `winget settings --enable LocalManifestFiles` (elevated, once),
   `winget install --manifest <folder>`, launch, then `winget uninstall quickmail`.
4. Test the **upgrade-from-MSI** path too, in a second Sandbox: install the release's MSI
   first (`msiexec /i <msi> /qn VELOPACK_INSTALLDIR="%LocalAppData%\QuickMail"` — the
   property must be on the command line, an environment variable does not reach the Windows
   Installer service), then `winget install --manifest <folder>` over it. Expect two rows in
   Settings → Apps and two `winget list` rows; confirm `winget upgrade` and
   `winget uninstall` act on the `ARP\...\QuickMail` row and not `ARP\...\MSI:QuickMail`.
   This is the state every existing user will be in, and it is the one thing the manifest's
   `ProductCode` exists to handle.
5. Fork microsoft/winget-pkgs, add the folder as
   `manifests/k/KellyLford/QuickMail/<version>/`, open the PR. Or from the folder:
   `wingetcreate submit --token <classic PAT with public_repo> <folder>`.
6. Expect the automated validation to run and a moderator to look at a first-time package;
   days, not hours.

## Every later release

Nothing to do by hand once `WINGET_TOKEN` is set: promoting a release fires the
`released` event and `winget-publish.yml` opens the winget-pkgs PR. Confirm it appeared.
