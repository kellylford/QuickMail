# winget manifest for `KellyLford.QuickMail`

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
  install lands in `C:\QuickMail` and a silent MSI upgrade uninstalls the old copy (data
  prompt and all) before relocating the app — issue #554. `Setup.exe --silent` installs to
  `%LocalAppData%\QuickMail`, and run over an existing install it overwrites in place
  without invoking the uninstall hook.
- **`Scope: user`** — per-user, no elevation. Matches the wizard.
- **`UpgradeBehavior: install`** — winget just runs the newer Setup.exe; that *is* the
  upgrade. `uninstallPrevious` would run `Update.exe --uninstall` first and fire the
  data-removal prompt on every upgrade.
- **`AppsAndFeaturesEntries`** — Velopack writes `HKCU\…\Uninstall\QuickMail` with
  `DisplayName: QuickMail`, `Publisher: Kelly Ford` and a 3-part `DisplayVersion`; this is
  how `winget list`/`upgrade` correlate an installed copy to the package.
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
3. `winget install --manifest <folder>` on a machine without QuickMail (needs the
   `LocalManifestFiles` setting enabled once, from an elevated prompt:
   `winget settings --enable LocalManifestFiles`). Then `winget uninstall quickmail`.
4. Fork microsoft/winget-pkgs, add the folder as
   `manifests/k/KellyLford/QuickMail/<version>/`, open the PR. Or from the folder:
   `wingetcreate submit --token <classic PAT with public_repo> <folder>`.
5. Expect the automated validation to run and a moderator to look at a first-time package;
   days, not hours.

## Every later release

Nothing to do by hand once `WINGET_TOKEN` is set: promoting a release fires the
`released` event and `winget-publish.yml` opens the winget-pkgs PR. Confirm it appeared.
