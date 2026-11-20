# installer/

Packaging inputs for QuickMail's Windows installer and its winget manifests. The installer is built
with [Velopack](https://velopack.io); **`docs/INSTALLER.md` is the full guide** to packaging,
auto-update and testing an update cycle locally.

## Building

From the repository root, with the Velopack CLI installed at the version pinned in
`.github/vpk-version` (`dotnet tool install -g vpk --version <that version>`):

```bat
build.bat installer
```

This publishes the self-contained single-file `QuickMail.exe` and runs `vpk pack`, which writes the
MSI installer and the update packages to `installer\Output\Releases\` (gitignored). A local build
names the MSI `QuickMail-win.msi`; the release workflow renames it to `QuickMail-<version>-win.msi`.

## Files

| Path | Purpose |
|---|---|
| `velopack/welcome.txt`, `velopack/conclusion.txt` | Text shown on the MSI wizard's first and last pages. |
| `velopack/license.txt` | Generated from `LICENSE` at build time (gitignored). |
| `winget/` | Template winget manifests; see `winget/README.md`. |
| `Output/` | Build output (gitignored). |

QuickMail was packaged with Inno Setup up to version 0.7.9.1. Those scripts were removed once the
Velopack installer had shipped; they remain in git history.
